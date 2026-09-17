"""Google Chat: verificación del JWT y parseo de eventos (§4.2 · F12).

Google Chat firma cada llamada al webhook con un **JWT bearer** emitido por
`chat@system.gserviceaccount.com`, cuyo `audience` es el **número del proyecto**
de la app de Chat. Aquí se valida (firma con los certs públicos de Google + `aud`
+ emisor) y se normaliza el payload del evento a un `EventoChat`.

La verificación se expone tras el protocolo `VerificadorJwt` para poder inyectar
un doble en pruebas (offline, sin depender de las claves rotатorias de Google).
El verificador real usa `google-auth` (extra `gchat`).
"""

from __future__ import annotations

from typing import Any, Protocol, runtime_checkable

from pydantic import BaseModel

# Emisor y certs públicos de la app de sistema de Google Chat.
CHAT_ISSUER = "chat@system.gserviceaccount.com"
CHAT_CERTS_URL = (
    "https://www.googleapis.com/service_accounts/v1/metadata/x509/"
    "chat@system.gserviceaccount.com"
)


class JwtInvalido(Exception):
    """El token bearer falta, está mal formado, o no supera la verificación."""


@runtime_checkable
class VerificadorJwt(Protocol):
    def verificar(self, token: str) -> dict[str, Any]:
        """Devuelve los claims si el token es válido; si no, lanza JwtInvalido."""
        ...


class GoogleChatJwtVerifier:
    """Verificador real: valida firma + `aud` + emisor contra los certs de Google."""

    def __init__(self, project_number: str) -> None:
        self._audience = project_number

    def verificar(self, token: str) -> dict[str, Any]:
        if not token:
            raise JwtInvalido("token vacío")
        if not self._audience:
            raise JwtInvalido("gchat_project_number no configurado (audience del JWT)")
        try:
            from google.auth.transport import requests as ga_requests
            from google.oauth2 import id_token

            claims = id_token.verify_token(
                token,
                ga_requests.Request(),
                audience=self._audience,
                certs_url=CHAT_CERTS_URL,
            )
        except JwtInvalido:
            raise
        except Exception as e:  # firma/exp/aud inválidos, red, etc.
            raise JwtInvalido(f"verificación fallida: {e}") from e
        # El emisor debe ser la app de sistema de Chat (defensa extra sobre `aud`).
        if claims.get("iss") != CHAT_ISSUER and claims.get("email") != CHAT_ISSUER:
            raise JwtInvalido("emisor inesperado")
        return dict(claims)


def construir_verificador() -> GoogleChatJwtVerifier:
    """Verificador de producción a partir de la config (número del proyecto)."""
    from shared.config import settings

    return GoogleChatJwtVerifier(settings.gchat_project_number)


def extraer_bearer(authorization: str | None) -> str:
    """Extrae el token de un header `Authorization: Bearer <jwt>`."""
    if not authorization:
        raise JwtInvalido("falta el header Authorization")
    partes = authorization.split(" ", 1)
    if len(partes) != 2 or partes[0].lower() != "bearer" or not partes[1].strip():
        raise JwtInvalido("Authorization no es 'Bearer <token>'")
    return partes[1].strip()


class EventoChat(BaseModel):
    """Evento de Google Chat normalizado (subconjunto que usa el Orquestador)."""

    tipo: str  # MESSAGE | ADDED_TO_SPACE | REMOVED_FROM_SPACE | ...
    space_id: str | None = None
    thread_id: str | None = None
    texto: str | None = None
    usuario_email: str | None = None
    usuario_nombre: str | None = None

    @property
    def es_mensaje(self) -> bool:
        """True si es un mensaje de texto procesable (dispara una cotización)."""
        return self.tipo == "MESSAGE" and bool(self.texto and self.texto.strip())


def parsear_evento(payload: dict[str, Any]) -> EventoChat:
    """Normaliza el payload de Chat a `EventoChat` (tolerante a campos ausentes).

    Soporta los **dos formatos** de Google Chat:
    - **Clásico**: `{type, message:{text,sender,thread,space}, space, user}`.
    - **Nuevo (HTTP/add-on)**: `{chat:{messagePayload:{message,space,user}}, ...}`.
    """
    # Modelo nuevo: los datos vienen anidados en chat.messagePayload.
    payload_msg = ((payload.get("chat") or {}).get("messagePayload")) or {}

    mensaje = payload.get("message") or payload_msg.get("message") or {}
    espacio = (
        payload.get("space") or payload_msg.get("space") or mensaje.get("space") or {}
    )
    hilo = mensaje.get("thread") or {}
    remitente = mensaje.get("sender") or {}
    usuario = payload.get("user") or payload_msg.get("user") or {}

    texto = mensaje.get("text")
    # Tipo: explícito, o inferido (si trae mensaje con texto, es un MESSAGE).
    tipo = payload.get("type") or ("MESSAGE" if (payload_msg or texto) else "")

    return EventoChat(
        tipo=tipo,
        space_id=espacio.get("name"),
        thread_id=hilo.get("name"),
        texto=texto,
        usuario_email=remitente.get("email") or usuario.get("email"),
        usuario_nombre=remitente.get("displayName") or usuario.get("displayName"),
    )
