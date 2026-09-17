"""Tests del `DriveXlsxRepositorio` (tarifario real: un Excel con hojas por línea).

Usan un `DriveServicio` en memoria que devuelve un .xlsx armado al vuelo con el
esquema real del export (`Tarifas Nacionales` y `Tarifas_contenedor`). Verifican:
mapeo línea→hoja, filtrado por rango de peso, parseo numérico correcto de valores
float (no se corrompen), candidatas con rango de peso, y caché (una sola descarga).
"""

from __future__ import annotations

import io

import pytest

from mcp_server.tarifas.base import FuenteTarifaNoDisponible
from mcp_server.tarifas.drive_provider import (
    DriveFlatFileProvider,
    DriveServicio,
    DriveXlsxRepositorio,
)

_HOJAS = {
    "Nacional": "Tarifas Nacionales",
    "Distribucion": "Tarifas Nacionales",
    "Importacion": "Tarifas_contenedor",
}


def _xlsx() -> bytes:
    from openpyxl import Workbook

    wb = Workbook()
    n = wb.active
    n.title = "Tarifas Nacionales"
    n.append(
        ["lineaNegocio", "claseViaje", "ciudadOrigen", "ciudadDestino",
         "tipoVehiculo", "tipoCarroceria", "pesoLimite", "valor", "Vigencia", "detallesTarifa"]
    )
    # valor como float (4800000.0) para verificar que NO se corrompe a 48000000.
    n.append(["Oferta Nacional", "Cupo", "CALI", "MEDELLIN", "3S3", "FURGON",
              30000, 4800000.0, "11072026", "Tractomula furgón"])
    n.append(["Oferta Nacional", "Cupo", "CALI", "MEDELLIN", "2S2", "FURGON",
              18000, 4000000.0, "11072026", "Minimula furgón"])

    c = wb.create_sheet("Tarifas_contenedor")
    c.append(
        ["lineaNegocio", "claseViaje", "ciudadOrigen", "ciudadDestino",
         "pesoInf", "pesoSup", "claseContenedor", "valor", "Vigencia", "detallesTarifa"]
    )
    c.append(["Oferta Puerto", "Contenedor", "BUENAVENTURA", "CALI",
              4000, 18000, "CNT40.HC", 1800000, "11072026", "Libre cargue"])
    c.append(["Oferta Puerto", "Contenedor", "BUENAVENTURA", "CALI",
              18001, 20000, "CNT40.HC", 2300000, "11072026", "Poderosa"])

    buf = io.BytesIO()
    wb.save(buf)
    return buf.getvalue()


class _DriveUnArchivo(DriveServicio):
    """Drive en memoria de un único archivo; cuenta las descargas."""

    def __init__(self, datos: bytes) -> None:
        self._datos = datos
        self.descargas = 0

    def listar(self, folder_id: str, prefijo: str) -> list[dict[str, str]]:
        return []

    def descargar(self, file_id: str) -> bytes:
        self.descargas += 1
        return self._datos


def _provider(servicio: _DriveUnArchivo) -> DriveFlatFileProvider:
    repo = DriveXlsxRepositorio(servicio, "FID", _HOJAS, ttl_seg=3600, reloj=lambda: 0.0)
    return DriveFlatFileProvider(repo, ttl_seg=3600, reloj=lambda: 0.0)


# --- Mapeo línea → hoja + peso -----------------------------------------------


def test_nacional_peso_selecciona_fila_y_valor_float_intacto() -> None:
    prov = _provider(_DriveUnArchivo(_xlsx()))
    # peso 25.000: solo la fila de pesoLimite 30.000 aplica (la de 18.000 no).
    r = prov.buscar("Nacional", "CALI", "MEDELLIN", filtros={"peso_kg": 25000})
    assert r.encontrada is True
    assert r.costo == 4800000.0  # float sin corromper (no 48.000.000)


def test_contenedor_rango_peso_inf_sup() -> None:
    prov = _provider(_DriveUnArchivo(_xlsx()))
    r1 = prov.buscar("Importacion", "BUENAVENTURA", "CALI", filtros={"peso_kg": 15000})
    assert r1.encontrada is True and r1.costo == 1800000.0  # rango 4000–18000
    r2 = prov.buscar("Importacion", "BUENAVENTURA", "CALI", filtros={"peso_kg": 19000})
    assert r2.encontrada is True and r2.costo == 2300000.0  # rango 18001–20000


def test_sin_peso_devuelve_candidatas_con_rango() -> None:
    prov = _provider(_DriveUnArchivo(_xlsx()))
    r = prov.buscar("Importacion", "BUENAVENTURA", "CALI")
    assert r.encontrada is False
    assert len(r.candidatas) == 2
    # Las candidatas traen el rango de peso y el detalle para que el LLM desambigüe.
    assert {c.peso_inf for c in r.candidatas} == {4000.0, 18001.0}
    assert all(c.clase_contenedor == "CNT40.HC" for c in r.candidatas)


def test_linea_sin_hoja_no_hay_tarifa() -> None:
    prov = _provider(_DriveUnArchivo(_xlsx()))
    # Almacenamiento no está mapeada a ninguna hoja → fuente no disponible.
    with pytest.raises(FuenteTarifaNoDisponible):
        prov.buscar("Almacenamiento", "CALI", "MEDELLIN", filtros={"peso_kg": 1000})


def test_descarga_una_sola_vez_por_cache() -> None:
    servicio = _DriveUnArchivo(_xlsx())
    prov = _provider(servicio)
    prov.buscar("Nacional", "CALI", "MEDELLIN", filtros={"peso_kg": 25000})
    prov.buscar("Nacional", "CALI", "MEDELLIN", filtros={"peso_kg": 25000})
    prov.buscar("Importacion", "BUENAVENTURA", "CALI", filtros={"peso_kg": 15000})
    assert servicio.descargas == 1  # bytes cacheados; distintas hojas del mismo libro
