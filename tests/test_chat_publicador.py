"""Tests del publicador en Chat (Fase 13): cards, flujo de ajuste y gating.

- Card de previsualización con costo·margen·precio y botones de control humano.
- Card con el link al PDF.
- Flujo de confirmación: "Ajusta margen a 11%" → registrar_ajuste_margen +
  calcular_precio y republica la card con el nuevo precio.
- Gating: sin confirmación explícita no se invoca enviar_correo, y la card de
  previsualización siempre pide confirmación.
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
import respx

from orquestador.agente import AgentRunner, HerramientasMCP, ResultadoTurno
from orquestador.cards import (
    PROMPT_CONFIRMACION,
    card_pdf,
    formato_cop,
    formato_pct,
    mensaje_para_resultado,
)
from orquestador.gchat import EventoChat
from orquestador.procesador import ProcesadorCotizacion
from orquestador.publicador import PublicadorChat, construir_publicador
from tests.conftest import requiere_pg
from tests.test_agente import _LLMFake, _resp, _servidor_precio, _tc


def _tc_calc(margen: float = 0.135, tid: str = "t1"):
    """tool_call de calcular_precio (costo fijo 2.850.000), para acortar los tests."""
    return _tc(tid, "calcular_precio", {"costo": 2850000, "margen": margen, "metodo": "margen"})


def _widgets(mensaje: dict) -> list[dict]:
    return mensaje["cardsV2"][0]["card"]["sections"][0]["widgets"]


def _valores(mensaje: dict) -> dict[str, str]:
    """Mapa topLabel→text de los decoratedText de la card."""
    return {
        w["decoratedText"]["topLabel"]: w["decoratedText"]["text"]
        for w in _widgets(mensaje)
        if "decoratedText" in w
    }


class _PublicadorFake(PublicadorChat):
    def __init__(self) -> None:
        self.publicados: list[dict[str, Any]] = []

        async def _enviar(space_id: str, body: dict[str, Any]) -> dict[str, Any]:
            self.publicados.append({"space_id": space_id, "body": body})
            return {"name": f"{space_id}/messages/generado"}

        super().__init__(_enviar)


# --- Formato -----------------------------------------------------------------


def test_formato_cop_y_pct() -> None:
    assert formato_cop(3294798) == "$3.294.798"
    assert formato_cop(2850000) == "$2.850.000"
    assert formato_pct(0.135) == "13,5%"
    assert formato_pct(0.11) == "11,0%"


# --- Selección de mensaje según el resultado ---------------------------------


def _resultado(salidas: list[dict], texto: str = "Listo.") -> ResultadoTurno:
    return ResultadoTurno(
        texto=texto, iteraciones=1, herramientas_invocadas=[s["nombre"] for s in salidas],
        detener_por="end_turn", salidas_herramientas=salidas,
    )


def test_mensaje_previsualizacion_desde_calcular_precio() -> None:
    salidas = [
        {"nombre": "obtener_margen", "salida": {"margen": 0.15, "origen": "politica_linea"}},
        {"nombre": "calcular_precio",
         "salida": {"costo": 2850000, "margen": 0.135, "precio": 3294798, "utilidad": 444798}},
    ]
    msg = mensaje_para_resultado(_resultado(salidas))
    vals = _valores(msg)
    assert vals["Costo"] == "$2.850.000"
    assert vals["Margen de política"] == "15,0%"   # de obtener_margen
    assert vals["Margen aplicado"] == "13,5%"      # de calcular_precio (difiere → se muestra)
    assert vals["Precio"] == "$3.294.798"
    assert msg["text"] == PROMPT_CONFIRMACION       # pide confirmación (gating)
    # Botones de control humano.
    textos_botones = [
        b["text"] for w in _widgets(msg) if "buttonList" in w for b in w["buttonList"]["buttons"]
    ]
    assert "Confirmar y generar PDF" in textos_botones
    assert "Ajustar margen" in textos_botones


def test_mensaje_pdf_desde_generar_pdf() -> None:
    salidas = [
        {"nombre": "calcular_precio",
         "salida": {"costo": 2850000, "margen": 0.135, "precio": 3294798, "utilidad": 444798}},
        {"nombre": "generar_pdf",
         "salida": {"folio": "PCN-19032026", "pdf_url": "https://x/pdfs/PCN-19032026.pdf",
                    "registrada_en_bd": True}},
    ]
    msg = mensaje_para_resultado(_resultado(salidas))
    assert msg["cardsV2"][0]["cardId"] == "pdf"
    botones = [b for w in _widgets(msg) if "buttonList" in w for b in w["buttonList"]["buttons"]]
    assert botones[0]["onClick"]["openLink"]["url"] == "https://x/pdfs/PCN-19032026.pdf"


def test_mensaje_texto_cuando_no_hay_precio_ni_pdf() -> None:
    msg = mensaje_para_resultado(_resultado([], texto="¿De qué origen sale la carga?"))
    assert msg == {"text": "¿De qué origen sale la carga?"}


def test_mensaje_ignora_salida_con_error() -> None:
    salidas = [{"nombre": "calcular_precio", "salida": {"error": {"codigo": "PRECIO_INVALIDO"}}}]
    msg = mensaje_para_resultado(_resultado(salidas, texto="No pude calcular."))
    assert msg == {"text": "No pude calcular."}


def test_card_pdf_sin_url() -> None:
    msg = card_pdf("PCN-1", None, "ok")
    assert not any("buttonList" in w for w in _widgets(msg))


# --- Publicador --------------------------------------------------------------


async def test_publicador_agrega_hilo_y_envia() -> None:
    pub = _PublicadorFake()
    await pub.publicar("spaces/X", "spaces/X/threads/1", {"text": "hola"})
    assert pub.publicados[0]["space_id"] == "spaces/X"
    assert pub.publicados[0]["body"]["thread"] == {"name": "spaces/X/threads/1"}
    assert pub.publicados[0]["body"]["text"] == "hola"


async def test_publicador_sin_space_falla() -> None:
    with pytest.raises(ValueError):
        await _PublicadorFake().publicar(None, "t", {"text": "x"})


async def test_construir_publicador_postea_a_chat_api(monkeypatch: pytest.MonkeyPatch) -> None:
    """El publicador de producción arma el POST correcto (URL, auth, hilo)."""

    class _Creds:
        token = "ya29.fake-token"

        def refresh(self, _req: object) -> None: ...

    monkeypatch.setattr(
        "google.oauth2.service_account.Credentials.from_service_account_file",
        lambda *a, **k: _Creds(),
    )
    with respx.mock:
        ruta = respx.post("https://chat.googleapis.com/v1/spaces/X/messages").mock(
            return_value=httpx.Response(200, json={"name": "spaces/X/messages/1"})
        )
        resp = await construir_publicador().publicar(
            "spaces/X", "spaces/X/threads/1", {"text": "hola"}
        )
    assert resp["name"] == "spaces/X/messages/1"
    req = ruta.calls[0].request
    assert req.headers["authorization"] == "Bearer ya29.fake-token"
    assert "messageReplyOption" in str(req.url)
    assert json.loads(req.content)["thread"] == {"name": "spaces/X/threads/1"}


# --- Runner final → card publicada (Chat mockeada) ---------------------------


async def test_runner_final_publica_card_previsualizacion() -> None:
    """Turno del agente (LLM mockeado) produce precio → se publica la card."""
    fake = _LLMFake(
        _resp("", [_tc_calc()]),
        _resp("Aquí tu previsualización."),
    )
    runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(_servidor_precio()))
    resultado = await runner.ejecutar_turno("Cotiza al margen de política.")

    pub = _PublicadorFake()
    await pub.publicar("spaces/X", "spaces/X/threads/1", mensaje_para_resultado(resultado))
    vals = _valores(pub.publicados[0]["body"])
    assert vals["Precio"] == "$3.294.798"
    assert pub.publicados[0]["body"]["text"] == PROMPT_CONFIRMACION


async def test_gating_sin_confirmacion_no_envia_correo() -> None:
    """Sin 'Confirmo/Envíalo', el turno no invoca enviar_correo y la card pide confirmar."""
    fake = _LLMFake(
        _resp("", [_tc_calc()]),
        _resp("Previsualización lista."),
    )
    runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(_servidor_precio()))
    resultado = await runner.ejecutar_turno("Cotiza YUMBO→BUN.")

    assert "enviar_correo" not in resultado.herramientas_invocadas
    assert mensaje_para_resultado(resultado)["text"] == PROMPT_CONFIRMACION


async def test_procesador_no_mensaje_no_publica() -> None:
    pub = _PublicadorFake()
    proc = ProcesadorCotizacion(runner=object(), publicador=pub)
    await proc(EventoChat(tipo="ADDED_TO_SPACE", space_id="spaces/X"))
    assert pub.publicados == []  # eventos no-mensaje no cotizan ni publican


@requiere_pg
async def test_procesador_corre_turno_y_publica(db_engine: object) -> None:
    """Camino completo del worker: evento → runner (mockeado) → publica la card."""
    space, thread = "spaces/PROC", "spaces/PROC/threads/1"
    fake = _LLMFake(
        _resp("", [_tc_calc()]),
        _resp("Previsualización."),
    )
    pub = _PublicadorFake()
    try:
        runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(_servidor_precio()))
        proc = ProcesadorCotizacion(runner=runner, publicador=pub)
        await proc(
            EventoChat(
                tipo="MESSAGE", space_id=space, thread_id=thread,
                texto="Cotiza YUMBO→BUN.", usuario_email="ana@boterosoto.com.co",
            )
        )
        assert len(pub.publicados) == 1
        publicado = pub.publicados[0]
        assert publicado["space_id"] == space
        assert publicado["body"]["thread"] == {"name": thread}
        assert _valores(publicado["body"])["Precio"] == "$3.294.798"
    finally:
        from sqlalchemy import delete

        from shared.db import get_session
        from shared.models import EventoConversacion, Sesion

        with get_session() as s:
            s.execute(delete(EventoConversacion).where(EventoConversacion.space_id == space))
            s.execute(delete(Sesion).where(Sesion.space_id == space))


# --- Flujo de ajuste de margen (MCP real, republica card) --------------------


@requiere_pg
async def test_flujo_ajuste_margen_republica(db_engine: object) -> None:
    """"Ajusta margen a 11%" → registrar_ajuste_margen + calcular_precio → nueva card."""
    from mcp_server.server import build_server

    fake = _LLMFake(
        _resp("", [_tc("t_aj", "registrar_ajuste_margen",
                       {"margen_politica": 0.135, "margen_aplicado": 0.11,
                        "motivo": "Negociación", "comercial": "ana@boterosoto.com.co"})]),
        _resp("", [_tc("t_cp", "calcular_precio",
                       {"costo": 2850000, "margen": 0.11, "metodo": "margen"})]),
        _resp("Recalculé con 11%."),
    )
    try:
        runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(build_server()))
        resultado = await runner.ejecutar_turno("Ajusta el margen a 11%.")

        assert "registrar_ajuste_margen" in resultado.herramientas_invocadas
        assert "calcular_precio" in resultado.herramientas_invocadas
        msg = mensaje_para_resultado(resultado)
        vals = _valores(msg)
        # Nuevo precio con margen 11%: 2.850.000 / (1-0.11) = 3.202.247.
        assert vals["Precio"] == "$3.202.247"
        assert vals["Margen de política"] == "11,0%"
    finally:
        from sqlalchemy import delete

        from shared.db import get_session
        from shared.models import AjusteMargen

        with get_session() as s:
            s.execute(
                delete(AjusteMargen).where(AjusteMargen.comercial_email == "ana@boterosoto.com.co")
            )
