"""Tests del `DriveRepositorioArchivos` (descarga real de Drive). Puros: sin red.

Usan un `DriveServicio` en memoria para verificar la orquestación: resolver la
carpeta de la línea, elegir el archivo más reciente por nombre, descargarlo y
leerlo con los parsers estándar. Integración real: marca `@pytest.mark.drive`.
"""

from __future__ import annotations

from mcp_server.tarifas.base import normalizar_fila
from mcp_server.tarifas.drive_provider import (
    DriveFlatFileProvider,
    DriveRepositorioArchivos,
    DriveServicio,
)

_CSV = (
    b"Origen,Destino,Tipo Vehiculo,Valor Flete,Moneda\n"
    b"YUMBO,BUENAVENTURA,TRACTOMULA,2.850.000,COP\n"
)


class _DriveFalso(DriveServicio):
    """Drive en memoria: {folder_id: {name: bytes}}. Registra las descargas."""

    def __init__(self, contenido: dict[str, dict[str, bytes]]) -> None:
        self._contenido = contenido
        self.descargas: list[str] = []

    def listar(self, folder_id: str, prefijo: str) -> list[dict[str, str]]:
        archivos = self._contenido.get(folder_id, {})
        return [
            {"id": f"{folder_id}/{nombre}", "name": nombre}
            for nombre in archivos
            if nombre.startswith(prefijo)
        ]

    def descargar(self, file_id: str) -> bytes:
        self.descargas.append(file_id)
        folder, nombre = file_id.split("/", 1)
        return self._contenido[folder][nombre]


def test_elige_mes_mas_reciente_y_lee_csv() -> None:
    drive = _DriveFalso(
        {
            "F1": {
                "tarifario_nacional_2026-06.csv": _CSV,
                "tarifario_nacional_2026-07.csv": _CSV,
                "otro_archivo.txt": b"x",
            }
        }
    )
    repo = DriveRepositorioArchivos(servicio=drive, folder_raiz="F1")
    archivo = repo.obtener("Nacional")

    assert archivo is not None
    assert archivo.referencia == "tarifario_nacional_2026-07.csv"  # el mayor por nombre
    filas = [normalizar_fila(f) for f in archivo.filas]
    assert filas[0]["origen"] == "YUMBO"
    assert filas[0]["tarifa"] == 2850000.0


def test_override_por_linea_tiene_prioridad_sobre_raiz() -> None:
    drive = _DriveFalso(
        {
            "RAIZ": {"tarifario_nacional_2026-07.csv": _CSV},
            "SOLO_IMPO": {"tarifario_importacion_2026-07.csv": _CSV},
        }
    )
    repo = DriveRepositorioArchivos(
        servicio=drive, folder_raiz="RAIZ", folder_por_linea={"Importacion": "SOLO_IMPO"}
    )
    archivo = repo.obtener("Importacion")
    assert archivo is not None
    assert archivo.referencia == "tarifario_importacion_2026-07.csv"


def test_sin_carpeta_ni_archivo_devuelve_none() -> None:
    repo_sin_folder = DriveRepositorioArchivos(servicio=_DriveFalso({}))
    assert repo_sin_folder.obtener("Nacional") is None  # no hay carpeta configurada

    repo_vacio = DriveRepositorioArchivos(servicio=_DriveFalso({"F1": {}}), folder_raiz="F1")
    assert repo_vacio.obtener("Nacional") is None  # carpeta sin tarifario de la línea


def test_integra_con_provider_y_cache() -> None:
    drive = _DriveFalso({"F1": {"tarifario_nacional_2026-07.csv": _CSV}})
    repo = DriveRepositorioArchivos(servicio=drive, folder_raiz="F1")
    prov = DriveFlatFileProvider(repo, ttl_seg=3600, reloj=lambda: 0.0)

    r1 = prov.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    prov.buscar("Nacional", "YUMBO", "BUENAVENTURA")  # segunda: debe salir de caché
    assert r1.encontrada is True
    assert r1.costo == 2850000.0
    assert r1.fuente == "drive_flat_files"
    assert len(drive.descargas) == 1  # la segunda usa caché, no re-descarga
