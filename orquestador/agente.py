"""Agent Runner — loop agéntico (LLM agnóstico) + MCP (§4.3 · F11).

Aquí se enchufa el LLM real. El proveedor es **agnóstico** vía **LiteLLM**: el
modelo se elige por config (`LLM_MODEL`, p. ej. `gemini/gemini-2.5-flash`,
`anthropic/claude-opus-4-8`, `openai/gpt-4o`) sin tocar este código. El runner
actúa como **puente al MCP interno** (patrón *runner-puente*): declara las
herramientas del MCP al LLM en formato de *tool calling* estándar (OpenAI), recibe
las `tool_calls`, las ejecuta contra el MCP y devuelve los resultados, iterando
hasta la respuesta final. Cada turno (usuario, asistente y herramientas) queda en
`eventos_conversacion` (§7.3); el historial se recorta con la ventana deslizante
del gestor de sesiones (F10).

La función `completar` (una llamada al LLM) se **inyecta**: en producción envuelve
`litellm.acompletion` (import perezoso, extra `llm`); en pruebas se pasa un doble,
de modo que el loop se prueba sin red ni tokens y sin depender de ningún SDK.
"""

from __future__ import annotations

import asyncio
import json
import time
from collections.abc import Awaitable, Callable
from typing import Any

from pydantic import BaseModel
from sqlalchemy.orm import Session

from orquestador.prompt import SYSTEM_COTIZADOR
from orquestador.sesiones import GestorSesiones
from shared.config import settings

# Una llamada al LLM: recibe (model, messages, tools, max_tokens, api_key) y
# devuelve una respuesta estilo OpenAI (choices[0].message / .finish_reason).
Completar = Callable[..., Awaitable[Any]]


class LimitadorRPM:
    """Límite de peticiones por minuto al LLM (token bucket simple, asíncrono).

    Evita el 429 de la capa gratuita: si se supera `rpm`, la siguiente llamada
    espera hasta que se libere una ranura. `rpm=0` desactiva el límite. Es un
    parámetro del **adaptador del modelo**: cada proveedor puede fijar el suyo.
    El reloj es inyectable para pruebas deterministas.
    """

    def __init__(self, rpm: int, reloj: Callable[[], float] = time.monotonic) -> None:
        self._intervalo = 60.0 / rpm if rpm > 0 else 0.0
        self._reloj = reloj
        self._proxima = 0.0
        self._lock = asyncio.Lock()

    async def esperar(self) -> None:
        if self._intervalo <= 0:
            return
        async with self._lock:
            ahora = self._reloj()
            espera = self._proxima - ahora
            if espera > 0:
                await asyncio.sleep(espera)
                ahora = self._reloj()
            self._proxima = max(ahora, self._proxima) + self._intervalo


class ResultadoTurno(BaseModel):
    """Salida de un turno del agente."""

    texto: str  # respuesta final / resumen de previsualización para el comercial
    iteraciones: int  # cuántas llamadas al LLM tomó cerrar el turno
    herramientas_invocadas: list[str]  # nombres de tools ejecutadas, en orden
    detener_por: str  # stop | tool_calls | length | ... (finish_reason final)
    # Salida estructurada de cada tool ejecutada ({nombre, salida}), en orden. La
    # usa el publicador (F13) para armar cards con datos reales (precio, PDF…).
    salidas_herramientas: list[dict[str, Any]] = []

    def ultima_salida(self, nombre: str) -> dict[str, Any] | None:
        """Última salida de la herramienta `nombre` en el turno, o None."""
        for registro in reversed(self.salidas_herramientas):
            if registro["nombre"] == nombre:
                salida = registro["salida"]
                return salida if isinstance(salida, dict) else None
        return None


class MaxIteracionesExcedidas(RuntimeError):
    """El loop no alcanzó una respuesta final dentro del tope configurado."""


class HerramientasMCP:
    """Puente al MCP Server vía el cliente FastMCP oficial.

    Traduce las herramientas del MCP al formato de *tool calling* estándar (OpenAI,
    el que consume LiteLLM) y despacha las invocaciones. Es un *async context
    manager*: abre un cliente conectado para la duración de un turno. `ping` se
    excluye por defecto (es la tool de humo).
    """

    def __init__(self, servidor: Any, excluir: set[str] | None = None) -> None:
        self._servidor = servidor
        self._excluir = excluir if excluir is not None else {"ping"}
        self._cli: Any = None
        # Contexto del hilo (space_id/thread_id) que se inyecta en cada tool call
        # para la auditoría por hilo (§7.3). Los parámetros que cada herramienta
        # acepta se descubren en `listar()`.
        self._contexto: dict[str, Any] = {}
        self._acepta: dict[str, set[str]] = {}

    def fijar_contexto(self, **contexto: Any) -> None:
        """Fija valores (p. ej. space_id/thread_id) a inyectar en cada invocación."""
        self._contexto = {k: v for k, v in contexto.items() if v is not None}

    async def __aenter__(self) -> HerramientasMCP:
        from fastmcp import Client

        self._cli = Client(self._servidor)
        await self._cli.__aenter__()
        return self

    async def __aexit__(self, *exc: Any) -> None:
        cli, self._cli = self._cli, None
        if cli is not None:
            await cli.__aexit__(*exc)

    async def listar(self) -> list[dict[str, Any]]:
        """Herramientas del MCP en el formato `tools` estándar (function calling)."""
        tools = await self._cli.list_tools()
        visibles = [t for t in tools if t.name not in self._excluir]
        self._acepta = {
            t.name: set((t.inputSchema or {}).get("properties", {}).keys()) for t in visibles
        }
        return [
            {
                "type": "function",
                "function": {
                    "name": t.name,
                    "description": t.description or "",
                    "parameters": t.inputSchema or {"type": "object", "properties": {}},
                },
            }
            for t in visibles
        ]

    async def invocar(self, nombre: str, argumentos: dict[str, Any]) -> dict[str, Any]:
        """Ejecuta una tool del MCP y devuelve su salida estructurada (dict).

        Inyecta el contexto del hilo (space_id/thread_id) en los parámetros que la
        herramienta acepte y que el LLM no haya provisto, para que la auditoría por
        hilo (§7.3) no dependa de que el modelo los pase.
        """
        aceptados = self._acepta.get(nombre, set())
        args = dict(argumentos)
        for clave, valor in self._contexto.items():
            if clave in aceptados and clave not in args:
                args[clave] = valor
        resultado = await self._cli.call_tool(nombre, args)
        salida = resultado.structured_content
        return salida if isinstance(salida, dict) else {"resultado": salida}


def _parse_args(raw: Any) -> dict[str, Any]:
    """Argumentos de una tool_call (JSON string o ya dict) → dict tolerante."""
    if isinstance(raw, dict):
        return raw
    try:
        valor = json.loads(raw or "{}")
    except (ValueError, TypeError):
        return {}
    return valor if isinstance(valor, dict) else {}


def _mensaje(resp: Any) -> Any:
    """Mensaje del asistente de una respuesta estilo OpenAI."""
    return resp.choices[0].message


def _finish(resp: Any) -> str:
    return resp.choices[0].finish_reason or "stop"


def _tool_calls(msg: Any) -> list[Any]:
    return list(getattr(msg, "tool_calls", None) or [])


def _asistente_dict(msg: Any, tcs: list[Any]) -> dict[str, Any]:
    """Serializa el turno assistant (con sus tool_calls) para reenviarlo al LLM."""
    d: dict[str, Any] = {"role": "assistant", "content": getattr(msg, "content", None)}
    if tcs:
        d["tool_calls"] = [
            {
                "id": tc.id,
                "type": "function",
                "function": {"name": tc.function.name, "arguments": tc.function.arguments},
            }
            for tc in tcs
        ]
    return d


def _system_con_contexto(
    base: str,
    *,
    usuario_email: str | None,
    usuario_nombre: str | None,
    fecha_hoy: str | None,
) -> str:
    """Antepone a la skill un bloque con la identidad del comercial y la fecha (§4.3).

    Así la cotización y el correo se firman con el comercial real que interactúa, y
    el `confirmado_por` de `enviar_correo` sale de este correo. Si no hay datos de
    usuario (p. ej. pruebas sin sesión), devuelve la skill sin cambios.
    """
    if not (usuario_email or usuario_nombre):
        return base
    lineas = ["# Contexto del comercial"]
    if usuario_nombre:
        lineas.append(f"- Nombre: {usuario_nombre}")
    if usuario_email:
        lineas.append(f"- Correo (usar como `confirmado_por` al enviar): {usuario_email}")
    if fecha_hoy:
        lineas.append(f"- Fecha de hoy (para el folio PCN-DDMMAAAA): {fecha_hoy}")
    return "\n".join(lineas) + "\n\n" + base


class AgentRunner:
    """Corre el loop de *tool calling* contra el LLM, con el MCP como herramientas.

    La llamada al LLM (`completar`) y las herramientas (`herramientas`) se inyectan:
    en pruebas se pasa un doble de `completar` y un MCP con fixtures. Si se pasan
    `session`/`space_id`/`thread_id`, cada turno se persiste y el historial alimenta
    el contexto.
    """

    def __init__(
        self,
        completar: Completar,
        herramientas: HerramientasMCP,
        gestor: GestorSesiones | None = None,
        modelo: str | None = None,
        system: str | None = None,
        max_tokens: int | None = None,
        max_iteraciones: int | None = None,
        api_key: str | None = None,
    ) -> None:
        self._completar = completar
        self._herramientas = herramientas
        self._gestor = gestor or GestorSesiones()
        self._modelo = modelo or settings.llm_model
        self._system = system or SYSTEM_COTIZADOR
        self._max_tokens = max_tokens or settings.llm_max_tokens
        self._max_iter = max_iteraciones or settings.agente_max_iteraciones
        self._api_key = api_key if api_key is not None else settings.llm_api_key

    async def ejecutar_turno(
        self,
        mensaje_usuario: str,
        *,
        session: Session | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
        usuario_email: str | None = None,
        usuario_nombre: str | None = None,
        fecha_hoy: str | None = None,
    ) -> ResultadoTurno:
        """Procesa un mensaje del comercial hasta la respuesta final del agente."""
        system_turno = _system_con_contexto(
            self._system,
            usuario_email=usuario_email,
            usuario_nombre=usuario_nombre,
            fecha_hoy=fecha_hoy,
        )
        messages: list[dict[str, Any]] = [{"role": "system", "content": system_turno}]
        if session is not None and space_id is not None and thread_id is not None:
            # El turno del usuario se guarda primero; luego el historial (ventana
            # deslizante F10) ya lo incluye como último mensaje.
            self._gestor.registrar_mensaje(
                session, space_id, thread_id, "user", mensaje_usuario, usuario_email
            )
            messages.extend(self._messages_desde_historial(session, space_id, thread_id))
        else:
            messages.append({"role": "user", "content": mensaje_usuario})

        invocadas: list[str] = []
        salidas: list[dict[str, Any]] = []
        async with self._herramientas as h:
            h.fijar_contexto(space_id=space_id, thread_id=thread_id)
            tools = await h.listar()
            for iteracion in range(1, self._max_iter + 1):
                resp = await self._completar(
                    model=self._modelo,
                    messages=messages,
                    tools=tools,
                    max_tokens=self._max_tokens,
                    api_key=self._api_key,
                )
                msg = _mensaje(resp)
                tcs = _tool_calls(msg)
                texto = (getattr(msg, "content", None) or "").strip()
                messages.append(_asistente_dict(msg, tcs))

                herr = (
                    {
                        "invocaciones": [
                            {
                                "nombre": tc.function.name,
                                "input": _parse_args(tc.function.arguments),
                            }
                            for tc in tcs
                        ]
                    }
                    if tcs
                    else None
                )
                self._quizas_registrar(
                    session, space_id, thread_id, "assistant", texto or None, herramientas=herr
                )

                if not tcs:
                    return ResultadoTurno(
                        texto=texto,
                        iteraciones=iteracion,
                        herramientas_invocadas=invocadas,
                        detener_por=_finish(resp),
                        salidas_herramientas=salidas,
                    )

                # Ejecuta cada tool_call contra el MCP y arma los mensajes 'tool'.
                traza: list[dict[str, Any]] = []
                for tc in tcs:
                    entrada = _parse_args(tc.function.arguments)
                    salida = await h.invocar(tc.function.name, entrada)
                    invocadas.append(tc.function.name)
                    messages.append(
                        {
                            "role": "tool",
                            "tool_call_id": tc.id,
                            "content": json.dumps(salida, ensure_ascii=False, default=str),
                        }
                    )
                    traza.append({"nombre": tc.function.name, "salida": salida})
                salidas.extend(traza)
                self._quizas_registrar(
                    session, space_id, thread_id, "herramienta", None,
                    herramientas={"resultados": traza},
                )

        raise MaxIteracionesExcedidas(
            f"El agente no cerró el turno en {self._max_iter} iteraciones."
        )

    def _quizas_registrar(
        self,
        session: Session | None,
        space_id: str | None,
        thread_id: str | None,
        rol: str,
        contenido: str | None,
        herramientas: dict | None = None,
    ) -> None:
        """Persiste el turno solo si hay sesión (space+thread); si no, no-op."""
        if session is None or space_id is None or thread_id is None:
            return
        self._gestor.registrar_mensaje(
            session, space_id, thread_id, rol, contenido, herramientas=herramientas
        )

    def _messages_desde_historial(
        self, session: Session, space_id: str, thread_id: str
    ) -> list[dict[str, Any]]:
        """Historial de sesión como `messages` de texto (user/assistant).

        Las `tool_calls`/resultados de turnos previos no se reconstruyen: se conserva
        el hilo de texto (suficiente para el contexto conversacional) y la ventana
        deslizante mantiene siempre la solicitud original.
        """
        messages: list[dict[str, Any]] = []
        for turno in self._gestor.historial(session, space_id, thread_id):
            if turno.rol == "user" and turno.contenido:
                messages.append({"role": "user", "content": turno.contenido})
            elif turno.rol == "assistant" and turno.contenido:
                messages.append({"role": "assistant", "content": turno.contenido})
        return messages


def _completar_litellm(rpm: int | None = None, num_retries: int | None = None) -> Completar:
    """`completar` de producción: envuelve `litellm.acompletion` (extra `llm`).

    Parámetros del adaptador del modelo (por proveedor):
    - `rpm`: tope de peticiones por minuto (limitador local, evita el 429).
    - `num_retries`: reintentos con backoff que hace LiteLLM ante 429/errores.
    Import perezoso para no exigir LiteLLM en entornos que no llaman al LLM real.
    """
    limitador = LimitadorRPM(settings.llm_rpm if rpm is None else rpm)
    reintentos = settings.llm_num_retries if num_retries is None else num_retries
    # Cadena de respaldo: si el modelo principal falla/agota cuota, LiteLLM prueba
    # el siguiente (misma petición). Todos comparten la misma api_key (OpenRouter).
    fallbacks = [m.strip() for m in settings.llm_fallbacks.split(",") if m.strip()]

    async def _c(
        *,
        model: str,
        messages: list[dict[str, Any]],
        tools: list[dict[str, Any]],
        max_tokens: int,
        api_key: str,
    ) -> Any:
        import os

        import litellm

        # Multi-proveedor: exporta las claves por proveedor a las env vars que LiteLLM
        # resuelve según el prefijo del modelo. Permite que la cadena de fallback mezcle
        # proveedores (nvidia_nim/…, openrouter/…) sin forzar una api_key única.
        if settings.openrouter_api_key:
            os.environ["OPENROUTER_API_KEY"] = settings.openrouter_api_key
        if settings.nvidia_nim_api_key:
            os.environ["NVIDIA_NIM_API_KEY"] = settings.nvidia_nim_api_key
        multi_proveedor = bool(settings.openrouter_api_key or settings.nvidia_nim_api_key)

        await limitador.esperar()  # respeta el RPM antes de llamar al proveedor
        kwargs: dict[str, Any] = {
            "model": model,
            "messages": messages,
            "max_tokens": max_tokens,
            "num_retries": reintentos,  # backoff exponencial ante 429/transitorios
        }
        if tools:
            kwargs["tools"] = tools
        # En multi-proveedor NO se pasa api_key: LiteLLM la toma por proveedor de las env
        # vars. En modo single-provider (legacy) se pasa la LLM_API_KEY explícita.
        if api_key and not multi_proveedor:
            kwargs["api_key"] = api_key
        if fallbacks:
            kwargs["fallbacks"] = fallbacks
        return await litellm.acompletion(**kwargs)

    return _c


def construir_runner(
    servidor: Any | None = None, gestor: GestorSesiones | None = None
) -> AgentRunner:
    """Runner de producción: LLM real (LiteLLM) + MCP como caja de herramientas.

    Decisión de red (F15): el runner es un **puente al MCP interno**. Si `MCP_URL`
    está configurada (despliegue con contenedores separados), se conecta por
    **Streamable HTTP con bearer** al MCP del overlay interno (nunca expuesto); si
    no, usa el MCP **in-process** (dev/tests). `servidor` inyectado tiene prioridad.
    """
    if servidor is not None:
        objetivo: Any = servidor
    elif settings.mcp_url:
        from fastmcp.client.transports import StreamableHttpTransport

        objetivo = StreamableHttpTransport(settings.mcp_url, auth=settings.mcp_auth_token)
    else:
        from mcp_server.server import build_server

        objetivo = build_server()
    # `enviar_correo` se aplaza a una fase posterior: no se le expone al LLM
    # (exclusión dura en el puente, además de la instrucción de la skill).
    excluir = {"ping"}
    if not settings.correo_habilitado:
        excluir.add("enviar_correo")
    return AgentRunner(
        completar=_completar_litellm(),
        herramientas=HerramientasMCP(objetivo, excluir=excluir),
        gestor=gestor,
    )
