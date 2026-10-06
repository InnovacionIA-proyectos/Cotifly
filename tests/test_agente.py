"""Tests del Agent Runner (Fase 11): loop de tool calling, registro y (opcional) LLM real.

- El loop se prueba con un **doble de `completar`** (respuestas estilo OpenAI) y un
  MCP mínimo in-process: un `tool_call` simulado dispara la tool MCP correcta, su
  salida real vuelve al modelo y el loop cierra en la respuesta final. Sin red, sin
  tokens y sin depender de ningún SDK de proveedor.
- El registro en `eventos_conversacion` se prueba contra Postgres real.
- La integración semi-real (marca `llm`) consume el LLM configurado (LiteLLM) con
  presupuesto acotado; se salta si no hay API key.
"""

from __future__ import annotations

import json
import os
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace
from typing import Any

import pytest
from fastmcp import FastMCP
from sqlalchemy.orm import Session

from mcp_server.precio import calcular_precio as _calcular_precio
from orquestador.agente import AgentRunner, HerramientasMCP, MaxIteracionesExcedidas
from orquestador.sesiones import GestorSesiones
from shared.config import settings
from shared.repos import ConversacionRepo
from tests.conftest import requiere_pg

_HAY_API_KEY = bool(os.environ.get("GEMINI_API_KEY") or settings.llm_api_key)


def _servidor_precio() -> FastMCP:
    """MCP mínimo con una tool pura (sin BD, sin auditoría) para aislar el loop."""
    mcp = FastMCP("test-agente")

    @mcp.tool
    def calcular_precio(costo: float, margen: float, metodo: str = "margen") -> dict:
        return _calcular_precio(costo, margen, metodo).model_dump()

    return mcp


# --- Dobles del LLM (respuestas estilo OpenAI) -------------------------------


def _tc(id: str, name: str, args: dict) -> SimpleNamespace:
    """Simula una tool_call de una respuesta OpenAI (arguments = JSON string)."""
    return SimpleNamespace(
        id=id, type="function",
        function=SimpleNamespace(name=name, arguments=json.dumps(args)),
    )


def _resp(content: str | None = None, tcs: list[Any] | None = None) -> SimpleNamespace:
    """Respuesta estilo OpenAI: choices[0].message + finish_reason."""
    msg = SimpleNamespace(content=content, tool_calls=tcs or None)
    finish = "tool_calls" if tcs else "stop"
    return SimpleNamespace(choices=[SimpleNamespace(message=msg, finish_reason=finish)])


class _LLMFake:
    """Doble de `completar`: entrega respuestas en orden y captura cada llamada."""

    def __init__(self, *respuestas: Any) -> None:
        self._it = iter(respuestas)
        self.llamadas: list[dict[str, Any]] = []

    async def __call__(self, **kw: Any) -> Any:
        # Copia los mensajes al capturarlos: la lista se muta en cada iteración del
        # loop, así que guardar la referencia mostraría el estado final, no el de esta
        # llamada.
        registro = dict(kw)
        if "messages" in registro:
            registro["messages"] = [dict(m) for m in registro["messages"]]
        self.llamadas.append(registro)
        return next(self._it)


class _LLMRepite:
    """Doble que siempre pide la misma tool (para forzar el tope de iteraciones)."""

    def __init__(self, resp: Any) -> None:
        self._resp = resp

    async def __call__(self, **kw: Any) -> Any:
        return self._resp


_RESP_TOOL_USE = _resp(
    "Calculo el precio con el margen de la política.",
    [_tc("toolu_1", "calcular_precio", {"costo": 2850000, "margen": 0.135, "metodo": "margen"})],
)
_RESP_FINAL = _resp("El precio final es 3.294.798 COP.")
_RESP_FINAL2 = _resp("Con margen 11% queda en 3.202.247 COP.")


def _reloj_incremental():
    """Timestamps distintos y ordenados (1 s por llamada) para orden estable en BD."""
    estado = {"n": 0}
    t0 = datetime(2026, 7, 17, 12, 0, 0, tzinfo=UTC)

    def _r() -> datetime:
        estado["n"] += 1
        return t0 + timedelta(seconds=estado["n"])

    return _r


# --- Loop de tool calling (LLM mockeado, sin BD) -----------------------------


async def test_loop_tool_use_invoca_mcp_y_cierra() -> None:
    fake = _LLMFake(_RESP_TOOL_USE, _RESP_FINAL)
    runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(_servidor_precio()))
    resultado = await runner.ejecutar_turno("Cotiza YUMBO→BUN al margen de política.")

    # El loop cerró en la respuesta final tras invocar la tool correcta.
    assert resultado.herramientas_invocadas == ["calcular_precio"]
    assert resultado.iteraciones == 2
    assert resultado.detener_por == "stop"
    assert "3.294.798" in resultado.texto

    # La segunda llamada al LLM lleva el mensaje 'tool' con la salida REAL del MCP.
    segunda = fake.llamadas[1]["messages"]
    tool_msg = segunda[-1]
    assert tool_msg["role"] == "tool"
    assert tool_msg["tool_call_id"] == "toolu_1"
    assert "3294798" in tool_msg["content"]  # precio calculado por calcular_precio


async def test_tools_declaradas_excluyen_ping() -> None:
    """El puente traduce las tools del MCP al formato function-calling y omite `ping`."""
    from mcp_server.server import build_server

    async with HerramientasMCP(build_server()) as h:
        tools = await h.listar()
    nombres = {t["function"]["name"] for t in tools}
    assert "ping" not in nombres
    assert {"buscar_tarifa", "obtener_margen", "calcular_precio"}.issubset(nombres)
    # Contrato estándar: type=function y function con name/description/parameters.
    assert all(t["type"] == "function" for t in tools)
    assert all({"name", "description", "parameters"} <= set(t["function"]) for t in tools)


async def test_max_iteraciones_excedidas() -> None:
    """Si el modelo nunca cierra (siempre tool_call), el loop corta con error claro."""
    runner = AgentRunner(
        completar=_LLMRepite(_RESP_TOOL_USE),
        herramientas=HerramientasMCP(_servidor_precio()),
        max_iteraciones=3,
    )
    with pytest.raises(MaxIteracionesExcedidas):
        await runner.ejecutar_turno("bucle")


# --- Registro en eventos_conversacion (Postgres real) ------------------------


@requiere_pg
async def test_registra_cada_turno_en_conversacion(db_session: Session) -> None:
    runner = AgentRunner(
        completar=_LLMFake(_RESP_TOOL_USE, _RESP_FINAL),
        herramientas=HerramientasMCP(_servidor_precio()),
        gestor=GestorSesiones(reloj=_reloj_incremental()),
    )
    await runner.ejecutar_turno(
        "Cotiza YUMBO→BUN.",
        session=db_session,
        space_id="spaces/AG",
        thread_id="threads/1",
        usuario_email="ana@boterosoto.com.co",
    )

    turnos = ConversacionRepo.historial(db_session, "spaces/AG", "threads/1")
    assert [t.rol for t in turnos] == ["user", "assistant", "herramienta", "assistant"]
    # user: mensaje original con su autor.
    assert turnos[0].contenido == "Cotiza YUMBO→BUN."
    # HU-002.2: `usuario_email` ya no se persiste (columna real es `usuario_id`, entero,
    # sin fuente hasta ClientePro/HU-004) — el parámetro se sigue aceptando para el
    # contexto del prompt, pero el turno queda sin identidad en BD.
    assert turnos[0].usuario_id is None
    # assistant que pide la tool: queda trazado qué invocó.
    assert turnos[1].herramientas["invocaciones"][0]["nombre"] == "calcular_precio"
    # turno de herramienta: queda trazada la salida real.
    assert turnos[2].herramientas["resultados"][0]["nombre"] == "calcular_precio"
    assert turnos[2].herramientas["resultados"][0]["salida"]["precio"] == 3294798
    # assistant final: el resumen de previsualización.
    assert turnos[3].contenido and "3.294.798" in turnos[3].contenido


@requiere_pg
async def test_memoria_entre_turnos_alimenta_contexto(db_session: Session) -> None:
    """F10+F11: el segundo turno arrastra el historial (la ventana deslizante) como
    contexto del LLM, conservando la solicitud original y las respuestas previas."""
    fake = _LLMFake(_RESP_TOOL_USE, _RESP_FINAL, _RESP_FINAL2)
    runner = AgentRunner(
        completar=fake,
        herramientas=HerramientasMCP(_servidor_precio()),
        gestor=GestorSesiones(reloj=_reloj_incremental()),
    )
    clave = {"session": db_session, "space_id": "spaces/MEM", "thread_id": "threads/1"}
    await runner.ejecutar_turno("Cotiza YUMBO→BUN.", **clave)
    r2 = await runner.ejecutar_turno("Ahora ajústalo a margen 11%.", **clave)

    assert "3.202.247" in r2.texto
    # La 3.ª llamada (turno 2) llevó el historial: solicitud original + respuestas previas.
    tercera = fake.llamadas[2]["messages"]
    roles_textos = [(m["role"], m["content"]) for m in tercera]
    assert roles_textos[0][0] == "system"  # el system va primero
    assert ("user", "Cotiza YUMBO→BUN.") in roles_textos  # solicitud original conservada
    assert ("assistant", "El precio final es 3.294.798 COP.") in roles_textos  # respuesta previa
    assert roles_textos[-1] == ("user", "Ahora ajústalo a margen 11%.")  # mensaje actual


# --- Integración semi-real (LLM configurado vía LiteLLM, presupuesto acotado) ----


@pytest.mark.llm
@requiere_pg
@pytest.mark.skipif(not _HAY_API_KEY, reason="sin API key del LLM (prueba con tokens)")
async def test_integracion_llm_flujo_cotizacion(db_session: Session) -> None:
    """DoD F11: con MCP real (fixtures) + LLM real, una solicitud llega al resumen
    de previsualización ejecutando buscar_tarifa → obtener_margen → calcular_precio."""
    from pathlib import Path

    from mcp_server.margen import ExportJsonFuente, MargenResolver
    from mcp_server.server import build_server
    from mcp_server.tarifas import DriveFlatFileProvider, LocalRepositorioArchivos, TarifaRouter
    from orquestador.agente import _completar_litellm

    fixtures = Path(__file__).parent / "fixtures"
    router = TarifaRouter(
        providers={"drive_flat_files": DriveFlatFileProvider(
            LocalRepositorioArchivos(fixtures / "tarifas")
        )},
        default="drive_flat_files",
    )
    margen = MargenResolver(
        ExportJsonFuente(fixtures / "sap" / "rentab_cliente_muestra.json"),
        sales_org_default="50000005",
    )
    runner = AgentRunner(
        completar=_completar_litellm(),
        herramientas=HerramientasMCP(build_server(router=router, margen=margen)),
        gestor=GestorSesiones(),
        max_tokens=2048,
        max_iteraciones=8,
    )

    mensaje = (
        "Cotiza un flete Nacional de YUMBO a BUENAVENTURA para el cliente LONGYIN "
        "(cliente_id 15927). Usa el margen de la política/SAP. Muéstrame el resumen "
        "de previsualización (costo, margen y precio). No generes PDF ni envíes correo."
    )
    resultado = await runner.ejecutar_turno(
        mensaje, session=db_session, space_id="spaces/LLM", thread_id="threads/1"
    )

    assert "buscar_tarifa" in resultado.herramientas_invocadas
    assert "calcular_precio" in resultado.herramientas_invocadas
    assert resultado.texto  # produjo un resumen no vacío
