"""Tests de la capa de margen (Fase 4).

- Puros (sin Postgres): normalización, anomalía, ExportJsonFuente y ODataFuente
  (mockeada con respx).
- Con Postgres (`requiere_pg`, usan las semillas de mapeo/política de F1):
  el `MargenResolver` completo — SAP, fallback, anomalía, mapeo y conmutación
  export→OData con la misma salida.
"""

from __future__ import annotations

from pathlib import Path

import httpx
import pytest
import respx
from sqlalchemy.orm import Session

from mcp_server.margen import (
    EntradaObtenerMargen,
    ExportJsonFuente,
    FuenteMargenNoDisponible,
    MargenResolver,
    ODataFuente,
    construir_resolver,
    es_anomalo,
    normalizar_value,
)
from mcp_server.margen.servicio import MargenLineaDesconocida
from shared.config import settings
from shared.repos import MapeoTrqTypeRepo
from tests.conftest import requiere_pg

_FIXTURE = Path(__file__).parent / "fixtures" / "sap" / "rentab_cliente_muestra.json"
_SAP_BASE = "http://sap-tm-test.local/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV"


# --- normalización y anomalía (unidad) --------------------------------------


def test_normalizar_value() -> None:
    assert normalizar_value("13.5") == 0.135
    assert normalizar_value("5") == 0.05
    assert normalizar_value("-19.2") == -0.192


def test_es_anomalo() -> None:
    assert es_anomalo(0.135) is False
    assert es_anomalo(-0.192) is True  # negativo
    assert es_anomalo(0.999) is True  # > 60%
    assert es_anomalo(0.6) is False  # borde inclusivo


# --- ExportJsonFuente --------------------------------------------------------


def test_export_encuentra_y_no_encuentra() -> None:
    f = ExportJsonFuente(_FIXTURE)
    assert f.obtener_value("50000005", "OPTN", "15927") == "13.5"
    assert f.obtener_value("50000005", "OPTN", "99999") is None  # cliente inexistente


def test_export_archivo_ausente() -> None:
    f = ExportJsonFuente("no/existe/export.json")
    with pytest.raises(FuenteMargenNoDisponible):
        f.obtener_value("50000005", "OPTN", "15927")


def test_export_json_malformado(tmp_path: Path) -> None:
    ruta = tmp_path / "bad.json"
    ruta.write_text("{ esto no es json", encoding="utf-8")
    with pytest.raises(FuenteMargenNoDisponible):
        ExportJsonFuente(ruta).obtener_value("50000005", "OPTN", "15927")


# --- ODataFuente (respx) -----------------------------------------------------


@respx.mock
def test_odata_encuentra() -> None:
    payload = {"d": {"results": [{"SalesOrgId": "50000005", "TrqType": "OPTN",
                                  "OrderPartyId": "15927", "Value": "13.5"}]}}
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(return_value=httpx.Response(200, json=payload))
    f = ODataFuente(_SAP_BASE)
    assert f.obtener_value("50000005", "OPTN", "15927") == "13.5"


@respx.mock
def test_odata_cliente_inyectado() -> None:
    payload = {"d": {"results": [{"SalesOrgId": "50000005", "TrqType": "OPTN",
                                  "OrderPartyId": "15927", "Value": "13.5"}]}}
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(return_value=httpx.Response(200, json=payload))
    with httpx.Client() as cliente:
        f = ODataFuente(_SAP_BASE, user="u", password="p", client=cliente)
        assert f.obtener_value("50000005", "OPTN", "15927") == "13.5"


@respx.mock
def test_odata_vacio_devuelve_none() -> None:
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(
        return_value=httpx.Response(200, json={"d": {"results": []}})
    )
    assert ODataFuente(_SAP_BASE).obtener_value("50000005", "OPTN", "404") is None


@respx.mock
def test_odata_error_500_es_estructurado() -> None:
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(return_value=httpx.Response(500))
    with pytest.raises(FuenteMargenNoDisponible):
        ODataFuente(_SAP_BASE).obtener_value("50000005", "OPTN", "15927")


@respx.mock
def test_odata_timeout_es_estructurado() -> None:
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(side_effect=httpx.ConnectTimeout("timeout"))
    with pytest.raises(FuenteMargenNoDisponible):
        ODataFuente(_SAP_BASE).obtener_value("50000005", "OPTN", "15927")


# --- MargenResolver (Postgres, con semillas de F1) --------------------------


def _resolver() -> MargenResolver:
    return MargenResolver(ExportJsonFuente(_FIXTURE), sales_org_default="50000005")


def test_construir_resolver_export(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(settings, "sap_tm_modo", "export_json")
    monkeypatch.setattr(settings, "sap_tm_export_path", str(_FIXTURE))
    assert isinstance(construir_resolver()._fuente, ExportJsonFuente)


def test_construir_resolver_odata(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(settings, "sap_tm_modo", "odata")
    monkeypatch.setattr(settings, "sap_tm_url", _SAP_BASE)
    assert isinstance(construir_resolver()._fuente, ODataFuente)


@requiere_pg
def test_resolver_sap_tm(db_session: Session) -> None:
    r = _resolver().resolver(
        db_session, EntradaObtenerMargen(linea_negocio="Nacional", cliente_id="15927")
    )
    assert r.origen == "sap_tm"
    assert r.margen == 0.135
    assert r.trq_type == "OPTN"
    assert r.anomalo is False
    assert "15927" in r.detalle


@requiere_pg
def test_resolver_fallback_cliente_nuevo(db_session: Session) -> None:
    r = _resolver().resolver(db_session, EntradaObtenerMargen(linea_negocio="Nacional"))
    assert r.origen == "politica_linea"
    assert r.margen == 0.15  # semilla F1
    assert r.trq_type == "OPTN"


@requiere_pg
def test_resolver_fallback_cliente_sin_registro(db_session: Session) -> None:
    r = _resolver().resolver(
        db_session, EntradaObtenerMargen(linea_negocio="Nacional", cliente_id="99999")
    )
    assert r.origen == "politica_linea"


@requiere_pg
def test_resolver_marca_anomalo_negativo(db_session: Session) -> None:
    r = _resolver().resolver(
        db_session, EntradaObtenerMargen(linea_negocio="Nacional", cliente_id="5000")
    )
    assert r.origen == "sap_tm"
    assert r.margen == -0.192
    assert r.anomalo is True
    assert "ANÓMALO" in r.detalle


@requiere_pg
def test_resolver_marca_anomalo_atipico(db_session: Session) -> None:
    r = _resolver().resolver(
        db_session, EntradaObtenerMargen(linea_negocio="Importacion", cliente_id="5001")
    )
    assert r.margen == 0.999
    assert r.anomalo is True


@requiere_pg
def test_resolver_respeta_sales_org(db_session: Session) -> None:
    # OHUB/3301 vive en la sales_org 50000010; con la default (50000005) no está.
    entrada = EntradaObtenerMargen(
        linea_negocio="Almacenamiento", cliente_id="3301", sales_org="50000010"
    )
    r = _resolver().resolver(db_session, entrada)
    assert r.origen == "sap_tm"
    assert r.margen == 0.15
    assert r.trq_type == "OHUB"


@requiere_pg
def test_resolver_linea_desconocida(db_session: Session) -> None:
    with pytest.raises(MargenLineaDesconocida):
        _resolver().resolver(db_session, EntradaObtenerMargen(linea_negocio="NoExiste"))


@requiere_pg
def test_mapeo_todas_las_lineas(db_session: Session) -> None:
    esperado = {
        "OPTN": "Nacional",
        "OPDS": "Distribucion",
        "OPIM": "Importacion",
        "OPEX": "Exportacion",
        "OPTI": "Internacional",
        "OHUB": "Almacenamiento",
        "OH3P": "Almacenamiento3PL",
        "OPSC": "ITR",
    }
    for trq, linea in esperado.items():
        assert MapeoTrqTypeRepo.linea_de(db_session, trq) == linea
        assert MapeoTrqTypeRepo.trqtype_de(db_session, linea) == trq


@requiere_pg
@respx.mock
def test_conmutacion_export_a_odata_misma_salida(db_session: Session) -> None:
    """Con la fuente en OData (SAP mockeado) la salida es idéntica a la del export."""
    payload = {"d": {"results": [{"SalesOrgId": "50000005", "TrqType": "OPTN",
                                  "OrderPartyId": "15927", "Value": "13.5"}]}}
    respx.get(f"{_SAP_BASE}/RentabClienteSet").mock(return_value=httpx.Response(200, json=payload))
    resolver = MargenResolver(ODataFuente(_SAP_BASE), sales_org_default="50000005")
    r = resolver.resolver(
        db_session, EntradaObtenerMargen(linea_negocio="Nacional", cliente_id="15927")
    )
    assert r.origen == "sap_tm"
    assert r.margen == 0.135
    assert r.trq_type == "OPTN"
