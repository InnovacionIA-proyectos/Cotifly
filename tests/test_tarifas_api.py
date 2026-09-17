"""Tests del ApiFletesProvider y la doble lectura (Fase 16, contrato provisional).

Todo contra la API mockeada (respx); sin red real. Verifica que la migración por
línea es solo config y que la fuente sombra nunca rompe la cotización.
"""

from __future__ import annotations

import logging

import httpx
import pytest
import respx

from mcp_server.tarifas import (
    ApiFletesProvider,
    DobleLecturaProvider,
    DriveFlatFileProvider,
    FuenteTarifaNoDisponible,
    LocalRepositorioArchivos,
    TarifaRouter,
)
from mcp_server.tarifas.base import EntradaBuscarTarifa

_BASE = "https://api-fletes.interno"
_URL = f"{_BASE}/tarifas"
_FIXDIR_TARIFAS = __import__("pathlib").Path(__file__).parent / "fixtures" / "tarifas"


def _fila(origen: str, destino: str, tarifa: float, **extra: object) -> dict:
    return {"origen": origen, "destino": destino, "tarifa": tarifa, "moneda": "COP", **extra}


def _ok(*filas: dict) -> httpx.Response:
    return httpx.Response(200, json={"resultados": list(filas)})


def _provider() -> ApiFletesProvider:
    # reintentos=0 para que un 500/timeout falle de inmediato en las pruebas.
    return ApiFletesProvider(_BASE, api_token="secreto", reintentos=0)


# --- Match / no-match --------------------------------------------------------


@respx.mock
def test_match_exacto_devuelve_costo_y_fuente() -> None:
    respx.get(_URL).mock(return_value=_ok(_fila("YUMBO", "BUENAVENTURA", 2850000)))
    r = _provider().buscar("Nacional", "YUMBO", "BUENAVENTURA")
    assert r.encontrada is True
    assert r.costo == 2850000.0
    assert r.fuente == "api_fletes"


@respx.mock
def test_sin_resultados_no_inventa() -> None:
    respx.get(_URL).mock(return_value=httpx.Response(200, json={"resultados": []}))
    r = _provider().buscar("Nacional", "YUMBO", "NEIVA")
    assert r.encontrada is False
    assert r.costo is None
    assert r.candidatas == []


@respx.mock
def test_404_es_no_encontrada() -> None:
    respx.get(_URL).mock(return_value=httpx.Response(404))
    r = _provider().buscar("Nacional", "YUMBO", "NEIVA")
    assert r.encontrada is False
    assert r.fuente == "api_fletes"


@respx.mock
def test_ambiguo_devuelve_candidatas_max_5() -> None:
    filas = [_fila("BOGOTA", "MEDELLIN", 1000000 + i, tipo_vehiculo=f"V{i}") for i in range(7)]
    respx.get(_URL).mock(return_value=httpx.Response(200, json={"resultados": filas}))
    r = _provider().buscar("Nacional", "BOGOTA", "MEDELLIN")
    assert r.encontrada is False
    assert len(r.candidatas) == 5  # tope de candidatas


@respx.mock
def test_filtro_por_vehiculo_desambigua() -> None:
    filas = [
        _fila("BOGOTA", "CALI", 2000000, tipo_vehiculo="Turbo"),
        _fila("BOGOTA", "CALI", 3000000, tipo_vehiculo="Tractomula"),
    ]
    respx.get(_URL).mock(return_value=httpx.Response(200, json={"resultados": filas}))
    r = _provider().buscar("Nacional", "BOGOTA", "CALI", filtros={"tipo_vehiculo": "Tractomula"})
    assert r.encontrada is True
    assert r.costo == 3000000.0


# --- Resiliencia -------------------------------------------------------------


@respx.mock
def test_500_tras_reintentos_error_estructurable() -> None:
    respx.get(_URL).mock(return_value=httpx.Response(500))
    with pytest.raises(FuenteTarifaNoDisponible):
        _provider().buscar("Nacional", "YUMBO", "BUENAVENTURA")


@respx.mock
def test_timeout_no_tumba_el_mcp() -> None:
    respx.get(_URL).mock(side_effect=httpx.ConnectTimeout("timeout"))
    with pytest.raises(FuenteTarifaNoDisponible):
        _provider().buscar("Nacional", "YUMBO", "BUENAVENTURA")


@respx.mock
def test_reintenta_y_luego_responde() -> None:
    respx.get(_URL).mock(
        side_effect=[httpx.Response(503), _ok(_fila("YUMBO", "BUENAVENTURA", 2850000))]
    )
    prov = ApiFletesProvider(_BASE, reintentos=2)
    r = prov.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    assert r.encontrada is True and r.costo == 2850000.0


# --- Conmutación por línea (solo config) -------------------------------------


@respx.mock
def test_conmutacion_por_linea_enruta_al_provider_correcto() -> None:
    respx.get(_URL).mock(return_value=_ok(_fila("YUMBO", "BUENAVENTURA", 9990000)))
    drive = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXDIR_TARIFAS))
    router = TarifaRouter(
        providers={"drive_flat_files": drive, "api_fletes": _provider()},
        default="drive_flat_files",
        por_linea={"Internacional": "api_fletes"},
    )
    # Internacional → API (9.990.000); Nacional → Drive (CSV fixture, 2.850.000).
    r_api = router.buscar(
        EntradaBuscarTarifa(linea_negocio="Internacional", origen="YUMBO", destino="BUENAVENTURA")
    )
    assert r_api.fuente == "api_fletes" and r_api.costo == 9990000.0
    r_drive = router.buscar(
        EntradaBuscarTarifa(linea_negocio="Nacional", origen="YUMBO", destino="BUENAVENTURA")
    )
    assert r_drive.fuente == "drive_flat_files" and r_drive.costo == 2850000.0


# --- Doble lectura -----------------------------------------------------------


@respx.mock
def test_doble_lectura_sirve_principal_y_loguea_diferencia(
    caplog: pytest.LogCaptureFixture,
) -> None:
    # Drive (principal) dice 2.850.000; API (sombra) dice 9.990.000 → diferencia.
    respx.get(_URL).mock(return_value=_ok(_fila("YUMBO", "BUENAVENTURA", 9990000)))
    drive = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXDIR_TARIFAS))
    doble = DobleLecturaProvider(principal=drive, sombra=_provider())
    with caplog.at_level(logging.WARNING, logger="mcp.tarifas.api_fletes"):
        r = doble.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    # Se sirve el principal (Drive), pero queda registrada la diferencia.
    assert r.fuente == "drive_flat_files" and r.costo == 2850000.0
    assert any("DIFERENCIA" in m for m in caplog.messages)


@respx.mock
def test_doble_lectura_no_rompe_si_sombra_falla(caplog: pytest.LogCaptureFixture) -> None:
    respx.get(_URL).mock(return_value=httpx.Response(500))
    drive = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXDIR_TARIFAS))
    doble = DobleLecturaProvider(principal=drive, sombra=_provider())
    with caplog.at_level(logging.WARNING, logger="mcp.tarifas.api_fletes"):
        r = doble.buscar("Nacional", "YUMBO", "BUENAVENTURA")
    # La cotización sale igual con el principal aunque la sombra falle.
    assert r.encontrada is True and r.costo == 2850000.0
    assert any("sombra" in m for m in caplog.messages)
