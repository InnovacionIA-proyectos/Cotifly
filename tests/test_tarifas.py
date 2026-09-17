"""Tests de la capa de tarifas (Fase 3A). Puros: sin Postgres ni servidor MCP.

Cubren el contrato §5.1 con el `DriveFlatFileProvider` sobre un CSV fixture de
encabezados "sucios": match exacto, ambigüedad, sin coincidencia, caché con TTL,
normalización de columnas y el ruteo por línea del `TarifaRouter`.
"""

from __future__ import annotations

from pathlib import Path

import pytest

from mcp_server.tarifas import (
    DriveFlatFileProvider,
    EntradaBuscarTarifa,
    FuenteTarifaNoDisponible,
    LocalRepositorioArchivos,
    RepositorioArchivos,
    TarifaRouter,
)
from mcp_server.tarifas.base import (
    ArchivoTarifa,
    _parse_tarifa,
    normalizar,
    normalizar_fila,
)
from mcp_server.tarifas.router import _por_linea_desde_env

FIXDIR = Path(__file__).parent / "fixtures" / "tarifas"


class _RepoEspia(RepositorioArchivos):
    """Envuelve un repo real y cuenta cuántas veces se leyó la fuente."""

    def __init__(self, inner: RepositorioArchivos) -> None:
        self.inner = inner
        self.lecturas = 0

    def obtener(self, linea_negocio: str) -> ArchivoTarifa | None:
        self.lecturas += 1
        return self.inner.obtener(linea_negocio)


def _provider(ttl_seg: int = 3600, reloj=None) -> DriveFlatFileProvider:
    repo = LocalRepositorioArchivos(FIXDIR)
    if reloj is None:
        return DriveFlatFileProvider(repo, ttl_seg=ttl_seg)
    return DriveFlatFileProvider(repo, ttl_seg=ttl_seg, reloj=reloj)


# --- match exacto + normalización -------------------------------------------


def test_match_exacto_normaliza_columnas_y_valor() -> None:
    # Encabezados sucios (" Origen ", "Valor Flete") y valor "2.850.000".
    r = _provider().buscar("Nacional", "yumbo", "Buenaventura")
    assert r.encontrada is True
    assert r.costo == 2850000.0
    assert r.moneda == "COP"
    assert r.fuente == "drive_flat_files"
    assert r.archivo_o_referencia == "tarifario_nacional_2026-07.csv"
    assert r.candidatas == []


def test_filtro_por_vehiculo_desambigua() -> None:
    r = _provider().buscar("Nacional", "BOGOTA", "MEDELLIN", filtros={"tipo_vehiculo": "SENCILLO"})
    assert r.encontrada is True
    assert r.costo == 1200000.0


# --- ambigüedad --------------------------------------------------------------


def test_filtro_por_tipo_carga() -> None:
    r = _provider().buscar(
        "Nacional", "YUMBO", "BUENAVENTURA", filtros={"tipo_carga": "carga_seca"}
    )
    assert r.encontrada is True
    assert r.costo == 2850000.0


def test_ambiguo_devuelve_candidatas_sin_costo() -> None:
    r = _provider().buscar("Nacional", "BOGOTA", "MEDELLIN")
    assert r.encontrada is False
    assert r.costo is None
    assert len(r.candidatas) == 2
    assert {c.tipo_vehiculo for c in r.candidatas} == {"TRACTOMULA", "SENCILLO"}


# --- sin coincidencia --------------------------------------------------------


def test_sin_match_propone_candidatas_mismo_origen() -> None:
    r = _provider().buscar("Nacional", "YUMBO", "PASTO")
    assert r.encontrada is False
    assert r.costo is None
    assert 1 <= len(r.candidatas) <= 5
    assert all(c.origen == "YUMBO" for c in r.candidatas)  # nunca inventa: solo del archivo


def test_candidatas_topadas_a_cinco() -> None:
    r = _provider().buscar("Nacional", "YUMBO", "NINGUNA")
    assert len(r.candidatas) <= 5


# --- fuente no disponible ----------------------------------------------------


def test_linea_sin_archivo_lanza_no_disponible() -> None:
    with pytest.raises(FuenteTarifaNoDisponible):
        _provider().buscar("Exportacion", "YUMBO", "BUENAVENTURA")


# --- caché con TTL -----------------------------------------------------------


def test_cache_no_relee_hasta_expirar_ttl() -> None:
    t = [0.0]
    repo = _RepoEspia(LocalRepositorioArchivos(FIXDIR))
    prov = DriveFlatFileProvider(repo, ttl_seg=100, reloj=lambda: t[0])

    prov.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    prov.buscar("Nacional", "CALI", "BOGOTA")
    assert repo.lecturas == 1  # segunda consulta usa caché

    t[0] = 50.0  # dentro del TTL
    prov.buscar("Nacional", "YUMBO", "CARTAGENA")
    assert repo.lecturas == 1

    t[0] = 150.0  # TTL expirado
    prov.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    assert repo.lecturas == 2  # relee la fuente


# --- router ------------------------------------------------------------------


def test_router_default_y_por_linea() -> None:
    prov = _provider()
    router = TarifaRouter(
        providers={"drive_flat_files": prov},
        default="drive_flat_files",
        por_linea={"Internacional": "api_fletes"},  # diferido a F16
    )
    # Línea en default → usa Drive y resuelve.
    entrada = EntradaBuscarTarifa(linea_negocio="Nacional", origen="YUMBO", destino="BUENAVENTURA")
    r = router.buscar(entrada)
    assert r.encontrada is True
    # Línea ruteada a un provider no registrado → error de configuración.
    with pytest.raises(ValueError, match="api_fletes"):
        router.provider_de("Internacional")


def test_router_default_invalido() -> None:
    with pytest.raises(ValueError, match="por defecto"):
        TarifaRouter(providers={"drive_flat_files": _provider()}, default="inexistente")


# --- normalización (unidad) --------------------------------------------------


def test_normalizar_none_y_acentos() -> None:
    assert normalizar(None) == ""
    assert normalizar("  Medellín ") == "MEDELLIN"


def test_parse_tarifa_variantes() -> None:
    assert _parse_tarifa("2.850.000") == 2850000.0
    assert _parse_tarifa("$ 1 200 000") == 1200000.0
    assert _parse_tarifa(None) is None
    assert _parse_tarifa("s/d") is None


def test_normalizar_fila_ignora_columnas_desconocidas() -> None:
    fila = normalizar_fila({"Origen": "Yumbo", "Columna Rara": "x", "Valor": "100"})
    assert fila == {"origen": "Yumbo", "tarifa": 100.0}


def test_sin_match_propone_por_destino() -> None:
    # Origen sin filas, pero el destino sí existe → candidatas por destino.
    r = _provider().buscar("Nacional", "INEXISTENTE", "MEDELLIN")
    assert r.encontrada is False
    assert r.candidatas
    assert all(c.destino == "MEDELLIN" for c in r.candidatas)


# --- lectura XLSX ------------------------------------------------------------


def test_lee_xlsx(tmp_path: Path) -> None:
    openpyxl = pytest.importorskip("openpyxl")
    wb = openpyxl.Workbook()
    hoja = wb.active
    hoja.append([" Origen ", "Ciudad Destino", "Tipo Vehículo", "Valor Flete", "Moneda"])
    hoja.append(["IPIALES", "CALI", "TRACTOMULA", 4200000, "COP"])
    wb.save(tmp_path / "tarifario_nacional_2026-08.xlsx")

    prov = DriveFlatFileProvider(LocalRepositorioArchivos(tmp_path))
    r = prov.buscar("Nacional", "IPIALES", "CALI")
    assert r.encontrada is True
    assert r.costo == 4200000.0
    assert r.archivo_o_referencia == "tarifario_nacional_2026-08.xlsx"


def test_por_linea_desde_env() -> None:
    env = {
        "TARIFA_PROVIDER__Nacional": "drive_flat_files",
        "TARIFA_PROVIDER__Internacional": "api_fletes",
        "OTRA_VAR": "x",
    }
    assert _por_linea_desde_env(env) == {
        "Nacional": "drive_flat_files",
        "Internacional": "api_fletes",
    }
