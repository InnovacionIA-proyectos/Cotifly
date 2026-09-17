"""Pruebas de la sincronización BD → Google Sheets (tablero Looker Studio).

- Formateadores puros: sin BD ni Google.
- Volcado completo: sobre Postgres real (requiere_pg) con un `SheetsServicio` falso
  que captura las hojas, verificando estructura y datos derivados (utilidad, %…).
"""

from __future__ import annotations

from datetime import UTC, datetime
from decimal import Decimal

from shared import sheets_sync
from shared.sheets_sync import (
    SheetsServicio,
    _efectividad,
    _lista,
    _num,
    _pct,
    construir_tablas,
    sincronizar,
)
from tests.conftest import requiere_pg


class _SheetsFake(SheetsServicio):
    """Captura el último volcado para inspeccionarlo en las aserciones."""

    def __init__(self) -> None:
        self.spreadsheet_id: str | None = None
        self.hojas: dict[str, list[list]] = {}

    def volcar(self, spreadsheet_id: str, hojas: dict[str, list[list]]) -> None:
        self.spreadsheet_id = spreadsheet_id
        self.hojas = hojas


# --- Formateadores puros -----------------------------------------------------


def test_num_convierte_decimal_y_none():
    assert _num(Decimal("2117647.06")) == 2117647.06
    assert _num(None) == ""
    assert _num("no-numero") == ""


def test_pct_pasa_decimal_a_porcentaje():
    assert _pct(Decimal("0.135")) == 13.5
    assert _pct(None) == ""


def test_efectividad_etiquetas():
    assert _efectividad(True, "enviada") == "Efectiva"
    assert _efectividad(False, "generada") == "No efectiva"
    assert _efectividad(None, "generada") == "Sin marcar"


def test_lista_array_a_cadena():
    assert _lista(["a@x.com", "b@x.com"]) == "a@x.com; b@x.com"
    assert _lista(None) == ""


# --- Volcado completo (Postgres real) ---------------------------------------


@requiere_pg
def test_sincronizar_vuelca_todas_las_hojas(db_session):
    from shared.models import AjusteMargen, CorreoEnviado, Cotizacion

    cot = Cotizacion(
        folio="PCN-TEST01",
        linea_negocio="Nacional",
        cliente_nombre="ACME S.A.",
        origen="Bogotá",
        destino="Medellín",
        costo=Decimal("1800000"),
        margen_politica=Decimal("0.15"),
        margen_aplicado=Decimal("0.18"),
        precio_final=Decimal("2117647.06"),
        moneda="COP",
        estado="enviada",
        efectiva=True,
        comercial_email="dguerra@boterosoto.com.co",
    )
    db_session.add(cot)
    db_session.flush()
    db_session.add(
        AjusteMargen(
            cotizacion_id=cot.id,
            folio=cot.folio,
            margen_politica=Decimal("0.15"),
            margen_aplicado=Decimal("0.18"),
            diferencia=Decimal("0.03"),
            motivo="Cliente estratégico",
            comercial_email=cot.comercial_email,
        )
    )
    db_session.add(
        CorreoEnviado(
            cotizacion_id=cot.id,
            para=["cliente@acme.com"],
            cc=[],
            asunto="Cotización PCN-TEST01",
            estado="enviado",
            confirmado_por=cot.comercial_email,
        )
    )
    db_session.flush()

    fake = _SheetsFake()
    conteo = sincronizar(
        db_session, servicio=fake, spreadsheet_id="LIBRO_TEST", ahora=datetime.now(UTC)
    )

    assert fake.spreadsheet_id == "LIBRO_TEST"
    # Todas las hojas del tablero están presentes.
    assert set(fake.hojas) == {
        "KPIs", "Cotizaciones", "Ajustes_Margen",
        "Correos", "Actividad_Comercial", "Uso_Herramientas",
    }
    # Cada hoja tiene su fila de encabezado + el conteo reporta filas de datos.
    for nombre, matriz in fake.hojas.items():
        assert len(matriz) == conteo[nombre] + 1

    # La fila de la cotización trae los datos derivados correctos.
    encabezados, *filas = fake.hojas["Cotizaciones"]
    fila = next(f for f in filas if f[0] == "PCN-TEST01")
    reg = dict(zip(encabezados, fila, strict=True))
    assert reg["Cliente"] == "ACME S.A."
    assert reg["Ruta"] == "Bogotá → Medellín"
    assert reg["Efectividad"] == "Efectiva"
    assert reg["Margen Aplicado %"] == 18.0
    assert reg["Precio Final"] == 2117647.06
    assert reg["Utilidad"] == 317647.06  # 2117647.06 - 1800000
    assert reg["N° Ajustes"] == 1
    assert reg["N° Correos Enviados"] == 1

    # KPIs es una tabla clave/valor con la marca de tiempo de actualización.
    kpis = dict(fila for fila in fake.hojas["KPIs"][1:])
    assert kpis["Total de cotizaciones"] >= 1
    assert "Última actualización" in kpis


@requiere_pg
def test_construir_tablas_encabezados_estables(db_session):
    hojas = construir_tablas(db_session, ahora=datetime.now(UTC))
    assert hojas["Cotizaciones"][0][0] == "Folio"
    assert hojas["Correos"][0][:2] == ["Fecha", "Folio"]
