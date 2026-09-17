"""Procesador de eventos de Chat: sesión + Agent Runner + publicación (§4 · F12–F13).

Es lo que corre el worker por cada mensaje: resuelve la sesión del hilo (F10),
dispara el turno del Agent Runner (F11) y **publica la respuesta en Chat** (F13):
una card de previsualización (costo·margen·precio → confirmar/ajustar), una card
con el link al PDF, o texto simple. El correo nunca se dispara desde aquí: exige
confirmación explícita del comercial en un turno posterior.
"""

from __future__ import annotations

from orquestador.cards import mensaje_para_resultado
from orquestador.gchat import EventoChat


class ProcesadorCotizacion:
    """Procesa un evento de Chat corriendo el Agent Runner y publica el resultado.

    `runner` y `publicador` se inyectan en pruebas (Anthropic y Chat mockeados); en
    producción el runner se construye de forma perezosa con la config. Sin
    `publicador` (p. ej. en pruebas del worker) el turno corre pero no se publica.
    """

    def __init__(self, runner: object | None = None, publicador: object | None = None) -> None:
        self._runner = runner
        self._publicador = publicador

    def _obtener_runner(self) -> object:
        if self._runner is None:
            from orquestador.agente import construir_runner

            self._runner = construir_runner()
        return self._runner

    async def __call__(self, evento: EventoChat) -> None:
        if not evento.es_mensaje:
            return  # eventos no-mensaje (ADDED_TO_SPACE, etc.) no cotizan
        from datetime import UTC, datetime

        from shared.db import get_session

        runner = self._obtener_runner()
        # Sesión transaccional propia del turno (fuera del ciclo del webhook).
        with get_session() as session:
            resultado = await runner.ejecutar_turno(  # type: ignore[attr-defined]
                evento.texto,
                session=session,
                space_id=evento.space_id,
                thread_id=evento.thread_id,
                usuario_email=evento.usuario_email,
                usuario_nombre=evento.usuario_nombre,
                fecha_hoy=datetime.now(UTC).strftime("%Y-%m-%d"),
            )

        if self._publicador is not None:
            mensaje = mensaje_para_resultado(resultado)
            await self._publicador.publicar(  # type: ignore[attr-defined]
                evento.space_id, evento.thread_id, mensaje
            )
