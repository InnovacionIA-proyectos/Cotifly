"""Cola de eventos y worker asíncrono del Orquestador (§4.2 · F12).

El webhook de Chat debe **acusar recibo en < 30 s**; por eso no procesa el turno
del agente dentro de la petición: encola el evento y devuelve el ACK. Un **worker
asíncrono in-process** (asyncio) consume la cola fuera del ciclo del webhook.

El worker es **resiliente**: un evento que falle se registra y no tumba al worker
ni bloquea los siguientes.
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from contextlib import suppress

from orquestador.gchat import EventoChat

_log = logging.getLogger("orquestador.worker")

Procesador = Callable[[EventoChat], Awaitable[None]]
AlErrar = Callable[[EventoChat, BaseException], Awaitable[None]]


class ColaEventos:
    """Cola in-process con un worker que procesa cada evento en segundo plano."""

    def __init__(
        self, procesador: Procesador, maxsize: int = 0, on_error: AlErrar | None = None
    ) -> None:
        self._procesador = procesador
        self._on_error = on_error
        self._cola: asyncio.Queue[EventoChat] = asyncio.Queue(maxsize=maxsize)
        self._tarea: asyncio.Task[None] | None = None

    async def iniciar(self) -> None:
        """Arranca el worker (idempotente)."""
        if self._tarea is None or self._tarea.done():
            self._tarea = asyncio.create_task(self._run(), name="chat-worker")

    async def detener(self) -> None:
        """Cancela el worker y espera su fin limpio."""
        if self._tarea is not None:
            self._tarea.cancel()
            with suppress(asyncio.CancelledError):
                await self._tarea
            self._tarea = None

    async def encolar(self, evento: EventoChat) -> None:
        await self._cola.put(evento)

    async def unir(self) -> None:
        """Espera a que todos los eventos encolados terminen de procesarse (tests)."""
        await self._cola.join()

    async def _run(self) -> None:
        while True:
            evento = await self._cola.get()
            try:
                await self._procesador(evento)
            except Exception as e:  # noqa: BLE001 - el worker no muere por un evento
                _log.exception("Fallo procesando evento de Chat (%s)", evento.tipo)
                if self._on_error is not None:
                    try:
                        await self._on_error(evento, e)
                    except Exception:  # noqa: BLE001 - la alerta es best-effort
                        _log.exception("Fallo notificando el error")
            finally:
                self._cola.task_done()
