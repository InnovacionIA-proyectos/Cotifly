"""Front web de pruebas del Bot Cotizador (reemplazo temporal de Google Chat).

- `GET  /web`            → página de chat + login + "mis cotizaciones" (un HTML).
- `POST /web/login`      → **desactivado (HU-002.2)**, pendiente de ClientePro (HU-004).
- `POST /web/chat`       → corre un turno del agente (mismo Agent Runner + MCP);
  requiere un token que hoy no se puede emitir sin `/web/login`.
- `GET  /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar`,
  `POST /web/cotizaciones/{folio}/seguimiento` → **desactivados (HU-002.2)**: dependían
  de `comercial_email`, reemplazado por `usuario_id` (entero, sin fuente real hasta
  ClientePro).

`usuarios_web` dejó de tener PK por correo (HU-002.2): la identidad real llegará con
el id de SAP que entregue ClientePro. Mientras tanto, cotizar por Google Chat (fuera
de `/web/*`, con su propia identidad de Google) sigue funcionando igual.
"""

from __future__ import annotations

import logging
import re
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from fastapi import APIRouter, Header, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse
from pydantic import BaseModel, Field

from orquestador.web_auth import TokenInvalido, verificar_token

logger = logging.getLogger("bot_cotizador.web")

router = APIRouter(tags=["web"])

_HTML_PATH = Path(__file__).parent / "static" / "chat.html"
_SESION_RE = re.compile(r"^[A-Za-z0-9_-]{8,64}$")


class LoginWeb(BaseModel):
    email: str = Field(min_length=3, max_length=200)
    nombre: str | None = Field(default=None, max_length=200)


class MensajeWeb(BaseModel):
    mensaje: str = Field(min_length=1, max_length=8000)
    sesion_id: str


class SeguimientoWeb(BaseModel):
    ganada: bool | None = None
    motivo_rechazo: str | None = Field(default=None, max_length=2000)


class EnvioWeb(BaseModel):
    para: list[str] = Field(min_length=1)
    cc: list[str] = Field(default_factory=list)
    asunto: str | None = Field(default=None, max_length=300)
    cuerpo: str | None = Field(default=None, max_length=5000)


def _usuario(authorization: str | None) -> dict[str, str | None]:
    """Extrae y valida el usuario del `Authorization: Bearer <token>`."""
    if not authorization or not authorization.lower().startswith("bearer "):
        raise TokenInvalido("falta el token de sesión")
    return verificar_token(authorization.split(" ", 1)[1].strip())


def _runner_de(request: Request) -> Any:
    runner = getattr(request.app.state, "runner_web", None)
    if runner is None:
        from orquestador.agente import construir_runner

        runner = construir_runner()
        request.app.state.runner_web = runner
    return runner


def _datos_de(resultado: Any) -> dict[str, Any]:
    precio = resultado.ultima_salida("calcular_precio")
    margen = resultado.ultima_salida("obtener_margen")
    pdf = resultado.ultima_salida("generar_pdf")

    def _ok(d: dict[str, Any] | None) -> dict[str, Any] | None:
        return d if d and "error" not in d else None

    return {
        "texto": resultado.texto,
        "herramientas": resultado.herramientas_invocadas,
        "precio": _ok(precio),
        "margen": _ok(margen),
        "pdf": _ok(pdf),
    }


@router.get("/web", response_class=HTMLResponse)
def pagina_chat() -> HTMLResponse:
    """Sirve la Consola de Cotización (sin caché: el front evoluciona rápido)."""
    return HTMLResponse(
        _HTML_PATH.read_text(encoding="utf-8"),
        headers={"Cache-Control": "no-store, must-revalidate"},
    )


@router.get("/web/logo.png")
def logo() -> FileResponse:
    """Logo corporativo de Botero Soto para la cabecera de la Consola."""
    return FileResponse(_HTML_PATH.parent / "logo.png", media_type="image/png")


@router.post("/web/login")
def login(cuerpo: LoginWeb) -> JSONResponse:
    """Desactivado (HU-002.2): `usuarios_web` ya no se llena por correo, sino por el
    id de SAP que entrega ClientePro (HU-004). Hasta que esa integración exista, no
    hay ninguna forma real de identificar al comercial desde este endpoint.
    """
    return JSONResponse(
        status_code=501,
        content={"error": "Login pendiente de ClientePro (HU-004). Aún no disponible."},
    )


@router.post("/web/chat")
async def web_chat(
    cuerpo: MensajeWeb, request: Request, authorization: str | None = Header(default=None)
) -> JSONResponse:
    """Corre un turno del agente para la sesión web del comercial autenticado."""
    try:
        usuario = _usuario(authorization)
    except TokenInvalido:
        return JSONResponse(status_code=401, content={"error": "Inicia sesión de nuevo."})
    if not _SESION_RE.match(cuerpo.sesion_id):
        return JSONResponse(status_code=400, content={"error": "sesion_id inválido"})

    from shared.db import get_session

    clave = f"web/{cuerpo.sesion_id}"
    runner = _runner_de(request)
    try:
        with get_session() as session:
            resultado = await runner.ejecutar_turno(
                cuerpo.mensaje,
                session=session,
                space_id=clave,
                thread_id=clave,
                usuario_email=usuario["email"],
                usuario_nombre=usuario["nombre"],
                fecha_hoy=datetime.now(UTC).strftime("%Y-%m-%d"),
            )
    except Exception as e:  # noqa: BLE001 - el front recibe un texto legible
        logger.exception("Fallo procesando turno web (sesión %s)", clave)
        if "RateLimit" in type(e).__name__ or "429" in str(e):
            texto = (
                "⏳ El modelo alcanzó su límite de peticiones por minuto. Espera unos "
                "segundos y reenvía el mismo mensaje — la conversación no se pierde."
            )
        else:
            texto = "Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo."
        return JSONResponse(status_code=200, content={"texto": texto, "error": True})
    return JSONResponse(status_code=200, content=_datos_de(resultado))


def _cotizacion_dict(c: Any) -> dict[str, Any]:
    # Sin uso hasta HU-004: los endpoints que la consumían (`mis-cotizaciones`,
    # `seguimiento`) quedaron desactivados en HU-002.2 al perder `comercial_email`.
    from shared.config import settings

    def _f(v: Any) -> float | None:
        return float(v) if v is not None else None

    return {
        "folio": c.folio,
        "cliente": c.cliente_nombre,
        "linea_negocio": c.linea_negocio,
        "precio_final": _f(c.precio_final),
        "margen_aplicado": _f(c.margen_aplicado),
        "moneda": c.moneda,
        "estado_costeo": c.estado_costeo,
        "estado_envio": c.estado_envio,
        "ganada": c.ganada,
        "motivo_rechazo": c.motivo_rechazo,
        "pdf_url": f"{settings.pdf_base_url}/pdfs/{c.folio}.pdf" if c.pdf_path else None,
        "created_at": c.created_at.isoformat() if c.created_at else None,
    }


def _sin_clientepro() -> JSONResponse:
    return JSONResponse(
        status_code=501,
        content={"error": "Identidad de comercial pendiente de ClientePro (HU-004)."},
    )


@router.get("/web/mis-cotizaciones")
def mis_cotizaciones(authorization: str | None = Header(default=None)) -> JSONResponse:
    """Desactivado (HU-002.2): sin `/web/login`, no hay `usuario_id` real que filtrar."""
    return _sin_clientepro()


async def _invocar_mcp(nombre: str, argumentos: dict[str, Any]) -> dict[str, Any]:
    """Invoca una herramienta del MCP (por HTTP con bearer, o in-process en dev).

    Sin uso hasta HU-004: su único llamador (`enviar_cotizacion`) quedó desactivado
    en HU-002.2 al perder `comercial_email`.
    """
    from orquestador.agente import HerramientasMCP
    from shared.config import settings

    if settings.mcp_url:
        from fastmcp.client.transports import StreamableHttpTransport

        objetivo: Any = StreamableHttpTransport(settings.mcp_url, auth=settings.mcp_auth_token)
    else:
        from mcp_server.server import build_server

        objetivo = build_server()
    async with HerramientasMCP(objetivo, excluir=set()) as h:
        return await h.invocar(nombre, argumentos)


@router.post("/web/cotizaciones/{folio}/enviar")
async def enviar_cotizacion(
    folio: str, cuerpo: EnvioWeb, authorization: str | None = Header(default=None)
) -> JSONResponse:
    """Envía la cotización al cliente por correo (relay SMTP), con el PDF adjunto.

    Desactivado (HU-002.2): el chequeo de dueño usaba `comercial_email`, que ya no
    existe (ver `usuario_id`, pendiente de ClientePro/HU-004). Sin identidad real no
    se puede validar que la cotización sea del comercial que llama.
    """
    return _sin_clientepro()


@router.post("/web/cotizaciones/{folio}/seguimiento")
def marcar_seguimiento(
    folio: str, cuerpo: SeguimientoWeb, authorization: str | None = Header(default=None)
) -> JSONResponse:
    """Desactivado (HU-002.2): `marcar_seguimiento` filtraba por `comercial_email`, que
    ya no existe (ver `usuario_id`, pendiente de ClientePro/HU-004)."""
    return _sin_clientepro()
