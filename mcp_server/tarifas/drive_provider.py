"""DriveFlatFileProvider: tarifas desde archivos planos (Drive/local) — Fase 3A.

Lee el tarifario de la línea vía un `RepositorioArchivos`, normaliza las columnas
y busca coincidencia exacta por (origen, destino) + filtros opcionales
(tipo_vehiculo, tipo_carga). Reglas duras (§5.1):

- exactamente 1 fila → `encontrada:true` con su costo;
- 0 filas por origen/destino → `encontrada:false` + hasta 5 candidatas (nunca inventa);
- varias filas (ambigüedad) → `encontrada:false` + esas filas como candidatas.

Mantiene **caché local con TTL** (default 6 h) por línea para no releer la fuente
en cada cotización. El reloj es inyectable para poder probar la expiración.
"""

from __future__ import annotations

import time
from abc import ABC, abstractmethod
from collections.abc import Callable
from pathlib import Path
from typing import Any

from mcp_server.tarifas.base import (
    ArchivoTarifa,
    Candidata,
    FuenteTarifaNoDisponible,
    RepositorioArchivos,
    ResultadoTarifa,
    TarifaProvider,
    _leer_csv,
    _leer_xlsx,
    normalizar,
    normalizar_fila,
)

_TTL_DEFAULT_SEG = 6 * 3600
_MAX_CANDIDATAS = 5


class _Entrada:
    """Entrada de caché: filas normalizadas de un tarifario + cuándo se cargó."""

    __slots__ = ("filas", "referencia", "cargado_en")

    def __init__(self, filas: list[dict[str, Any]], referencia: str, cargado_en: float) -> None:
        self.filas = filas
        self.referencia = referencia
        self.cargado_en = cargado_en


class DriveFlatFileProvider(TarifaProvider):
    fuente = "drive_flat_files"

    def __init__(
        self,
        repositorio: RepositorioArchivos,
        ttl_seg: int = _TTL_DEFAULT_SEG,
        reloj: Callable[[], float] = time.monotonic,
    ) -> None:
        self._repo = repositorio
        self._ttl = ttl_seg
        self._reloj = reloj
        self._cache: dict[str, _Entrada] = {}

    # --- caché ---------------------------------------------------------------

    def _cargar(self, linea_negocio: str) -> _Entrada:
        clave = normalizar(linea_negocio)
        entrada = self._cache.get(clave)
        ahora = self._reloj()
        if entrada is not None and (ahora - entrada.cargado_en) < self._ttl:
            return entrada  # caché vigente: no se relee la fuente

        archivo = self._repo.obtener(linea_negocio)
        if archivo is None:
            raise FuenteTarifaNoDisponible(
                f"No hay tarifario disponible para la línea '{linea_negocio}'."
            )
        entrada = _Entrada(
            filas=self._normalizar(archivo),
            referencia=archivo.referencia,
            cargado_en=ahora,
        )
        self._cache[clave] = entrada
        return entrada

    @staticmethod
    def _normalizar(archivo: ArchivoTarifa) -> list[dict[str, Any]]:
        filas = []
        for cruda in archivo.filas:
            fila = normalizar_fila(cruda)
            if fila.get("origen") and fila.get("destino") and fila.get("tarifa") is not None:
                filas.append(fila)
        return filas

    # --- búsqueda ------------------------------------------------------------

    def buscar(
        self,
        linea_negocio: str,
        origen: str,
        destino: str,
        filtros: dict[str, Any] | None = None,
    ) -> ResultadoTarifa:
        entrada = self._cargar(linea_negocio)
        filtros = filtros or {}
        veh = filtros.get("tipo_vehiculo")
        carga = filtros.get("tipo_carga")
        peso = filtros.get("peso_kg")

        por_od = [
            f
            for f in entrada.filas
            if normalizar(f["origen"]) == normalizar(origen)
            and normalizar(f["destino"]) == normalizar(destino)
        ]

        filtradas = por_od
        if veh:
            objetivo = normalizar(veh)
            filtradas = [f for f in filtradas if normalizar(f.get("tipo_vehiculo")) == objetivo]
        if carga:
            objetivo = normalizar(carga)
            filtradas = [f for f in filtradas if normalizar(f.get("tipo_carga")) == objetivo]
        if peso is not None:
            filtradas = [f for f in filtradas if _peso_en_rango(f, float(peso))]

        if len(filtradas) == 1:
            fila = filtradas[0]
            return ResultadoTarifa(
                encontrada=True,
                costo=fila["tarifa"],
                moneda=fila.get("moneda") or "COP",
                fuente=self.fuente,
                archivo_o_referencia=entrada.referencia,
            )

        # Ambigüedad (varias por O/D o por filtro) o sin match: devolver candidatas.
        base = filtradas or por_od or self._sugerencias(entrada.filas, origen, destino)
        return ResultadoTarifa(
            encontrada=False,
            fuente=self.fuente,
            archivo_o_referencia=entrada.referencia,
            candidatas=[self._a_candidata(f) for f in base[:_MAX_CANDIDATAS]],
        )

    @staticmethod
    def _sugerencias(
        filas: list[dict[str, Any]], origen: str, destino: str
    ) -> list[dict[str, Any]]:
        """Sin coincidencia O/D: propone filas que comparten origen o destino."""
        mismo_origen = [f for f in filas if normalizar(f["origen"]) == normalizar(origen)]
        mismo_destino = [f for f in filas if normalizar(f["destino"]) == normalizar(destino)]
        return mismo_origen or mismo_destino

    @staticmethod
    def _a_candidata(fila: dict[str, Any]) -> Candidata:
        return Candidata(
            origen=fila["origen"],
            destino=fila["destino"],
            tipo_vehiculo=fila.get("tipo_vehiculo"),
            tipo_carga=fila.get("tipo_carga"),
            clase_contenedor=fila.get("clase_contenedor"),
            peso_inf=fila.get("peso_inf"),
            peso_sup=fila.get("peso_sup"),
            peso_limite=fila.get("peso_limite"),
            tarifa=fila["tarifa"],
            moneda=fila.get("moneda") or "COP",
            vigencia=fila.get("vigencia"),
            detalles=fila.get("detalles"),
        )


def _peso_en_rango(fila: dict[str, Any], peso: float) -> bool:
    """True si el peso cae en el rango de la tarifa.

    - Con rango (pesoInf/pesoSup, hoja de contenedor): `inf <= peso <= sup`.
    - Con tope (pesoLimite, hoja nacional): `peso <= limite`.
    - Sin datos de peso en la fila: no restringe (la fila sigue siendo candidata).
    """
    inf = fila.get("peso_inf")
    sup = fila.get("peso_sup")
    lim = fila.get("peso_limite")
    if inf is not None and sup is not None:
        return float(inf) <= peso <= float(sup)
    if lim is not None:
        return peso <= float(lim)
    return True


class DriveServicio(ABC):
    """Acceso mínimo a Google Drive que necesita el repositorio (inyectable).

    Se aísla como interfaz para poder probar `DriveRepositorioArchivos` con un doble
    en memoria, sin red ni credenciales. La implementación real es
    `GoogleDriveServicio` (extra `[drive]`).
    """

    @abstractmethod
    def listar(self, folder_id: str, prefijo: str) -> list[dict[str, str]]:
        """Archivos de la carpeta cuyo nombre empieza por `prefijo`.

        Devuelve dicts con al menos `id` y `name`, ordenables por `name`.
        """

    @abstractmethod
    def descargar(self, file_id: str) -> bytes:
        """Contenido binario del archivo."""


class GoogleDriveServicio(DriveServicio):  # pragma: no cover - requiere red + credenciales
    """Implementación real sobre `google-api-python-client` (extra `[drive]`)."""

    _SCOPES = ("https://www.googleapis.com/auth/drive.readonly",)

    def __init__(self, google_sa_json: str) -> None:
        self._sa = google_sa_json
        self._svc: Any = None

    def _servicio(self) -> Any:
        if self._svc is not None:
            return self._svc
        try:
            import json

            from google.oauth2 import service_account
            from googleapiclient.discovery import build
        except ImportError as e:
            raise FuenteTarifaNoDisponible(
                'La lectura de Drive requiere el extra: pip install -e ".[drive]"'
            ) from e
        # `google_sa_json` admite una ruta a archivo o el JSON en línea (secreto).
        ruta = Path(self._sa)
        if ruta.exists():
            creds = service_account.Credentials.from_service_account_file(
                str(ruta), scopes=list(self._SCOPES)
            )
        else:
            creds = service_account.Credentials.from_service_account_info(
                json.loads(self._sa), scopes=list(self._SCOPES)
            )
        self._svc = build("drive", "v3", credentials=creds, cache_discovery=False)
        return self._svc

    def listar(self, folder_id: str, prefijo: str) -> list[dict[str, str]]:
        svc = self._servicio()
        # Escapa comillas simples del prefijo para la query de Drive.
        seguro = prefijo.replace("'", "\\'")
        query = (
            f"'{folder_id}' in parents and trashed = false "
            f"and name contains '{seguro}'"
        )
        resp = (
            svc.files()
            .list(q=query, fields="files(id,name)", orderBy="name", pageSize=100)
            .execute()
        )
        return list(resp.get("files", []))

    def descargar(self, file_id: str) -> bytes:
        import io

        from googleapiclient.http import MediaIoBaseDownload

        svc = self._servicio()
        buffer = io.BytesIO()
        descarga = MediaIoBaseDownload(buffer, svc.files().get_media(fileId=file_id))
        terminado = False
        while not terminado:
            _, terminado = descarga.next_chunk()
        return buffer.getvalue()


class DriveRepositorioArchivos(RepositorioArchivos):
    """Repositorio real: descarga el tarifario CSV/XLSX de carpetas de Drive.

    Resuelve la carpeta de la línea (override por línea o carpeta raíz común),
    lista `tarifario_<linea>_*`, elige el de nombre mayor (mes más reciente),
    descarga su contenido y lo lee con los mismos parsers que la fuente local. El
    `DriveFlatFileProvider` cachea el resultado con TTL, así que Drive se consulta
    a lo sumo una vez por línea cada 6 h. El `DriveServicio` es inyectable para
    pruebas; en producción se usa `GoogleDriveServicio` (extra `[drive]`).
    """

    def __init__(
        self,
        servicio: DriveServicio,
        folder_raiz: str = "",
        folder_por_linea: dict[str, str] | None = None,
        cache_dir: str = "/data/tarifas_cache",
    ) -> None:
        self._servicio = servicio
        self._folder_raiz = folder_raiz
        self._folder_por_linea = {
            normalizar(k).lower().replace(" ", ""): v
            for k, v in (folder_por_linea or {}).items()
        }
        self._cache_dir = Path(cache_dir)

    def _folder_de(self, clave: str) -> str | None:
        return self._folder_por_linea.get(clave) or self._folder_raiz or None

    def obtener(self, linea_negocio: str) -> ArchivoTarifa | None:
        clave = normalizar(linea_negocio).lower().replace(" ", "")
        folder = self._folder_de(clave)
        if not folder:
            return None
        archivos = self._servicio.listar(folder, f"tarifario_{clave}_")
        # Solo CSV/XLSX; el de nombre mayor es el mes más reciente (convención §6).
        candidatos = sorted(
            (a for a in archivos if a["name"].lower().endswith((".csv", ".xlsx"))),
            key=lambda a: a["name"],
        )
        if not candidatos:
            return None
        elegido = candidatos[-1]
        datos = self._servicio.descargar(elegido["id"])
        nombre = elegido["name"]
        if nombre.lower().endswith(".csv"):
            filas = _leer_csv(datos)
        else:
            # openpyxl necesita un archivo: se materializa en el cache local.
            self._cache_dir.mkdir(parents=True, exist_ok=True)
            ruta = self._cache_dir / nombre
            ruta.write_bytes(datos)
            filas = _leer_xlsx(ruta)
        return ArchivoTarifa(referencia=nombre, filas=filas)


class DriveXlsxRepositorio(RepositorioArchivos):
    """Tarifario desde un **único** Excel (.xlsx/.xlsm) de Drive con hojas por línea.

    Es la fuente real del proyecto: un libro con hojas como `Tarifas Nacionales` y
    `Tarifas_contenedor`. Descarga el archivo por su ID (Drive API `get_media`), lo
    cachea en memoria con TTL, y por cada línea devuelve las filas de la hoja mapeada
    en `hoja_por_linea` (líneas sin hoja → `None`, la tool responde que no hay tarifa
    en esta fuente y el comercial la captura manual). El `DriveServicio` es inyectable
    para pruebas (sin red).
    """

    def __init__(
        self,
        servicio: DriveServicio,
        file_id: str,
        hoja_por_linea: dict[str, str],
        ttl_seg: int = _TTL_DEFAULT_SEG,
        reloj: Callable[[], float] = time.monotonic,
    ) -> None:
        self._servicio = servicio
        self._file_id = file_id
        self._hoja_por_linea = {normalizar(k): v for k, v in hoja_por_linea.items()}
        self._ttl = ttl_seg
        self._reloj = reloj
        self._bytes: bytes | None = None
        self._descargado_en = 0.0

    def _datos(self) -> bytes:
        ahora = self._reloj()
        if self._bytes is not None and (ahora - self._descargado_en) < self._ttl:
            return self._bytes
        self._bytes = self._servicio.descargar(self._file_id)
        self._descargado_en = ahora
        return self._bytes

    def obtener(self, linea_negocio: str) -> ArchivoTarifa | None:
        hoja = self._hoja_por_linea.get(normalizar(linea_negocio))
        if not hoja:
            return None
        import io

        from openpyxl import load_workbook

        wb = load_workbook(io.BytesIO(self._datos()), read_only=True, data_only=True)
        try:
            if hoja not in wb.sheetnames:
                return None
            ws = wb[hoja]
            filas_iter = ws.iter_rows(values_only=True)
            try:
                encabezados = [str(c) if c is not None else "" for c in next(filas_iter)]
            except StopIteration:  # hoja vacía
                return None
            filas = [dict(zip(encabezados, vals, strict=False)) for vals in filas_iter]
        finally:
            wb.close()
        return ArchivoTarifa(referencia=f"{self._file_id}#{hoja}", filas=filas)
