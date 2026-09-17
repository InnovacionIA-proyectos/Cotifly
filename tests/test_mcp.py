"""Pruebas del MCP Server (Fase 2): auth, auditoría y contrato de error.

Cubren el DoD de la fase:
- auth bearer: sin token/token inválido → 401; token válido → conecta y lista.
- auditoría automática: invocar una tool deja fila en `eventos_herramienta`.
- error estructurado: un fallo devuelve `{"error": {...}}`, nunca traceback.
- cliente MCP conecta y lista herramientas.

Las de auth usan el transporte HTTP real (uvicorn en un hilo); las de auditoría
y error usan el cliente MCP in-process (más rápido y sin puerto). Requieren
Postgres real para verificar la persistencia de la auditoría.
"""

from __future__ import annotations

import asyncio
import socket
import threading
from collections.abc import Iterator
from pathlib import Path
from uuid import UUID

import httpx
import pytest
import uvicorn
from fastmcp import Client, FastMCP
from fastmcp.client.transports import StreamableHttpTransport
from fastmcp.exceptions import ToolError
from sqlalchemy import create_engine, delete, select
from sqlalchemy.orm import Session, sessionmaker

from mcp_server.auditoria import AuditoriaMiddleware
from mcp_server.correo import EnviadorCorreo, ServicioCorreo
from mcp_server.margen import ExportJsonFuente, MargenResolver
from mcp_server.server import build_server
from mcp_server.tarifas import (
    DriveFlatFileProvider,
    LocalRepositorioArchivos,
    TarifaRouter,
)
from shared.config import settings
from shared.models import EventoHerramienta
from tests.conftest import DATABASE_URL, requiere_pg

pytestmark = requiere_pg

_FIXTURES = __import__("pathlib").Path(__file__).parent / "fixtures"
_FIXDIR_TARIFAS = _FIXTURES / "tarifas"
_FIXTURE_SAP = _FIXTURES / "sap" / "rentab_cliente_muestra.json"


def _router_fixture() -> TarifaRouter:
    """Router sobre el CSV fixture (sin tocar Drive) para el DoD vía cliente MCP."""
    provider = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXDIR_TARIFAS))
    return TarifaRouter(providers={"drive_flat_files": provider}, default="drive_flat_files")


def _margen_fixture() -> MargenResolver:
    """Resolver sobre el export JSON fixture (sin tocar SAP) para el DoD."""
    return MargenResolver(ExportJsonFuente(_FIXTURE_SAP), sales_org_default="50000005")


def _correo_fixture(fallar: bool = False) -> ServicioCorreo:
    """Servicio de correo con transporte SMTP simulado (no toca la red)."""

    def _transporte(_msg: object) -> None:
        if fallar:
            raise OSError("SMTP rechazado")

    return ServicioCorreo(
        EnviadorCorreo("smtp.local", 587, "cotizaciones@boterosoto.com.co", transporte=_transporte)
    )


def _crear_cotizacion_con_pdf(folio: str, tmp_pdf: Path) -> str:
    """Crea (commit) una cotización con un PDF en disco; devuelve su id."""
    from shared.db import get_session
    from shared.repos import CotizacionRepo

    tmp_pdf.write_bytes(b"%PDF-1.4 prueba")
    with get_session() as s:
        cot = CotizacionRepo.crear(s, folio=folio, cliente_nombre="LONGYIN", pdf_path=str(tmp_pdf))
        return str(cot.id)


def _borrar_cotizacion_y_correos(folio: str) -> None:
    from sqlalchemy import delete

    from shared.db import get_session
    from shared.models import AjusteMargen, CorreoEnviado, Cotizacion

    with get_session() as s:
        ids = list(s.scalars(select(Cotizacion.id).where(Cotizacion.folio == folio)).all())
        for cid in ids:
            s.execute(delete(CorreoEnviado).where(CorreoEnviado.cotizacion_id == cid))
            s.execute(delete(AjusteMargen).where(AjusteMargen.cotizacion_id == cid))
        s.execute(delete(AjusteMargen).where(AjusteMargen.folio == folio))
        s.execute(delete(Cotizacion).where(Cotizacion.folio == folio))

_TOKEN = "test-token-fase2"


def _puerto_libre() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    puerto = s.getsockname()[1]
    s.close()
    return puerto


@pytest.fixture
def _token(monkeypatch: pytest.MonkeyPatch) -> str:
    """Fija el token del servidor para la prueba (singleton de settings)."""
    monkeypatch.setattr(settings, "mcp_auth_token", _TOKEN)
    return _TOKEN


@pytest.fixture
def servidor_http(_token: str) -> Iterator[str]:
    """Levanta el MCP por HTTP en un hilo y devuelve la URL base `/mcp/`."""
    puerto = _puerto_libre()
    app = build_server().http_app(transport="streamable-http")
    config = uvicorn.Config(app, host="127.0.0.1", port=puerto, log_level="warning")
    server = uvicorn.Server(config)
    hilo = threading.Thread(target=server.run, daemon=True)
    hilo.start()

    for _ in range(100):  # espera readiness (<=10 s)
        if server.started:
            break
        __import__("time").sleep(0.1)
    else:  # pragma: no cover - solo si el arranque falla
        raise RuntimeError("el servidor MCP no arrancó a tiempo")

    yield f"http://127.0.0.1:{puerto}/mcp/"

    server.should_exit = True
    hilo.join(timeout=10)


def _limpiar_auditoria(herramienta: str) -> None:
    """Borra filas de auditoría de una herramienta (la auditoría hace commit propio)."""
    eng = create_engine(DATABASE_URL, future=True)
    ses: Session = sessionmaker(bind=eng, future=True)()
    ses.execute(delete(EventoHerramienta).where(EventoHerramienta.herramienta == herramienta))
    ses.commit()
    ses.close()
    eng.dispose()


def _filas_auditoria(herramienta: str) -> list[EventoHerramienta]:
    eng = create_engine(DATABASE_URL, future=True)
    ses: Session = sessionmaker(bind=eng, future=True)()
    filas = list(
        ses.scalars(
            select(EventoHerramienta).where(EventoHerramienta.herramienta == herramienta)
        ).all()
    )
    ses.close()
    eng.dispose()
    return filas


# --- Auth (HTTP real) --------------------------------------------------------


async def test_auth_sin_bearer_rechaza(servidor_http: str) -> None:
    async with httpx.AsyncClient(follow_redirects=True) as h:
        r = await h.post(
            servidor_http,
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
            headers={"Accept": "application/json, text/event-stream"},
        )
    assert r.status_code == 401


async def test_auth_bearer_invalido_rechaza(servidor_http: str) -> None:
    async with httpx.AsyncClient(follow_redirects=True) as h:
        r = await h.post(
            servidor_http,
            json={"jsonrpc": "2.0", "id": 1, "method": "tools/list"},
            headers={
                "Accept": "application/json, text/event-stream",
                "Authorization": "Bearer token-invalido",
            },
        )
    assert r.status_code == 401


async def test_auth_bearer_valido_lista_tools(servidor_http: str) -> None:
    transporte = StreamableHttpTransport(servidor_http, auth=_TOKEN)
    async with Client(transporte) as c:
        tools = await c.list_tools()
    assert "ping" in {t.name for t in tools}


# --- Cliente in-process: listar, auditoría y error ---------------------------


async def test_cliente_inprocess_lista_tools() -> None:
    async with Client(build_server()) as c:
        tools = await c.list_tools()
    assert "ping" in {t.name for t in tools}


async def test_ping_ok_queda_auditado() -> None:
    _limpiar_auditoria("ping")
    try:
        async with Client(build_server()) as c:
            r = await c.call_tool("ping", {})
        assert r.structured_content == {"status": "ok", "servicio": "mcp", "entorno": "local"}
        await asyncio.sleep(0.3)  # la auditoría persiste en un threadpool
        filas = _filas_auditoria("ping")
        assert len(filas) == 1
        ev = filas[0]
        assert ev.herramienta == "ping"
        assert ev.exito is True
        assert ev.duracion_ms is not None and ev.duracion_ms >= 0
    finally:
        _limpiar_auditoria("ping")


async def test_error_estructurado_no_traceback() -> None:
    _limpiar_auditoria("ping")
    try:
        async with Client(build_server()) as c:
            r = await c.call_tool("ping", {"fallar": True})
        # Contrato de error: dict con clave "error", sin excepción cruda.
        assert "error" in r.structured_content
        assert r.structured_content["error"]["codigo"] == "PING_FORZADO"
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("ping")
        assert len(filas) == 1
        assert filas[0].exito is False  # el error de negocio se audita como fallo
    finally:
        _limpiar_auditoria("ping")


async def test_buscar_tarifa_match_exacto_auditado() -> None:
    """DoD F3A: costo correcto para un caso conocido, vía cliente MCP y auditado."""
    _limpiar_auditoria("buscar_tarifa")
    try:
        async with Client(build_server(router=_router_fixture())) as c:
            r = await c.call_tool(
                "buscar_tarifa",
                {"linea_negocio": "Nacional", "origen": "YUMBO", "destino": "BUENAVENTURA"},
            )
        salida = r.structured_content
        assert salida["encontrada"] is True
        assert salida["costo"] == 2850000.0
        assert salida["fuente"] == "drive_flat_files"
        assert salida["archivo_o_referencia"] == "tarifario_nacional_2026-07.csv"
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("buscar_tarifa")
        assert len(filas) == 1
        assert filas[0].exito is True
    finally:
        _limpiar_auditoria("buscar_tarifa")


async def test_buscar_tarifa_ambiguo_devuelve_candidatas() -> None:
    """DoD F3A: caso ambiguo → candidatas para que el LLM pregunte (no inventa)."""
    async with Client(build_server(router=_router_fixture())) as c:
        r = await c.call_tool(
            "buscar_tarifa",
            {"linea_negocio": "Nacional", "origen": "BOGOTA", "destino": "MEDELLIN"},
        )
    salida = r.structured_content
    assert salida["encontrada"] is False
    assert salida["costo"] is None
    assert len(salida["candidatas"]) == 2


async def test_buscar_tarifa_fuente_no_disponible_error_estructurado() -> None:
    async with Client(build_server(router=_router_fixture())) as c:
        r = await c.call_tool(
            "buscar_tarifa",
            {"linea_negocio": "Exportacion", "origen": "YUMBO", "destino": "BUENAVENTURA"},
        )
    assert r.structured_content["error"]["codigo"] == "TARIFA_FUENTE_NO_DISPONIBLE"


async def test_buscar_tarifa_config_invalida_error_estructurado() -> None:
    """Línea ruteada a un provider no registrado (p. ej. api_fletes diferido) → error."""
    provider = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXDIR_TARIFAS))
    router = TarifaRouter(
        providers={"drive_flat_files": provider},
        default="drive_flat_files",
        por_linea={"Internacional": "api_fletes"},
    )
    async with Client(build_server(router=router)) as c:
        r = await c.call_tool(
            "buscar_tarifa",
            {"linea_negocio": "Internacional", "origen": "BOGOTA", "destino": "QUITO"},
        )
    assert r.structured_content["error"]["codigo"] == "TARIFA_CONFIG_INVALIDA"


async def test_obtener_margen_sap_auditado() -> None:
    """DoD F4: margen correcto desde el export, vía cliente MCP y auditado."""
    _limpiar_auditoria("obtener_margen")
    try:
        async with Client(build_server(margen=_margen_fixture())) as c:
            r = await c.call_tool(
                "obtener_margen", {"linea_negocio": "Nacional", "cliente_id": "15927"}
            )
        salida = r.structured_content
        assert salida["origen"] == "sap_tm"
        assert salida["margen"] == 0.135
        assert salida["trq_type"] == "OPTN"
        assert salida["anomalo"] is False
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("obtener_margen")
        assert len(filas) == 1
        assert filas[0].exito is True
    finally:
        _limpiar_auditoria("obtener_margen")


async def test_obtener_margen_fallback_politica() -> None:
    async with Client(build_server(margen=_margen_fixture())) as c:
        r = await c.call_tool("obtener_margen", {"linea_negocio": "Nacional"})
    assert r.structured_content["origen"] == "politica_linea"
    assert r.structured_content["margen"] == 0.15


async def test_obtener_margen_anomalo() -> None:
    async with Client(build_server(margen=_margen_fixture())) as c:
        r = await c.call_tool(
            "obtener_margen", {"linea_negocio": "Nacional", "cliente_id": "5000"}
        )
    assert r.structured_content["anomalo"] is True
    assert r.structured_content["margen"] == -0.192


async def test_obtener_margen_linea_desconocida_error() -> None:
    async with Client(build_server(margen=_margen_fixture())) as c:
        r = await c.call_tool("obtener_margen", {"linea_negocio": "NoExiste"})
    assert r.structured_content["error"]["codigo"] == "MARGEN_LINEA_DESCONOCIDA"


async def test_obtener_margen_fuente_no_disponible_error() -> None:
    # Export ausente + cliente presente → la consulta a la fuente falla de forma controlada.
    resolver = MargenResolver(ExportJsonFuente("no/existe.json"), sales_org_default="50000005")
    async with Client(build_server(margen=resolver)) as c:
        r = await c.call_tool(
            "obtener_margen", {"linea_negocio": "Nacional", "cliente_id": "15927"}
        )
    assert r.structured_content["error"]["codigo"] == "MARGEN_FUENTE_NO_DISPONIBLE"


async def test_calcular_precio_auditado() -> None:
    """DoD F5: cálculo exacto del §5.1, vía cliente MCP y auditado."""
    _limpiar_auditoria("calcular_precio")
    try:
        async with Client(build_server()) as c:
            r = await c.call_tool(
                "calcular_precio", {"costo": 2850000, "margen": 0.135, "metodo": "margen"}
            )
        salida = r.structured_content
        assert salida["precio"] == 3294798
        assert salida["utilidad"] == 444798
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("calcular_precio")
        assert len(filas) == 1
        assert filas[0].exito is True
    finally:
        _limpiar_auditoria("calcular_precio")


async def test_calcular_precio_invalido_error() -> None:
    async with Client(build_server()) as c:
        r = await c.call_tool("calcular_precio", {"costo": 1000000, "margen": 1.0})
    assert r.structured_content["error"]["codigo"] == "PRECIO_INVALIDO"


async def test_enviar_correo_dod_auditado(tmp_path: Path) -> None:
    """DoD F7: envío con confirmación → enviado + trazado + auditado, vía cliente MCP."""
    _borrar_cotizacion_y_correos("PCN-CORREO-1")
    _limpiar_auditoria("enviar_correo")
    try:
        cot_id = _crear_cotizacion_con_pdf("PCN-CORREO-1", tmp_path / "PCN-CORREO-1.pdf")
        async with Client(build_server(correo=_correo_fixture())) as c:
            r = await c.call_tool(
                "enviar_correo",
                {
                    "cotizacion_id": cot_id,
                    "para": ["cliente@empresa.com"],
                    "asunto": "Cotización PCN-CORREO-1 — Botero Soto",
                    "confirmado_por": "comercial@boterosoto.com.co",
                },
            )
        salida = r.structured_content
        assert salida["enviado"] is True
        assert salida["registrado_en_bd"] is True
        assert salida["message_id"]
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("enviar_correo")
        assert len(filas) == 1
        assert filas[0].exito is True
    finally:
        _borrar_cotizacion_y_correos("PCN-CORREO-1")
        _limpiar_auditoria("enviar_correo")


async def test_enviar_correo_sin_confirmacion_rechaza(tmp_path: Path) -> None:
    _borrar_cotizacion_y_correos("PCN-CORREO-2")
    try:
        cot_id = _crear_cotizacion_con_pdf("PCN-CORREO-2", tmp_path / "PCN-CORREO-2.pdf")
        async with Client(build_server(correo=_correo_fixture())) as c:
            r = await c.call_tool(
                "enviar_correo",
                {
                    "cotizacion_id": cot_id,
                    "para": ["cliente@empresa.com"],
                    "asunto": "Sin confirmar",
                },
            )
        assert r.structured_content["error"]["codigo"] == "CONFIRMACION_REQUERIDA"
    finally:
        _borrar_cotizacion_y_correos("PCN-CORREO-2")


async def test_enviar_correo_smtp_fallido_se_traza(tmp_path: Path) -> None:
    _borrar_cotizacion_y_correos("PCN-CORREO-3")
    try:
        cot_id = _crear_cotizacion_con_pdf("PCN-CORREO-3", tmp_path / "PCN-CORREO-3.pdf")
        async with Client(build_server(correo=_correo_fixture(fallar=True))) as c:
            r = await c.call_tool(
                "enviar_correo",
                {
                    "cotizacion_id": cot_id,
                    "para": ["cliente@empresa.com"],
                    "asunto": "Fallo SMTP",
                    "confirmado_por": "comercial@boterosoto.com.co",
                },
            )
        assert r.structured_content["error"]["codigo"] == "CORREO_ENVIO_FALLIDO"
        # El fallo quedó trazado como 'fallido'.
        from shared.db import get_session
        from shared.models import CorreoEnviado

        with get_session() as s:
            fila = s.scalar(
                select(CorreoEnviado).where(CorreoEnviado.cotizacion_id == UUID(cot_id))
            )
        assert fila is not None
        assert fila.estado == "fallido"
        assert fila.error
    finally:
        _borrar_cotizacion_y_correos("PCN-CORREO-3")


async def test_enviar_correo_cotizacion_no_encontrada() -> None:
    import uuid

    async with Client(build_server(correo=_correo_fixture())) as c:
        r = await c.call_tool(
            "enviar_correo",
            {
                "cotizacion_id": str(uuid.uuid4()),
                "para": ["cliente@empresa.com"],
                "asunto": "X",
                "confirmado_por": "comercial@boterosoto.com.co",
            },
        )
    assert r.structured_content["error"]["codigo"] == "COTIZACION_NO_ENCONTRADA"


async def test_enviar_correo_pdf_no_encontrado(tmp_path: Path) -> None:
    _borrar_cotizacion_y_correos("PCN-CORREO-4")
    try:
        from shared.db import get_session
        from shared.repos import CotizacionRepo

        with get_session() as s:
            cot = CotizacionRepo.crear(s, folio="PCN-CORREO-4", pdf_path="/no/existe.pdf")
            cot_id = str(cot.id)
        async with Client(build_server(correo=_correo_fixture())) as c:
            r = await c.call_tool(
                "enviar_correo",
                {
                    "cotizacion_id": cot_id,
                    "para": ["cliente@empresa.com"],
                    "asunto": "X",
                    "confirmado_por": "comercial@boterosoto.com.co",
                },
            )
        assert r.structured_content["error"]["codigo"] == "CORREO_PDF_NO_ENCONTRADO"
    finally:
        _borrar_cotizacion_y_correos("PCN-CORREO-4")


async def test_enviar_correo_duplicado(tmp_path: Path) -> None:
    _borrar_cotizacion_y_correos("PCN-CORREO-5")
    try:
        cot_id = _crear_cotizacion_con_pdf("PCN-CORREO-5", tmp_path / "PCN-CORREO-5.pdf")
        payload = {
            "cotizacion_id": cot_id,
            "para": ["cliente@empresa.com"],
            "asunto": "Cotización",
            "confirmado_por": "comercial@boterosoto.com.co",
        }
        async with Client(build_server(correo=_correo_fixture())) as c:
            r1 = await c.call_tool("enviar_correo", payload)
            r2 = await c.call_tool("enviar_correo", payload)
        assert r1.structured_content["enviado"] is True
        assert r2.structured_content["error"]["codigo"] == "CORREO_DUPLICADO"
    finally:
        _borrar_cotizacion_y_correos("PCN-CORREO-5")


async def test_registrar_ajuste_margen_dod_auditado(tmp_path: Path) -> None:
    """DoD F8: ajuste calcula diferencia y persiste, vía cliente MCP y auditado."""
    _borrar_cotizacion_y_correos("PCN-AJ-1")
    _limpiar_auditoria("registrar_ajuste_margen")
    try:
        cot_id = _crear_cotizacion_con_pdf("PCN-AJ-1", tmp_path / "PCN-AJ-1.pdf")
        async with Client(build_server()) as c:
            r = await c.call_tool(
                "registrar_ajuste_margen",
                {
                    "cotizacion_id": cot_id,
                    "folio": "PCN-AJ-1",
                    "margen_politica": 0.135,
                    "margen_aplicado": 0.11,
                    "motivo": "Cliente estratégico",
                    "comercial": "ana@boterosoto.com.co",
                },
            )
        salida = r.structured_content
        assert salida["registrado"] is True
        assert salida["diferencia"] == pytest.approx(-0.025)
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("registrar_ajuste_margen")
        assert len(filas) == 1 and filas[0].exito is True
    finally:
        _borrar_cotizacion_y_correos("PCN-AJ-1")
        _limpiar_auditoria("registrar_ajuste_margen")


async def test_registrar_ajuste_sin_cotizacion_id() -> None:
    _borrar_cotizacion_y_correos("PCN-AJ-2")
    try:
        async with Client(build_server()) as c:
            r = await c.call_tool(
                "registrar_ajuste_margen",
                {
                    "folio": "PCN-AJ-2",
                    "margen_politica": 0.15,
                    "margen_aplicado": 0.15,
                    "motivo": "sin cambio",
                    "comercial": "ana@boterosoto.com.co",
                },
            )
        assert r.structured_content["registrado"] is True
        assert r.structured_content["diferencia"] == pytest.approx(0.0)
    finally:
        _borrar_cotizacion_y_correos("PCN-AJ-2")


async def test_registrar_ajuste_cotizacion_invalida() -> None:
    import uuid

    async with Client(build_server()) as c:
        r = await c.call_tool(
            "registrar_ajuste_margen",
            {
                "cotizacion_id": str(uuid.uuid4()),
                "margen_politica": 0.15,
                "margen_aplicado": 0.1,
                "motivo": "x",
                "comercial": "a@b.com",
            },
        )
    assert r.structured_content["error"]["codigo"] == "AJUSTE_COTIZACION_INVALIDA"


async def test_registrar_ajuste_id_malformado() -> None:
    async with Client(build_server()) as c:
        r = await c.call_tool(
            "registrar_ajuste_margen",
            {
                "cotizacion_id": "no-es-uuid",
                "margen_politica": 0.15,
                "margen_aplicado": 0.1,
                "motivo": "x",
                "comercial": "a@b.com",
            },
        )
    assert r.structured_content["error"]["codigo"] == "AJUSTE_ID_INVALIDO"


async def test_consultar_cotizacion_por_criterios(tmp_path: Path) -> None:
    _borrar_cotizacion_y_correos("PCN-CONS-1")
    _borrar_cotizacion_y_correos("PCN-CONS-2")
    try:
        from shared.db import get_session
        from shared.repos import CotizacionRepo

        with get_session() as s:
            CotizacionRepo.crear(
                s, folio="PCN-CONS-1", cliente_nombre="ACME SA",
                comercial_email="ana@boterosoto.com.co", estado="generada",
            )
            CotizacionRepo.crear(
                s, folio="PCN-CONS-2", cliente_nombre="OTRO",
                comercial_email="beto@boterosoto.com.co",
            )
        async with Client(build_server()) as c:
            async def _consulta(criterio: dict) -> dict:
                return (await c.call_tool("consultar_cotizacion", criterio)).structured_content

            por_folio = await _consulta({"folio": "PCN-CONS-1"})
            por_cliente = await _consulta({"cliente": "acme"})
            por_comercial = await _consulta({"comercial": "beto@boterosoto.com.co"})

        assert por_folio["total"] == 1
        assert por_folio["resultados"][0]["folio"] == "PCN-CONS-1"
        assert por_folio["resultados"][0]["cliente_nombre"] == "ACME SA"
        assert por_cliente["total"] == 1
        assert por_cliente["resultados"][0]["folio"] == "PCN-CONS-1"
        assert por_comercial["total"] == 1
        assert por_comercial["resultados"][0]["folio"] == "PCN-CONS-2"
    finally:
        _borrar_cotizacion_y_correos("PCN-CONS-1")
        _borrar_cotizacion_y_correos("PCN-CONS-2")


def test_contrato_siete_herramientas() -> None:
    """Contrato integral §5.1: las 7 herramientas exponen exactamente sus I/O.

    Todas aceptan además `space_id`/`thread_id` opcionales (los inyecta el puente
    del Orquestador para la auditoría por hilo; el LLM no tiene que pasarlos).
    """
    # Parámetros de auditoría comunes a todas las herramientas de negocio.
    _AUD = {"space_id", "thread_id"}
    esperado = {
        "buscar_tarifa": {
            "props": {"linea_negocio", "origen", "destino", "tipo_vehiculo", "tipo_carga",
                      "peso_kg"} | _AUD,
            "required": {"linea_negocio", "origen", "destino"},
        },
        "obtener_margen": {
            "props": {"linea_negocio", "cliente_id", "sales_org"} | _AUD,
            "required": {"linea_negocio"},
        },
        "calcular_precio": {
            "props": {"costo", "margen", "metodo"} | _AUD,
            "required": {"costo", "margen"},
        },
        "generar_pdf": {
            "props": {"cotizacion"} | _AUD,
            "required": {"cotizacion"},
        },
        "enviar_correo": {
            "props": {"cotizacion_id", "para", "cc", "asunto", "cuerpo", "confirmado_por"} | _AUD,
            "required": {"cotizacion_id", "para", "asunto"},
        },
        "registrar_ajuste_margen": {
            "props": {"cotizacion_id", "folio", "margen_politica", "margen_aplicado",
                      "motivo", "comercial"} | _AUD,
            "required": {"margen_politica", "margen_aplicado", "motivo", "comercial"},
        },
        "consultar_cotizacion": {
            "props": {"folio", "cliente", "comercial"} | _AUD,
            "required": set(),
        },
    }

    async def _tools() -> dict[str, object]:
        async with Client(build_server()) as c:
            return {t.name: t.inputSchema or {} for t in await c.list_tools()}

    esquemas = asyncio.run(_tools())
    # Las 7 herramientas de negocio están presentes.
    assert set(esperado).issubset(set(esquemas))
    for nombre, contrato in esperado.items():
        schema = esquemas[nombre]
        assert set(schema.get("properties", {})) == contrato["props"], nombre
        assert set(schema.get("required", [])) == contrato["required"], nombre


async def test_excepcion_no_controlada_se_enmascara_y_audita() -> None:
    """Excepción cruda en la tool → error enmascarado (sin detalle) + auditada como fallo."""
    mcp = FastMCP("test-fallo", mask_error_details=True)
    mcp.add_middleware(AuditoriaMiddleware())

    @mcp.tool
    def explota() -> dict[str, str]:
        raise ValueError("boom interno que no debe filtrarse")

    _limpiar_auditoria("explota")
    try:
        async with Client(mcp) as c:
            with pytest.raises(ToolError) as exc:
                await c.call_tool("explota", {})
        # El mensaje al cliente no contiene el detalle interno crudo.
        assert "boom interno" not in str(exc.value)
        await asyncio.sleep(0.3)
        filas = _filas_auditoria("explota")
        assert len(filas) == 1
        assert filas[0].exito is False
    finally:
        _limpiar_auditoria("explota")
