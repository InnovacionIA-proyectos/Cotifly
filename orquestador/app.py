"""Punto de entrada del API Orquestador (FastAPI).

- Fase 9: `/health` (liveness) y `/health/ready` (readiness: verifica la BD) para
  Dokploy/Traefik, y el servidor de PDFs (`/pdfs/{archivo}.pdf`).
- Fase 12: webhook de Google Chat (`POST /chat/events`) con validación de JWT y
  **ACK asíncrono**: el evento se encola y lo procesa un **worker** in-process
  (arrancado/detenido en el `lifespan`). La lógica de negocio vive en el MCP.

`crear_app` inyecta verificador/cola/procesador para poder sustituirlos en pruebas;
`app` es la instancia de producción que arranca uvicorn.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import FastAPI
from fastapi.responses import JSONResponse
from sqlalchemy import text

from orquestador.alertas import NotificadorErrores
from orquestador.gchat import EventoChat, VerificadorJwt, construir_verificador
from orquestador.pdfs import router as pdfs_router
from orquestador.procesador import ProcesadorCotizacion
from orquestador.publicador import construir_publicador
from orquestador.sesiones_api import router as sesiones_router
from orquestador.sheets_api import router as sheets_router
from orquestador.web import router as web_router
from orquestador.webhook import router as chat_router
from orquestador.worker import ColaEventos, Procesador
from shared.config import settings
from shared.db import engine

logger = logging.getLogger("bot_cotizador.orquestador")


async def _barrido_sesiones(intervalo_seg: int) -> None:
    """Archiva periódicamente las sesiones inactivas (§4.4). Nunca aborta por error."""
    from orquestador.sesiones import GestorSesiones
    from shared.db import get_session

    gestor = GestorSesiones()
    while True:
        await asyncio.sleep(intervalo_seg)
        try:
            with get_session() as s:
                archivadas = gestor.archivar_inactivas(s)
            if archivadas:
                logger.info("Barrido de sesiones: %s archivadas por inactividad.", archivadas)
        except Exception:  # noqa: BLE001 - el barrido no debe tumbar el servicio
            logger.exception("Fallo en el barrido de sesiones inactivas.")


def _volcar_sheets() -> dict[str, int]:
    """Ejecuta una sincronización síncrona completa (para correr en un threadpool)."""
    from shared.db import get_session
    from shared.sheets_sync import sincronizar

    with get_session() as s:
        return sincronizar(s)


async def _sync_sheets_periodico(intervalo_seg: int) -> None:
    """Vuelca la BD al libro de Sheets cada `intervalo_seg`. Nunca aborta por error."""
    while True:
        await asyncio.sleep(intervalo_seg)
        try:
            conteo = await asyncio.to_thread(_volcar_sheets)
            logger.info("Sync a Google Sheets: %s", conteo)
        except Exception:  # noqa: BLE001 - la sync no debe tumbar el servicio
            logger.exception("Fallo en la sincronización periódica a Google Sheets.")


def crear_app(
    verificador: VerificadorJwt | None = None,
    cola: ColaEventos | None = None,
    procesador: Procesador | None = None,
) -> FastAPI:
    """Construye la app con sus dependencias (inyectables para pruebas)."""

    @asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        await app.state.cola.iniciar()
        tareas: list[asyncio.Task[None]] = []
        if settings.sesion_barrido_seg > 0:
            tareas.append(asyncio.create_task(_barrido_sesiones(settings.sesion_barrido_seg)))
        if (
            settings.sheets_habilitado
            and settings.sheets_spreadsheet_id
            and settings.sheets_sync_intervalo_seg > 0
        ):
            tareas.append(
                asyncio.create_task(
                    _sync_sheets_periodico(settings.sheets_sync_intervalo_seg)
                )
            )
        try:
            yield
        finally:
            for t in tareas:
                t.cancel()
            for t in tareas:
                with contextlib.suppress(asyncio.CancelledError):
                    await t
            await app.state.cola.detener()

    app = FastAPI(title="Bot Cotizador — API Orquestador", version="0.1.0", lifespan=lifespan)
    app.include_router(pdfs_router)
    app.include_router(chat_router)
    app.include_router(sesiones_router)
    app.include_router(sheets_router)
    app.include_router(web_router)

    app.state.verificador = verificador or construir_verificador()
    if cola is not None:
        app.state.cola = cola
    else:
        publicador = construir_publicador()
        proc = procesador or ProcesadorCotizacion(publicador=publicador)
        notificador = NotificadorErrores(publicador, settings.gchat_support_space)

        async def _al_fallar(evento: EventoChat, error: BaseException) -> None:
            await notificador.notificar(f"evento {evento.tipo} · {evento.space_id}", error)

        app.state.cola = ColaEventos(proc, on_error=_al_fallar)

    @app.get("/health")
    def health() -> dict[str, str]:
        """Liveness: el proceso responde (sin dependencias externas)."""
        return {"status": "ok", "servicio": "orquestador", "entorno": settings.entorno}

    @app.get("/health/ready")
    def ready() -> JSONResponse:
        """Readiness: además la BD es alcanzable. 503 si no lo es."""
        try:
            with engine.connect() as conn:
                conn.execute(text("SELECT 1"))
        except Exception as e:  # noqa: BLE001 - readiness reporta, no propaga
            return JSONResponse(
                status_code=503, content={"status": "unavailable", "detalle": type(e).__name__}
            )
        return JSONResponse(status_code=200, content={"status": "ready"})

    return app


app = crear_app()
