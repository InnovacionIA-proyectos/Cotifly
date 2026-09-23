"""Sincronización PostgreSQL → Google Sheets para el tablero de Looker Studio.

El bot deja toda su trazabilidad en Postgres; Gerencia quiere un tablero en Looker
Studio. Looker no lee Postgres directo en este entorno, pero sí una Google Sheet.
Este módulo **vuelca** la BD a varias hojas de un mismo libro, con la forma que un
tablero necesita (una tabla de hechos por cotización + tablas relacionadas +
agregados listos para tarjetas KPI):

- `Cotizaciones`        → una fila por cotización (tabla de hechos del tablero).
- `Ajustes_Margen`      → cada ajuste de margen hecho por un comercial.
- `Correos`             → cada envío de correo (con su folio).
- `Actividad_Comercial` → agregado por comercial (vista v_actividad_por_comercial).
- `Uso_Herramientas`    → uso agregado de cada tool del MCP (volumen, éxito, latencia).
- `KPIs`                → indicadores globales (clave/valor) para las tarjetas.

Diseño:
- Las funciones `_tabla_*` son **puras** (reciben una `Session`, devuelven filas):
  se prueban sin Google. El `SheetsServicio` (escritura real) es una interfaz
  inyectable; la implementación real `GoogleSheetsServicio` usa la Sheets API con
  el mismo service account del tarifario (extra `[sheets]`).
- `sincronizar()` construye todas las tablas y las vuelca en una sola pasada.

Requisitos operativos (ver infra/GOOGLE-SETUP.md):
1. Habilitar la **Google Sheets API** en el proyecto GCP del service account.
2. Compartir el libro destino con el correo del service account como **Editor**.
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from collections.abc import Iterable
from datetime import UTC, datetime
from decimal import Decimal
from pathlib import Path
from typing import Any

from sqlalchemy import text
from sqlalchemy.orm import Session

from shared.config import settings

# ---------------------------------------------------------------------------
# Utilidades de formato: dejar cada celda apta para Sheets/Looker.
# ---------------------------------------------------------------------------

# Fila = lista de celdas; celda = str | int | float (nunca None en el volcado).
Celda = str | int | float
Fila = list[Celda]
Tabla = tuple[list[str], list[Fila]]  # (encabezados, filas)


def _num(v: Any) -> float | str:
    """Numérico a float (Decimal incluido); None → "" para no romper la API."""
    if v is None:
        return ""
    if isinstance(v, Decimal):
        return float(v)
    if isinstance(v, (int, float)):
        return float(v)
    try:
        return float(v)
    except (TypeError, ValueError):
        return ""


def _pct(v: Any) -> float | str:
    """Margen decimal (0.135) → porcentaje redondeado (13.5) para el tablero."""
    n = _num(v)
    return round(n * 100, 2) if isinstance(n, float) else ""


def _txt(v: Any) -> str:
    return "" if v is None else str(v)


def _fecha(dt: datetime | None) -> str:
    return dt.strftime("%Y-%m-%d") if dt else ""


def _fecha_hora(dt: datetime | None) -> str:
    return dt.strftime("%Y-%m-%d %H:%M:%S") if dt else ""


def _mes(dt: datetime | None) -> str:
    return dt.strftime("%Y-%m") if dt else ""


def _anio(dt: datetime | None) -> str:
    return dt.strftime("%Y") if dt else ""


def _resultado(ganada: bool | None) -> str:
    """Etiqueta legible para el tablero a partir de `ganada`."""
    if ganada is True:
        return "Ganada"
    if ganada is False:
        return "Perdida"
    return "Sin definir"


def _lista(v: Any) -> str:
    """ARRAY de Postgres (para/cc) → cadena separada por '; '."""
    if not v:
        return ""
    if isinstance(v, (list, tuple)):
        return "; ".join(str(x) for x in v)
    return str(v)


# ---------------------------------------------------------------------------
# Constructores de tablas (puros: Session → (encabezados, filas)).
# ---------------------------------------------------------------------------

_SQL_COTIZACIONES = text(
    """
    SELECT
        c.folio, c.created_at, c.estado_costeo, c.estado_envio, c.usuario_id,
        c.linea_negocio, c.cliente_nombre, c.cliente_id_sap,
        c.costo, c.margen_politica, c.margen_aplicado, c.precio_final, c.moneda,
        c.metodo_calculo, c.origen_margen,
        c.ganada, c.motivo_rechazo,
        COALESCE(aj.n, 0)  AS num_ajustes,
        COALESCE(co.n, 0)  AS num_correos,
        COALESCE(co.ok, 0) AS num_correos_enviados
    FROM cotizaciones c
    LEFT JOIN (
        SELECT cotizacion_id, COUNT(*) AS n
        FROM ajustes_margen WHERE cotizacion_id IS NOT NULL
        GROUP BY cotizacion_id
    ) aj ON aj.cotizacion_id = c.id
    LEFT JOIN (
        SELECT cotizacion_id, COUNT(*) AS n,
               COUNT(*) FILTER (WHERE estado = 'enviado') AS ok
        FROM correos_enviados GROUP BY cotizacion_id
    ) co ON co.cotizacion_id = c.id
    ORDER BY c.created_at
    """
)

_COTIZACIONES_COLS = [
    "Folio", "Fecha", "Fecha_Hora", "Mes", "Año", "Estado Costeo", "Estado Envío",
    "Resultado", "Usuario ID", "Línea de Negocio", "Cliente", "Cliente ID SAP",
    "Costo", "Margen Política %", "Margen Aplicado %", "Precio Final",
    "Utilidad", "Margen Real %", "Moneda",
    "Método Cálculo", "Origen Margen",
    "N° Ajustes", "N° Correos", "N° Correos Enviados", "Motivo Rechazo",
]


def _tabla_cotizaciones(session: Session) -> Tabla:
    # `Usuario ID`, `Estado Costeo`: sin poblar hasta HU-004/HU-005 (ver HU-002.2).
    # `Origen`/`Destino`/`Fuente Tarifa` se eliminaron: ese detalle vivirá en Nexo.
    filas: list[Fila] = []
    for r in session.execute(_SQL_COTIZACIONES).mappings():
        costo = _num(r["costo"])
        precio = _num(r["precio_final"])
        utilidad = (
            round(precio - costo, 2)
            if isinstance(precio, float) and isinstance(costo, float)
            else ""
        )
        margen_real = (
            round((utilidad / precio) * 100, 2)
            if isinstance(utilidad, float) and isinstance(precio, float) and precio
            else ""
        )
        filas.append(
            [
                _txt(r["folio"]),
                _fecha(r["created_at"]),
                _fecha_hora(r["created_at"]),
                _mes(r["created_at"]),
                _anio(r["created_at"]),
                _txt(r["estado_costeo"]),
                _txt(r["estado_envio"]),
                _resultado(r["ganada"]),
                _txt(r["usuario_id"]),
                _txt(r["linea_negocio"]),
                _txt(r["cliente_nombre"]),
                _txt(r["cliente_id_sap"]),
                costo,
                _pct(r["margen_politica"]),
                _pct(r["margen_aplicado"]),
                precio,
                utilidad,
                margen_real,
                _txt(r["moneda"]) or "COP",
                _txt(r["metodo_calculo"]),
                _txt(r["origen_margen"]),
                int(r["num_ajustes"] or 0),
                int(r["num_correos"] or 0),
                int(r["num_correos_enviados"] or 0),
                _txt(r["motivo_rechazo"]),
            ]
        )
    return _COTIZACIONES_COLS, filas


_SQL_AJUSTES = text(
    """
    SELECT folio, created_at, usuario_id,
           margen_politica, margen_aplicado, diferencia, motivo
    FROM ajustes_margen ORDER BY created_at
    """
)
_AJUSTES_COLS = [
    "Folio", "Fecha", "Usuario ID",
    "Margen Política %", "Margen Aplicado %", "Diferencia %", "Motivo",
]


def _tabla_ajustes(session: Session) -> Tabla:
    filas: list[Fila] = []
    for r in session.execute(_SQL_AJUSTES).mappings():
        filas.append(
            [
                _txt(r["folio"]),
                _fecha_hora(r["created_at"]),
                _txt(r["usuario_id"]),
                _pct(r["margen_politica"]),
                _pct(r["margen_aplicado"]),
                _pct(r["diferencia"]),
                _txt(r["motivo"]),
            ]
        )
    return _AJUSTES_COLS, filas


_SQL_CORREOS = text(
    """
    SELECT co.created_at, c.folio, co.confirmado_por,
           co.para, co.cc, co.asunto, co.estado, co.message_id, co.error
    FROM correos_enviados co
    LEFT JOIN cotizaciones c ON c.id = co.cotizacion_id
    ORDER BY co.created_at
    """
)
_CORREOS_COLS = [
    "Fecha", "Folio", "Confirmado Por", "Para", "CC",
    "Asunto", "Estado", "Message ID", "Error",
]


def _tabla_correos(session: Session) -> Tabla:
    filas: list[Fila] = []
    for r in session.execute(_SQL_CORREOS).mappings():
        filas.append(
            [
                _fecha_hora(r["created_at"]),
                _txt(r["folio"]),
                _txt(r["confirmado_por"]),
                _lista(r["para"]),
                _lista(r["cc"]),
                _txt(r["asunto"]),
                _txt(r["estado"]),
                _txt(r["message_id"]),
                _txt(r["error"]),
            ]
        )
    return _CORREOS_COLS, filas


_SQL_ACTIVIDAD = text(
    """
    SELECT usuario_id, num_cotizaciones, num_cotizaciones_enviadas,
           num_ajustes, ultima_cotizacion
    FROM v_actividad_por_comercial
    ORDER BY num_cotizaciones DESC
    """
)
_ACTIVIDAD_COLS = [
    "Usuario ID", "N° Cotizaciones", "N° Enviadas", "N° Ajustes", "Última Cotización",
]


def _tabla_actividad(session: Session) -> Tabla:
    filas: list[Fila] = []
    for r in session.execute(_SQL_ACTIVIDAD).mappings():
        filas.append(
            [
                _txt(r["usuario_id"]),
                int(r["num_cotizaciones"] or 0),
                int(r["num_cotizaciones_enviadas"] or 0),
                int(r["num_ajustes"] or 0),
                _fecha_hora(r["ultima_cotizacion"]),
            ]
        )
    return _ACTIVIDAD_COLS, filas


_SQL_HERRAMIENTAS = text(
    """
    SELECT herramienta,
           COUNT(*)                               AS total,
           COUNT(*) FILTER (WHERE exito IS TRUE)  AS exitosos,
           COUNT(*) FILTER (WHERE exito IS FALSE) AS fallidos,
           AVG(duracion_ms)                       AS dur_prom,
           MAX(created_at)                        AS ultimo_uso
    FROM eventos_herramienta
    GROUP BY herramienta
    ORDER BY total DESC
    """
)
_HERRAMIENTAS_COLS = [
    "Herramienta", "Invocaciones", "Exitosas", "Fallidas",
    "Duración Prom (ms)", "Último Uso",
]


def _tabla_herramientas(session: Session) -> Tabla:
    filas: list[Fila] = []
    for r in session.execute(_SQL_HERRAMIENTAS).mappings():
        dur = _num(r["dur_prom"])
        filas.append(
            [
                _txt(r["herramienta"]),
                int(r["total"] or 0),
                int(r["exitosos"] or 0),
                int(r["fallidos"] or 0),
                round(dur, 1) if isinstance(dur, float) else "",
                _fecha_hora(r["ultimo_uso"]),
            ]
        )
    return _HERRAMIENTAS_COLS, filas


_SQL_KPIS = text(
    """
    SELECT
        COUNT(*)                                              AS total,
        COUNT(*) FILTER (WHERE estado_envio = 'enviada')      AS enviadas,
        COUNT(*) FILTER (WHERE ganada IS TRUE)                AS ganadas,
        COUNT(*) FILTER (WHERE ganada IS FALSE)               AS perdidas,
        COUNT(*) FILTER (WHERE ganada IS NULL)                AS sin_marcar,
        AVG(precio_final)                                     AS ticket_prom,
        SUM(precio_final - costo)                             AS utilidad_total,
        AVG(margen_aplicado)                                  AS margen_prom,
        COUNT(DISTINCT usuario_id)                            AS comerciales
    FROM cotizaciones
    """
)
_KPIS_COLS = ["Indicador", "Valor"]


def _tabla_kpis(session: Session, ahora: datetime) -> Tabla:
    r = session.execute(_SQL_KPIS).mappings().first() or {}
    total = int(r.get("total") or 0)
    ganadas = int(r.get("ganadas") or 0)
    marcadas = ganadas + int(r.get("perdidas") or 0)
    tasa = round((ganadas / marcadas) * 100, 2) if marcadas else 0
    filas: list[Fila] = [
        ["Total de cotizaciones", total],
        ["Cotizaciones enviadas", int(r.get("enviadas") or 0)],
        ["Cotizaciones ganadas", ganadas],
        ["Cotizaciones perdidas", int(r.get("perdidas") or 0)],
        ["Cotizaciones sin definir", int(r.get("sin_marcar") or 0)],
        ["Tasa de éxito %", tasa],
        ["Ticket promedio", _num(r.get("ticket_prom"))],
        ["Utilidad total", _num(r.get("utilidad_total"))],
        ["Margen aplicado promedio %", _pct(r.get("margen_prom"))],
        ["Comerciales activos", int(r.get("comerciales") or 0)],
        ["Última actualización", ahora.strftime("%Y-%m-%d %H:%M:%S")],
    ]
    return _KPIS_COLS, filas


def construir_tablas(session: Session, ahora: datetime | None = None) -> dict[str, list[Fila]]:
    """Arma todas las hojas del tablero (nombre de hoja → matriz con encabezado)."""
    ahora = ahora or datetime.now(UTC)
    fuentes = {
        "KPIs": _tabla_kpis(session, ahora),
        "Cotizaciones": _tabla_cotizaciones(session),
        "Ajustes_Margen": _tabla_ajustes(session),
        "Correos": _tabla_correos(session),
        "Actividad_Comercial": _tabla_actividad(session),
        "Uso_Herramientas": _tabla_herramientas(session),
    }
    return {nombre: [list(cols), *filas] for nombre, (cols, filas) in fuentes.items()}


# ---------------------------------------------------------------------------
# Escritura en Google Sheets (interfaz inyectable + implementación real).
# ---------------------------------------------------------------------------


class SheetsServicio(ABC):
    """Escritura mínima que necesita el volcado. Inyectable para pruebas."""

    @abstractmethod
    def volcar(self, spreadsheet_id: str, hojas: dict[str, list[Fila]]) -> None:
        """Reemplaza por completo el contenido de cada hoja (crea la que falte)."""


class SheetsNoDisponible(RuntimeError):
    """El extra [sheets] no está instalado o el service account no está configurado."""


def _cargar_credenciales(sa_json: str, scopes: Iterable[str]) -> Any:  # pragma: no cover
    """Credenciales del service account desde ruta a archivo o JSON en línea."""
    import json

    from google.oauth2 import service_account

    ruta = Path(sa_json)
    if ruta.exists():
        return service_account.Credentials.from_service_account_file(
            str(ruta), scopes=list(scopes)
        )
    return service_account.Credentials.from_service_account_info(
        json.loads(sa_json), scopes=list(scopes)
    )


class GoogleSheetsServicio(SheetsServicio):  # pragma: no cover - requiere red + credenciales
    """Escritura real sobre la Google Sheets API v4 (extra `[sheets]`)."""

    _SCOPES = ("https://www.googleapis.com/auth/spreadsheets",)

    def __init__(self, google_sa_json: str) -> None:
        self._sa = google_sa_json
        self._svc: Any = None

    def _servicio(self) -> Any:
        if self._svc is not None:
            return self._svc
        try:
            from googleapiclient.discovery import build
        except ImportError as e:
            raise SheetsNoDisponible(
                'La sincronización a Sheets requiere el extra: pip install -e ".[sheets]"'
            ) from e
        creds = _cargar_credenciales(self._sa, self._SCOPES)
        self._svc = build("sheets", "v4", credentials=creds, cache_discovery=False)
        return self._svc

    def _titulos_existentes(self, sid: str) -> set[str]:
        meta = (
            self._servicio()
            .spreadsheets()
            .get(spreadsheetId=sid, fields="sheets.properties.title")
            .execute()
        )
        return {s["properties"]["title"] for s in meta.get("sheets", [])}

    def volcar(self, spreadsheet_id: str, hojas: dict[str, list[Fila]]) -> None:
        svc = self._servicio().spreadsheets()
        existentes = self._titulos_existentes(spreadsheet_id)

        # 1) Crear las hojas que aún no existen (una sola batchUpdate).
        nuevas = [t for t in hojas if t not in existentes]
        if nuevas:
            svc.batchUpdate(
                spreadsheetId=spreadsheet_id,
                body={"requests": [{"addSheet": {"properties": {"title": t}}} for t in nuevas]},
            ).execute()

        # 2) Limpiar el contenido previo de las hojas destino.
        svc.values().batchClear(
            spreadsheetId=spreadsheet_id, body={"ranges": list(hojas)}
        ).execute()

        # 3) Escribir cada matriz desde A1 (RAW: números como números, sin reinterpretar).
        svc.values().batchUpdate(
            spreadsheetId=spreadsheet_id,
            body={
                "valueInputOption": "RAW",
                "data": [
                    {"range": f"'{titulo}'!A1", "values": matriz}
                    for titulo, matriz in hojas.items()
                ],
            },
        ).execute()


def construir_servicio_sheets() -> SheetsServicio:
    """Fábrica del servicio real; exige service account configurado."""
    if not settings.google_sa_json:
        raise SheetsNoDisponible(
            "Falta GOOGLE_SA_JSON: no hay credenciales para escribir en Sheets."
        )
    return GoogleSheetsServicio(settings.google_sa_json)


def sincronizar(
    session: Session,
    servicio: SheetsServicio | None = None,
    spreadsheet_id: str | None = None,
    ahora: datetime | None = None,
) -> dict[str, int]:
    """Vuelca toda la BD al libro de Sheets. Devuelve {hoja: n_filas_de_datos}."""
    sid = spreadsheet_id or settings.sheets_spreadsheet_id
    if not sid:
        raise SheetsNoDisponible("Falta SHEETS_SPREADSHEET_ID: no se sabe a qué libro escribir.")
    servicio = servicio or construir_servicio_sheets()
    hojas = construir_tablas(session, ahora)
    servicio.volcar(sid, hojas)
    # n_filas de datos = matriz menos la fila de encabezado.
    return {nombre: max(0, len(matriz) - 1) for nombre, matriz in hojas.items()}
