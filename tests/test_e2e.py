"""Integración End-to-End del flujo del §8 (Fase 14): los 5 componentes juntos.

Externos en modo controlado: tarifas desde CSV fixture (Drive), margen desde el
export JSON (SAP), SMTP mockeado (buzón de prueba), Chat mockeado (publicador
fake) y **LLM guionizado** (un doble de `completar` con respuestas en cola; mismo
loop agéntico real, sin gastar tokens ni depender del proveedor). Cada "turno"
simula un mensaje del comercial; el procesador corre el worker (F12), así que
ejercita todo el camino de negocio.

Cubre: E2E feliz (solicitud → previsualización → ajuste → confirma → PDF → envío)
con trazabilidad completa en las 5 tablas; E2E sin tarifa (candidatas, no inventa);
E2E margen anómalo (se reporta); y las invariantes del flujo dinámico.
"""

from __future__ import annotations

import asyncio
import json
from email.message import EmailMessage
from pathlib import Path
from typing import Any

import pytest
from sqlalchemy import delete, select

from mcp_server.correo import EnviadorCorreo, ServicioCorreo
from mcp_server.margen import ExportJsonFuente, MargenResolver
from mcp_server.server import build_server
from mcp_server.tarifas import DriveFlatFileProvider, LocalRepositorioArchivos, TarifaRouter
from orquestador.agente import AgentRunner, HerramientasMCP
from orquestador.gchat import EventoChat
from orquestador.procesador import ProcesadorCotizacion
from orquestador.publicador import PublicadorChat
from tests.conftest import requiere_pg, requiere_weasyprint
from tests.test_agente import _resp, _tc

_FIXTURES = Path(__file__).parent / "fixtures"
_SPACE = "spaces/E2E"
_THREAD = "spaces/E2E/threads/longyin"
_COMERCIAL = "nvargas@boterosoto.com.co"
_FOLIO = "PCN-19032026"

pytestmark = requiere_pg


# --- Utilidades de guion del LLM ---------------------------------------------


class _LLMGuion:
    """Doble de `completar` reconfigurable: se le cargan respuestas por turno y las
    entrega en orden (respuestas estilo OpenAI: choices[0].message/finish_reason)."""

    def __init__(self) -> None:
        self.cola: list[Any] = []

    def cargar(self, respuestas: list[Any]) -> None:
        self.cola.extend(respuestas)

    async def __call__(self, **kw: Any) -> Any:
        return self.cola.pop(0)


def _tu(nombre: str, entrada: dict, tid: str = "toolu") -> Any:
    return _resp("", [_tc(tid, nombre, entrada)])


def _final(texto: str) -> Any:
    return _resp(texto)


# --- Dobles de los sistemas externos -----------------------------------------


def _router_fixture() -> TarifaRouter:
    provider = DriveFlatFileProvider(LocalRepositorioArchivos(_FIXTURES / "tarifas"))
    return TarifaRouter(providers={"drive_flat_files": provider}, default="drive_flat_files")


def _margen_fixture() -> MargenResolver:
    return MargenResolver(
        ExportJsonFuente(_FIXTURES / "sap" / "rentab_cliente_muestra.json"),
        sales_org_default="50000005",
    )


class _BuzonSmtp:
    """Transporte SMTP de prueba: captura los mensajes en lugar de enviarlos."""

    def __init__(self) -> None:
        self.enviados: list[EmailMessage] = []

    def __call__(self, msg: EmailMessage) -> None:
        self.enviados.append(msg)


class _ChatFake(PublicadorChat):
    def __init__(self) -> None:
        self.publicados: list[dict[str, Any]] = []

        async def _enviar(space_id: str, body: dict[str, Any]) -> dict[str, Any]:
            self.publicados.append({"space_id": space_id, "body": body})
            return {"name": f"{space_id}/messages/x"}

        super().__init__(_enviar)


def _valores_card(body: dict) -> dict[str, str]:
    widgets = body["cardsV2"][0]["card"]["sections"][0]["widgets"]
    return {
        w["decoratedText"]["topLabel"]: w["decoratedText"]["text"]
        for w in widgets
        if "decoratedText" in w
    }


async def _turno(
    proc: ProcesadorCotizacion, guion: _LLMGuion, texto: str, respuestas: list[Any]
) -> None:
    """Corre un turno del comercial con el LLM guionizado (respuestas en cola)."""
    guion.cargar(respuestas)
    await proc(
        EventoChat(
            tipo="MESSAGE", space_id=_SPACE, thread_id=_THREAD,
            texto=texto, usuario_email=_COMERCIAL,
        )
    )


def _limpiar() -> None:
    from shared.db import get_session
    from shared.models import (
        AjusteMargen,
        CorreoEnviado,
        Cotizacion,
        EventoConversacion,
        EventoHerramienta,
        Sesion,
    )

    with get_session() as s:
        ids = list(s.scalars(select(Cotizacion.id).where(Cotizacion.folio.like(f"{_FOLIO}%"))))
        for cid in ids:
            s.execute(delete(CorreoEnviado).where(CorreoEnviado.cotizacion_id == cid))
            s.execute(delete(AjusteMargen).where(AjusteMargen.cotizacion_id == cid))
        # HU-002.2: `comercial_email` ya no existe (ahora `usuario_id`, sin fuente
        # hasta ClientePro/HU-004) — se limpia por `folio`, que sí identifica el test.
        s.execute(delete(AjusteMargen).where(AjusteMargen.folio == _FOLIO))
        s.execute(delete(Cotizacion).where(Cotizacion.folio.like(f"{_FOLIO}%")))
        s.execute(delete(EventoConversacion).where(EventoConversacion.space_id == _SPACE))
        s.execute(delete(Sesion).where(Sesion.space_id == _SPACE))
        for tool in (
            "buscar_tarifa", "obtener_margen", "calcular_precio",
            "registrar_ajuste_margen", "generar_pdf", "enviar_correo",
        ):
            s.execute(delete(EventoHerramienta).where(EventoHerramienta.herramienta == tool))


# --- E2E feliz ---------------------------------------------------------------


@requiere_weasyprint
async def test_e2e_feliz_cotizar_y_enviar(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    """Comercial pega solicitud → previsualización → ajuste → confirma → PDF → envío.

    Verifica que las 5 tablas de trazabilidad reflejan todo el flujo (§8, DoD F14).
    """
    monkeypatch.setattr("shared.config.settings.pdf_storage_dir", str(tmp_path))
    buzon = _BuzonSmtp()
    correo = ServicioCorreo(
        EnviadorCorreo("smtp.local", 587, "cotizaciones@boterosoto.com.co", transporte=buzon)
    )
    servidor = build_server(router=_router_fixture(), margen=_margen_fixture(), correo=correo)
    guion = _LLMGuion()
    runner = AgentRunner(completar=guion, herramientas=HerramientasMCP(servidor))
    chat = _ChatFake()
    proc = ProcesadorCotizacion(runner=runner, publicador=chat)
    solicitud = json.loads(
        (_FIXTURES / "cotizacion" / "ejemplo-solicitud.json").read_text(encoding="utf-8")
    )

    _limpiar()
    try:
        # Turno 1 — solicitud → buscar_tarifa → obtener_margen → calcular_precio → preview.
        await _turno(
            proc,
            guion,
            "Cotiza un flete Nacional de YUMBO a BUENAVENTURA para LONGYIN (cliente 15927).",
            [
                _tu("buscar_tarifa",
                    {"linea_negocio": "Nacional", "origen": "YUMBO", "destino": "BUENAVENTURA"}),
                _tu("obtener_margen", {"linea_negocio": "Nacional", "cliente_id": "15927"}),
                _tu("calcular_precio", {"costo": 2850000, "margen": 0.135, "metodo": "margen"}),
                _final("Previsualización: costo $2.850.000, margen 13,5%, precio $3.294.798."),
            ],
        )
        vals1 = _valores_card(chat.publicados[-1]["body"])
        assert vals1["Precio"] == "$3.294.798"
        assert vals1["Margen de política"] == "13,5%"

        # Turno 2 — ajuste de margen a 11% → registrar_ajuste + recálculo → republica.
        await _turno(
            proc,
            guion,
            "Ajusta el margen a 11% por negociación.",
            [
                _tu("registrar_ajuste_margen",
                    {"margen_politica": 0.135, "margen_aplicado": 0.11,
                     "motivo": "Negociación cliente estratégico", "comercial": _COMERCIAL,
                     "folio": _FOLIO}),
                _tu("calcular_precio", {"costo": 2850000, "margen": 0.11, "metodo": "margen"}),
                _final("Recalculado con 11%: precio $3.202.247."),
            ],
        )
        assert _valores_card(chat.publicados[-1]["body"])["Precio"] == "$3.202.247"

        # Turno 3 — "Confirmo" → generar_pdf → card con link al PDF.
        await _turno(
            proc,
            guion,
            "Confirmo, genera el PDF.",
            [
                _tu("generar_pdf",
                    {"cotizacion": solicitud, "space_id": _SPACE, "thread_id": _THREAD}),
                _final("Cotización generada, aquí el PDF."),
            ],
        )
        card_pdf = chat.publicados[-1]["body"]
        assert card_pdf["cardsV2"][0]["cardId"] == "pdf"
        assert Path(tmp_path / f"{_FOLIO}.pdf").is_file()  # el PDF quedó en el volumen

        # Recupera el cotizacion_id real generado para autorizar el envío.
        from shared.db import get_session
        from shared.repos import CotizacionRepo

        with get_session() as s:
            cot = CotizacionRepo.obtener_por_folio(s, _FOLIO)
            assert cot is not None
            cot_id = str(cot.id)

        # Turno 4 — "Envíalo" con autorización explícita → enviar_correo con PDF.
        await _turno(
            proc,
            guion,
            "Envíalo al cliente a nicolas@longyin.com.",
            [
                _tu("enviar_correo",
                    {"cotizacion_id": cot_id, "para": ["nicolas@longyin.com"],
                     "asunto": f"Cotización {_FOLIO} — Botero Soto", "confirmado_por": _COMERCIAL}),
                _final("Listo, la cotización fue enviada al cliente."),
            ],
        )

        # --- Trazabilidad: las 5 tablas reflejan el flujo ---
        await asyncio.sleep(0.5)  # la auditoría persiste en un threadpool
        from shared.models import (
            AjusteMargen,
            CorreoEnviado,
            Cotizacion,
            EventoConversacion,
            EventoHerramienta,
        )

        with get_session() as s:
            # cotizaciones
            cots = list(s.scalars(select(Cotizacion).where(Cotizacion.folio == _FOLIO)))
            assert len(cots) == 1 and cots[0].pdf_path

            # ajustes_margen — HU-002.2: se identifica por `folio` (ya no hay
            # `comercial_email`; `usuario_id` siempre queda NULL sin ClientePro/HU-004).
            ajustes = list(
                s.scalars(select(AjusteMargen).where(AjusteMargen.folio == _FOLIO))
            )
            assert len(ajustes) == 1
            assert float(ajustes[0].diferencia) == pytest.approx(-0.025)

            # correos_enviados
            correos = list(
                s.scalars(select(CorreoEnviado).where(CorreoEnviado.cotizacion_id == cots[0].id))
            )
            assert len(correos) == 1
            assert correos[0].estado == "enviado"
            # HU-002.2: `confirmado_por` en BD es entero, sin fuente hasta HU-004 — el
            # envío en sí sí usó `_COMERCIAL` como remitente real (ver `mcp_server/correo.py`).
            assert correos[0].confirmado_por is None

            # eventos_conversacion (4 turnos: user/assistant/herramienta)
            convs = list(
                s.scalars(
                    select(EventoConversacion).where(EventoConversacion.space_id == _SPACE)
                )
            )
            roles = {c.rol for c in convs}
            assert {"user", "assistant", "herramienta"} <= roles
            assert sum(1 for c in convs if c.rol == "user") == 4  # cuatro mensajes del comercial

            # eventos_herramienta (auditoría automática de las herramientas usadas)
            auditadas = set(
                s.scalars(
                    select(EventoHerramienta.herramienta).where(
                        EventoHerramienta.herramienta.in_(
                            ["buscar_tarifa", "obtener_margen", "calcular_precio",
                             "registrar_ajuste_margen", "generar_pdf", "enviar_correo"]
                        )
                    )
                )
            )
            assert {"buscar_tarifa", "obtener_margen", "calcular_precio",
                    "registrar_ajuste_margen", "generar_pdf", "enviar_correo"} <= auditadas

        # El correo salió por el buzón de prueba, con el PDF adjunto.
        assert len(buzon.enviados) == 1
        adjuntos = list(buzon.enviados[0].iter_attachments())
        assert len(adjuntos) == 1
        assert adjuntos[0].get_content_type() == "application/pdf"
    finally:
        _limpiar()


# --- E2E sin tarifa ----------------------------------------------------------


async def test_e2e_sin_tarifa_pide_candidatas_no_inventa() -> None:
    """buscar_tarifa sin match exacto → candidatas; el bot no inventa ni calcula precio."""
    servidor = build_server(router=_router_fixture(), margen=_margen_fixture())
    guion = _LLMGuion()
    runner = AgentRunner(completar=guion, herramientas=HerramientasMCP(servidor))
    chat = _ChatFake()
    proc = ProcesadorCotizacion(runner=runner, publicador=chat)
    _limpiar()
    try:
        await _turno(
            proc,
            guion,
            "Cotiza Nacional de BOGOTA a MEDELLIN.",
            [
                _tu("buscar_tarifa",
                    {"linea_negocio": "Nacional", "origen": "BOGOTA", "destino": "MEDELLIN"}),
                _final("No hay tarifa exacta; te paso las candidatas o dame el costo."),
            ],
        )
        # Se publicó texto (no card de precio): no se inventó tarifa ni se calculó precio.
        publicado = chat.publicados[-1]["body"]
        assert "cardsV2" not in publicado
        assert "candidat" in publicado["text"].lower()

        from shared.db import get_session
        from shared.models import EventoConversacion

        with get_session() as s:
            convs = list(
                s.scalars(select(EventoConversacion).where(EventoConversacion.space_id == _SPACE))
            )
        # La herramienta se ejecutó pero calcular_precio no (invariante: no inventa).
        traza = [c.herramientas for c in convs if c.rol == "herramienta"]
        nombres = {r["nombre"] for h in traza if h for r in h["resultados"]}
        assert "buscar_tarifa" in nombres
        assert "calcular_precio" not in nombres
    finally:
        _limpiar()


# --- E2E margen anómalo ------------------------------------------------------


async def test_e2e_margen_anomalo_se_reporta() -> None:
    """SAP devuelve un margen atípico → obtener_margen lo marca `anomalo` y el bot lo reporta."""
    servidor = build_server(router=_router_fixture(), margen=_margen_fixture())
    guion = _LLMGuion()
    runner = AgentRunner(completar=guion, herramientas=HerramientasMCP(servidor))
    chat = _ChatFake()
    proc = ProcesadorCotizacion(runner=runner, publicador=chat)
    _limpiar()
    try:
        await _turno(
            proc,
            guion,
            "Cotiza Nacional YUMBO→BUENAVENTURA para el cliente 5000.",
            [
                _tu("buscar_tarifa",
                    {"linea_negocio": "Nacional", "origen": "YUMBO", "destino": "BUENAVENTURA"}),
                _tu("obtener_margen", {"linea_negocio": "Nacional", "cliente_id": "5000"}),
                _final("El margen de SAP es atípico (-19,2%); lo dejo para validación."),
            ],
        )
        from shared.db import get_session
        from shared.models import EventoConversacion

        with get_session() as s:
            convs = list(
                s.scalars(select(EventoConversacion).where(EventoConversacion.space_id == _SPACE))
            )
        salidas = {
            r["nombre"]: r["salida"]
            for c in convs if c.rol == "herramienta" and c.herramientas
            for r in c.herramientas["resultados"]
        }
        # El margen vino marcado como anómalo y NO se generó PDF (se reporta, no se aplica ciego).
        assert salidas["obtener_margen"]["anomalo"] is True
        assert "generar_pdf" not in salidas
        reporte = chat.publicados[-1]["body"]["text"]
        assert "-19" in reporte or "atípico" in reporte
    finally:
        _limpiar()
