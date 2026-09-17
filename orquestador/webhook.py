"""Webhook de Google Chat (§4.2 · F12).

`POST /chat/events`: valida el JWT de Google (401 si falta o es inválido), parsea
el evento y lo **encola** para el worker asíncrono, devolviendo un **ACK inmediato**
(< 30 s) sin esperar al procesamiento. La cola y el verificador viven en
`app.state` (los inyecta `crear_app`), de modo que las pruebas pueden sustituirlos.
"""

from __future__ import annotations

import logging
from typing import Any

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from orquestador.gchat import EventoChat, JwtInvalido, extraer_bearer, parsear_evento
from shared.config import settings

logger = logging.getLogger("bot_cotizador.webhook")

router = APIRouter(tags=["chat"])

_RECIBO = "Recibí tu solicitud, la estoy procesando…"


def _ack(payload: dict[str, Any], texto: str | None = None) -> dict[str, Any]:
    """ACK en el formato que espera cada modelo de app de Chat.

    - **Add-on de Workspace** (evento con clave `chat`): la respuesta síncrona debe
      ir envuelta en `hostAppDataAction.chatDataAction.createMessageAction`.
    - **App clásica**: basta `{"text": ...}`.
    Sin texto → cuerpo vacío (ACK silencioso).
    """
    if not texto:
        return {}
    if "chat" in payload:
        return {
            "hostAppDataAction": {
                "chatDataAction": {"createMessageAction": {"message": {"text": texto}}}
            }
        }
    return {"text": texto}


@router.post("/chat/events")
async def chat_events(request: Request) -> JSONResponse:
    """Recibe un evento de Chat, (opcionalmente) verifica el JWT y lo encola (ACK).

    La verificación del JWT es configurable (`GCHAT_VERIFICAR_JWT`): las apps del
    modelo HTTP nuevo de Chat no envían el Bearer clásico, y en pruebas con túnel
    conviene desactivarla. Cuando está activa, un JWT inválido responde 401.
    """
    cola = request.app.state.cola

    if settings.gchat_verificar_jwt:
        try:
            token = extraer_bearer(request.headers.get("authorization"))
            request.app.state.verificador.verificar(token)
        except JwtInvalido:
            logger.warning("Webhook: JWT inválido o ausente (verificación activa).")
            return JSONResponse(status_code=401, content={"error": "JWT inválido o ausente"})

    try:
        payload: dict[str, Any] = await request.json()
    except Exception:
        return JSONResponse(status_code=400, content={"error": "payload no es JSON"})

    # Log del evento crudo para diagnóstico (formato clásico o nuevo de Chat).
    logger.info("Webhook evento recibido: %s", payload)

    evento: EventoChat = parsear_evento(payload)
    if evento.es_mensaje:
        await cola.encolar(evento)
        return JSONResponse(status_code=200, content=_ack(payload, _RECIBO))
    # Eventos no-mensaje (ADDED_TO_SPACE, etc.): ACK sin cuerpo.
    return JSONResponse(status_code=200, content=_ack(payload))
