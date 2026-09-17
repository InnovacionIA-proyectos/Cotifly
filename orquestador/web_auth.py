"""Login ligero del front web: identidad por correo corporativo + token firmado.

No hay contraseñas todavía: el comercial se identifica con su **correo Botero
Soto** y su nombre; se emite un **token HMAC** (firmado con `WEB_SESSION_SECRET`,
con expiración) que el front guarda y envía en cada petición. Es suficiente para
identificar al usuario, firmar cotizaciones y dar seguimiento por persona.

Cuando se resuelva el OAuth de Google (login real con la cuenta corporativa), este
módulo se reemplaza por la verificación del id_token de Google sin tocar el resto:
la app solo depende de `verificar_token -> (email, nombre)`.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import re
import time

from shared.config import settings

# Correos válidos para el piloto: dominio corporativo.
_EMAIL_RE = re.compile(r"^[A-Za-z0-9._%+-]+@boterosoto\.com\.co$")
_TTL_SEG = 12 * 3600  # la sesión del front dura 12 h


class TokenInvalido(Exception):
    """El token falta, está mal formado, caducó o su firma no valida."""


def es_correo_valido(email: str) -> bool:
    return bool(_EMAIL_RE.match((email or "").strip().lower()))


def _firmar(datos: bytes) -> str:
    secreto = (settings.web_session_secret or "dev").encode()
    mac = hmac.new(secreto, datos, hashlib.sha256).digest()
    return base64.urlsafe_b64encode(mac).decode().rstrip("=")


def crear_token(email: str, nombre: str | None, ahora: float | None = None) -> str:
    """Emite un token `<payload_b64>.<firma>` con email, nombre y emisión."""
    payload = {
        "email": email.strip().lower(),
        "nombre": (nombre or "").strip() or None,
        "iat": int(ahora if ahora is not None else time.time()),
    }
    crudo = base64.urlsafe_b64encode(
        json.dumps(payload, separators=(",", ":")).encode()
    ).decode().rstrip("=")
    return f"{crudo}.{_firmar(crudo.encode())}"


def verificar_token(token: str, ahora: float | None = None) -> dict[str, str | None]:
    """Devuelve {email, nombre} si el token es válido; si no, lanza TokenInvalido."""
    if not token or "." not in token:
        raise TokenInvalido("token ausente o mal formado")
    crudo, firma = token.rsplit(".", 1)
    if not hmac.compare_digest(firma, _firmar(crudo.encode())):
        raise TokenInvalido("firma inválida")
    try:
        relleno = "=" * (-len(crudo) % 4)
        payload = json.loads(base64.urlsafe_b64decode(crudo + relleno))
    except Exception as e:  # noqa: BLE001
        raise TokenInvalido("payload ilegible") from e
    iat = int(payload.get("iat", 0))
    if (ahora if ahora is not None else time.time()) - iat > _TTL_SEG:
        raise TokenInvalido("token expirado")
    return {"email": payload.get("email"), "nombre": payload.get("nombre")}
