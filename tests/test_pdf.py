"""Tests de `generar_pdf` (Fase 6).

- Puros: generación de HTML equivalente a la skill y bloques condicionales.
- Postgres: versionado de folio (idempotencia) y persistencia.
- weasyprint (gated por libs nativas): render real + nº de páginas, y el DoD del
  ciclo vía cliente MCP (PDF en disco + fila en `cotizaciones`, auditado).
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from fastmcp import Client
from sqlalchemy import create_engine, delete, select
from sqlalchemy.orm import Session, sessionmaker

from mcp_server.pdf import construir_html, render_pdf
from mcp_server.server import build_server
from shared.config import settings
from shared.models import Cotizacion, EventoHerramienta
from shared.repos import CotizacionRepo
from tests.conftest import DATABASE_URL, requiere_pg, requiere_weasyprint

_EJEMPLO = Path(__file__).parent / "fixtures" / "cotizacion" / "ejemplo-solicitud.json"


def _ejemplo() -> dict:
    return json.loads(_EJEMPLO.read_text(encoding="utf-8"))


# --- HTML: equivalencia estructural con la skill ----------------------------


def test_html_contiene_datos_de_la_cotizacion() -> None:
    html = construir_html(_ejemplo())
    assert "LONGYIN" in html
    assert "$3.000.000" in html and "$3.600.000" in html  # tarifas del ejemplo
    assert "YUMBO" in html  # origen
    assert "Nathaly Vargas Garcia" in html  # firma comercial
    assert "data:image/png;base64," in html  # logo corporativo embebido
    assert "Facturación y forma de pago" in html and "Vigencia" in html  # condiciones


def test_html_determinista() -> None:
    assert construir_html(_ejemplo()) == construir_html(_ejemplo())


def test_bloque_aduanero_solo_internacional() -> None:
    nacional = _ejemplo()
    assert "Datos aduaneros" not in construir_html(nacional)

    internacional = _ejemplo()
    internacional["linea_negocio"] = "Internacional"
    internacional["aduana"] = {"modalidad": "ANDINO", "frontera": "Ipiales", "arancel": "5%"}
    html = construir_html(internacional)
    assert "Datos aduaneros (ANDINO)" in html
    assert "Ipiales" in html


def test_bloque_extradimensionada_y_refrigerada() -> None:
    d = _ejemplo()
    d["extradimensionada"] = {"peso_kg": 40000, "ancho": "3m", "largo": "20m"}
    d["refrigerada"] = {"temperatura": "-18°C"}
    html = construir_html(d)
    assert "extradimensionada" in html.lower()
    assert "-18°C" in html


def test_multi_seccion_usa_armar_multi() -> None:
    d = _ejemplo()
    d.pop("tarifa", None)
    d["secciones"] = [
        {"titulo": "TRAMO 1", "origen": "YUMBO",
         "filas": [{"destino": "BUN", "vehiculo_peso": "Tractomula", "tarifa": 3000000}]},
        {"titulo": "TRAMO 2", "origen": "BOGOTA",
         "filas": [{"destino": "MED", "vehiculo_peso": "Sencillo", "tarifa": 1200000}]},
    ]
    html = construir_html(d)
    assert "TRAMO 1" in html and "TRAMO 2" in html


# --- Versionado de folio (idempotencia) -------------------------------------


@requiere_pg
def test_folio_versionado(db_session: Session) -> None:
    assert CotizacionRepo.folio_versionado(db_session, "PCN-F6") == "PCN-F6"
    CotizacionRepo.crear(db_session, folio="PCN-F6")
    assert CotizacionRepo.folio_versionado(db_session, "PCN-F6") == "PCN-F6-v2"
    CotizacionRepo.crear(db_session, folio="PCN-F6-v2")
    assert CotizacionRepo.folio_versionado(db_session, "PCN-F6") == "PCN-F6-v3"


# --- Render real (requiere weasyprint) --------------------------------------


@requiere_weasyprint
def test_render_pdf_genera_archivo_y_paginas(tmp_path: Path) -> None:
    destino = tmp_path / "cotizacion.pdf"
    paginas = render_pdf(construir_html(_ejemplo()), destino)
    assert destino.exists()
    assert destino.stat().st_size > 0
    assert paginas >= 3  # portada/carta + tarifa + condiciones (2 saltos de página)


# --- DoD del ciclo vía cliente MCP (requiere weasyprint + Postgres) ----------


def _borrar_cotizaciones(prefijo: str) -> None:
    eng = create_engine(DATABASE_URL, future=True)
    s: Session = sessionmaker(bind=eng, future=True)()
    s.execute(delete(Cotizacion).where(Cotizacion.folio.like(f"{prefijo}%")))
    s.execute(delete(EventoHerramienta).where(EventoHerramienta.herramienta == "generar_pdf"))
    s.commit()
    s.close()
    eng.dispose()


def _cotizaciones(prefijo: str) -> list[Cotizacion]:
    eng = create_engine(DATABASE_URL, future=True)
    s: Session = sessionmaker(bind=eng, future=True)()
    filas = list(
        s.scalars(select(Cotizacion).where(Cotizacion.folio.like(f"{prefijo}%"))).all()
    )
    s.close()
    eng.dispose()
    return filas


@requiere_pg
@requiere_weasyprint
async def test_generar_pdf_dod_via_mcp(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")
    _borrar_cotizaciones("PCN-19032026")
    try:
        async with Client(build_server()) as c:
            r = await c.call_tool("generar_pdf", {"cotizacion": _ejemplo()})
        salida = r.structured_content
        assert salida["registrada_en_bd"] is True
        assert salida["folio"] == "PCN-19032026"
        assert salida["pdf_url"].endswith("/pdfs/PCN-19032026.pdf")
        assert (tmp_path / "PCN-19032026.pdf").exists()  # PDF en el volumen

        filas = _cotizaciones("PCN-19032026")
        assert len(filas) == 1
        assert filas[0].cliente_nombre == "LONGYIN"
        # HU-002.2: `payload` (JSONB) se eliminó, sin consumidor real.
        assert filas[0].pdf_path.endswith("PCN-19032026.pdf")
        # HU-002.2: `estado` se dividió en `estado_costeo`/`estado_envio`.
        assert filas[0].estado_envio == "sin_enviar"
    finally:
        _borrar_cotizaciones("PCN-19032026")


@requiere_pg
@requiere_weasyprint
async def test_generar_pdf_idempotencia_versiona(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")
    _borrar_cotizaciones("PCN-19032026")
    try:
        async with Client(build_server()) as c:
            r1 = await c.call_tool("generar_pdf", {"cotizacion": _ejemplo()})
            r2 = await c.call_tool("generar_pdf", {"cotizacion": _ejemplo()})
        assert r1.structured_content["folio"] == "PCN-19032026"
        assert r2.structured_content["folio"] == "PCN-19032026-v2"
        assert len(_cotizaciones("PCN-19032026")) == 2
        assert (tmp_path / "PCN-19032026.pdf").exists()
        assert (tmp_path / "PCN-19032026-v2.pdf").exists()
    finally:
        _borrar_cotizaciones("PCN-19032026")


@requiere_pg
async def test_generar_pdf_persiste_trazabilidad_completa(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, db_engine: object
) -> None:
    """F1.1: `generar_pdf` guarda costo/márgenes/precio/destino/fuentes (§7.3) y
    `consultar_cotizacion` los devuelve. Se mockea el render (sin weasyprint)."""
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")

    def _render_falso(_html: str) -> tuple[bytes, int]:
        return b"%PDF-1.4 fake", 1

    # El servidor importa render_pdf_bytes de mcp_server.pdf en build_server().
    monkeypatch.setattr("mcp_server.pdf.render_pdf_bytes", _render_falso)

    _borrar_cotizaciones("PCN-TRZ")
    try:
        payload = _ejemplo()
        payload.update(
            {
                "referencia": "PCN-TRZ",
                "costo": 3000000,
                "margen_politica": 0.135,
                "margen_aplicado": 0.11,
                "precio_final": 3370787,
                "fuente_tarifa": "drive_flat_files",
                "origen_margen": "sap_tm",
                "cliente_id_sap": "15927",
            }
        )
        async with Client(build_server()) as c:
            r = await c.call_tool("generar_pdf", {"cotizacion": payload})
            assert r.structured_content["registrada_en_bd"] is True
            consulta = await c.call_tool("consultar_cotizacion", {"folio": "PCN-TRZ"})
            cons = consulta.structured_content

        fila = _cotizaciones("PCN-TRZ")[0]
        assert float(fila.costo) == 3000000.0
        assert float(fila.margen_politica) == 0.135
        assert float(fila.margen_aplicado) == 0.11
        assert float(fila.precio_final) == 3370787.0
        # HU-002.2: `destino`/`fuente_tarifa` se eliminaron de `cotizaciones` (Nexo
        # los reemplaza, HU-005) — el resto de la trazabilidad se sigue guardando.
        assert fila.origen_margen == "sap_tm"
        assert fila.cliente_id_sap == "15927"

        # consultar_cotizacion refleja la trazabilidad completa (no null).
        res = cons["resultados"][0]
        assert res["precio_final"] == 3370787.0
        assert res["margen_aplicado"] == 0.11
        assert res["origen_margen"] == "sap_tm"
    finally:
        _borrar_cotizaciones("PCN-TRZ")


@requiere_pg
async def test_generar_pdf_tolera_cliente_como_objeto(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, db_engine: object
) -> None:
    """Regresión: el LLM a veces manda `cliente` como objeto ({"nombre", "id"}).

    La herramienta debe normalizarlo (nombre al documento/BD, id a cliente_id_sap)
    en vez de reventar el INSERT con "cannot adapt type 'dict'".
    """
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")

    def _render_falso(_html: str) -> tuple[bytes, int]:
        return b"%PDF-1.4 fake", 1

    monkeypatch.setattr("mcp_server.pdf.render_pdf_bytes", _render_falso)
    _borrar_cotizaciones("PCN-OBJ")
    try:
        payload = _ejemplo()
        payload["referencia"] = "PCN-OBJ"
        payload["cliente"] = {"nombre": "LONGYIN", "id": "15927"}  # objeto, no string
        async with Client(build_server()) as c:
            r = await c.call_tool("generar_pdf", {"cotizacion": payload})
        salida = r.structured_content
        assert "error" not in salida, salida
        assert salida["registrada_en_bd"] is True

        fila = _cotizaciones("PCN-OBJ")[0]
        assert fila.cliente_nombre == "LONGYIN"          # nombre extraído del objeto
        assert fila.cliente_id_sap == "15927"            # id extraído del objeto
    finally:
        _borrar_cotizaciones("PCN-OBJ")


async def test_generar_pdf_sin_referencia_error() -> None:
    async with Client(build_server()) as c:
        r = await c.call_tool("generar_pdf", {"cotizacion": {"cliente": "X"}})
    assert r.structured_content["error"]["codigo"] == "PDF_PAYLOAD_INVALIDO"


@requiere_pg
async def test_generar_pdf_render_no_disponible_error(monkeypatch: pytest.MonkeyPatch) -> None:
    """Si weasyprint no puede renderizar, la tool devuelve error estructurado."""
    from mcp_server.pdf import generador
    from mcp_server.pdf.generador import PdfRenderNoDisponible

    def _boom(_html: str) -> object:
        raise PdfRenderNoDisponible("sin librerías nativas")

    monkeypatch.setattr(generador, "_documento", _boom)
    async with Client(build_server()) as c:
        r = await c.call_tool("generar_pdf", {"cotizacion": _ejemplo()})
    assert r.structured_content["error"]["codigo"] == "PDF_RENDER_NO_DISPONIBLE"
