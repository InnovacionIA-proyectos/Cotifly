"""TarifaRouter: resuelve el provider por línea de negocio (§6).

La herramienta `buscar_tarifa` delega aquí; el router elige el provider según la
configuración por línea (`TARIFA_PROVIDER__<LINEA>`, con fallback a
`TARIFA_PROVIDER_DEFAULT`). Migrar una línea a `api_fletes` (F16) será solo
cambiar esa variable de entorno, sin tocar la herramienta ni el Orquestador.
"""

from __future__ import annotations

import os

from mcp_server.tarifas.base import (
    EntradaBuscarTarifa,
    LocalRepositorioArchivos,
    RepositorioArchivos,
    ResultadoTarifa,
    TarifaProvider,
    normalizar,
)
from mcp_server.tarifas.drive_provider import DriveFlatFileProvider
from shared.config import settings

_PREFIJO_ENV = "TARIFA_PROVIDER__"
_PREFIJO_FOLDER = "DRIVE_FOLDER_ID__"
_PREFIJO_HOJA = "DRIVE_HOJA__"

# Mapeo línea de negocio → hoja del Excel del tarifario (decisión de negocio
# 2026-07-21). Almacenamiento/ITR no tienen hoja aquí → sin tarifa en esta fuente
# (el comercial la captura manual). Override por env: DRIVE_HOJA__<Linea>=<Hoja>.
_HOJA_POR_LINEA_DEFAULT = {
    "Nacional": "Tarifas Nacionales",
    "Distribucion": "Tarifas Nacionales",
    "Importacion": "Tarifas_contenedor",
    "Exportacion": "Tarifas_contenedor",
    "Internacional": "Tarifas_contenedor",
}


class TarifaRouter:
    def __init__(
        self,
        providers: dict[str, TarifaProvider],
        default: str,
        por_linea: dict[str, str] | None = None,
    ) -> None:
        if default not in providers:
            raise ValueError(f"El provider por defecto '{default}' no está registrado.")
        self._providers = providers
        self._default = default
        # Claves de línea normalizadas para comparar sin importar acentos/mayúsculas.
        self._por_linea = {normalizar(k): v for k, v in (por_linea or {}).items()}

    def provider_de(self, linea_negocio: str) -> TarifaProvider:
        nombre = self._por_linea.get(normalizar(linea_negocio), self._default)
        provider = self._providers.get(nombre)
        if provider is None:
            raise ValueError(
                f"La línea '{linea_negocio}' está configurada con el provider '{nombre}', "
                f"que no está disponible (¿diferido a F16?)."
            )
        return provider

    def buscar(self, entrada: EntradaBuscarTarifa) -> ResultadoTarifa:
        provider = self.provider_de(entrada.linea_negocio)
        return provider.buscar(
            entrada.linea_negocio,
            entrada.origen,
            entrada.destino,
            filtros={
                "tipo_vehiculo": entrada.tipo_vehiculo,
                "tipo_carga": entrada.tipo_carga,
                "peso_kg": entrada.peso_kg,
            },
        )


def _por_linea_desde_env(entorno: dict[str, str] | None = None) -> dict[str, str]:
    """Lee `TARIFA_PROVIDER__<LINEA>=<provider>` del entorno."""
    entorno = entorno if entorno is not None else dict(os.environ)
    return {
        clave[len(_PREFIJO_ENV) :]: valor
        for clave, valor in entorno.items()
        if clave.startswith(_PREFIJO_ENV) and valor
    }


def _folders_desde_env(entorno: dict[str, str] | None = None) -> dict[str, str]:
    """Lee `DRIVE_FOLDER_ID__<LINEA>=<folderId>` del entorno (override por línea)."""
    entorno = entorno if entorno is not None else dict(os.environ)
    return {
        clave[len(_PREFIJO_FOLDER) :]: valor
        for clave, valor in entorno.items()
        if clave.startswith(_PREFIJO_FOLDER) and valor
    }


def _hojas_desde_env(entorno: dict[str, str] | None = None) -> dict[str, str]:
    """Lee overrides `DRIVE_HOJA__<LINEA>=<NombreHoja>` del entorno."""
    entorno = entorno if entorno is not None else dict(os.environ)
    return {
        clave[len(_PREFIJO_HOJA) :]: valor
        for clave, valor in entorno.items()
        if clave.startswith(_PREFIJO_HOJA) and valor
    }


def _construir_repositorio() -> RepositorioArchivos:
    """Repositorio de archivos, en orden de preferencia:

    1. **Excel único de Drive** (`DRIVE_TARIFARIO_FILE_ID` + `GOOGLE_SA_JSON`): la
       fuente real, un libro con hojas por línea. Es lo activo hoy.
    2. **Carpeta de Drive** con CSV/XLSX por línea (`DRIVE_TARIFARIO_FOLDER_ID`…).
    3. **Archivos locales** del volumen (`TARIFA_LOCAL_DIR`) — dev/tests.
    """
    if settings.google_sa_json and settings.drive_tarifario_file_id:
        from mcp_server.tarifas.drive_provider import DriveXlsxRepositorio, GoogleDriveServicio

        hojas = {**_HOJA_POR_LINEA_DEFAULT, **_hojas_desde_env()}
        return DriveXlsxRepositorio(
            servicio=GoogleDriveServicio(settings.google_sa_json),
            file_id=settings.drive_tarifario_file_id,
            hoja_por_linea=hojas,
            ttl_seg=settings.tarifa_cache_ttl_seg,
        )

    folders = _folders_desde_env()
    if settings.google_sa_json and (settings.drive_tarifario_folder_id or folders):
        from mcp_server.tarifas.drive_provider import (
            DriveRepositorioArchivos,
            GoogleDriveServicio,
        )

        return DriveRepositorioArchivos(
            servicio=GoogleDriveServicio(settings.google_sa_json),
            folder_raiz=settings.drive_tarifario_folder_id,
            folder_por_linea=folders,
            cache_dir=settings.drive_cache_dir,
        )
    return LocalRepositorioArchivos(settings.tarifa_local_dir)


def construir_router() -> TarifaRouter:
    """Fábrica de producción: Drive siempre; API Fletes + doble lectura si hay URL (F16).

    Con `API_FLETES_URL` seteada se registran los providers `api_fletes` (REST del
    Tarifario) y `doble_lectura` (Drive principal + API sombra). Migrar una línea es
    solo config: `TARIFA_PROVIDER__<Linea>=api_fletes` (o `=doble_lectura`).
    """
    repo = _construir_repositorio()
    drive = DriveFlatFileProvider(repo, ttl_seg=settings.tarifa_cache_ttl_seg)
    providers: dict[str, TarifaProvider] = {"drive_flat_files": drive}
    if settings.api_fletes_url:
        from mcp_server.tarifas.api_provider import ApiFletesProvider, DobleLecturaProvider

        api = ApiFletesProvider(
            settings.api_fletes_url,
            api_token=settings.api_fletes_token,
            timeout_seg=settings.api_fletes_timeout_seg,
        )
        providers["api_fletes"] = api
        providers["doble_lectura"] = DobleLecturaProvider(principal=drive, sombra=api)
    return TarifaRouter(
        providers=providers,
        default=settings.tarifa_provider_default,
        por_linea=_por_linea_desde_env(),
    )
