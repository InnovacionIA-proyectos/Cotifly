"""Publicador de mensajes en Google Chat (§4.1 · F13).

Cliente de la Chat API (`spaces.messages.create`) con **service account** para
publicar la respuesta del agente en el hilo del comercial. El transporte real se
inyecta (`enviar`) para poder mockearlo en pruebas; en producción usa google-auth
para obtener un token y `httpx` para el POST. Requiere el extra `gchat`.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from typing import Any

CHAT_API_BASE = "https://chat.googleapis.com/v1"
CHAT_SCOPE = "https://www.googleapis.com/auth/chat.bot"

# enviar(space_id, body) -> respuesta de la API
EnviarMensaje = Callable[[str, dict[str, Any]], Awaitable[dict[str, Any]]]


class PublicadorChat:
    """Publica mensajes/cards en un hilo de un space de Google Chat."""

    def __init__(self, enviar: EnviarMensaje) -> None:
        self._enviar = enviar

    async def publicar(
        self, space_id: str | None, thread_id: str | None, mensaje: dict[str, Any]
    ) -> dict[str, Any]:
        """Publica `mensaje` (texto/card) en el hilo indicado; devuelve la respuesta."""
        if not space_id:
            raise ValueError("space_id requerido para publicar en Chat")
        body = dict(mensaje)
        if thread_id:
            body["thread"] = {"name": thread_id}
        return await self._enviar(space_id, body)


def construir_publicador() -> PublicadorChat:
    """Publicador de producción: token de service account + POST a la Chat API."""

    async def _enviar(space_id: str, body: dict[str, Any]) -> dict[str, Any]:
        import httpx
        from google.auth.transport.requests import Request
        from google.oauth2 import service_account

        from shared.config import settings

        creds = service_account.Credentials.from_service_account_file(
            settings.google_sa_json, scopes=[CHAT_SCOPE]
        )
        creds.refresh(Request())
        async with httpx.AsyncClient(timeout=30) as cliente:
            resp = await cliente.post(
                f"{CHAT_API_BASE}/{space_id}/messages",
                params={"messageReplyOption": "REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD"},
                headers={"Authorization": f"Bearer {creds.token}"},
                json=body,
            )
            resp.raise_for_status()
            return resp.json()

    return PublicadorChat(_enviar)
