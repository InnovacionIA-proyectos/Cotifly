"""Front web de pruebas del Bot Cotizador (reemplazo temporal de Google Chat).

- `GET  /web`            → página de chat + login + "mis cotizaciones" (un HTML).
- `POST /web/login`      → identifica al comercial por correo, emite token firmado.
- `POST /web/chat`       → corre un turno del agente (mismo Agent Runner + MCP).
- `GET  /web/mis-cotizaciones`            → cotizaciones del comercial autenticado.
- `POST /web/cotizaciones/{folio}/seguimiento` → marca efectiva/no efectiva + nota.

La identidad viene del **token** (login), no del cliente: así la firma, el
`comercial_email` de las cotizaciones y el seguimiento son del usuario real. La
sesión conversacional es un hilo `web/<id>` (misma trazabilidad que Google Chat).
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

from orquestador.web_auth import TokenInvalido, crear_token, es_correo_valido, verificar_token

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
    efectiva: bool | None = None
    nota: str | None = Field(default=None, max_length=2000)


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
    """Identifica al comercial por su correo corporativo y emite un token de sesión."""
    email = cuerpo.email.strip().lower()
    if not es_correo_valido(email):
        return JSONResponse(
            status_code=400,
            content={"error": "Usa tu correo corporativo @boterosoto.com.co"},
        )
    from shared.db import get_session
    from shared.repos import UsuarioWebRepo

    with get_session() as s:
        u = UsuarioWebRepo.upsert(s, email, cuerpo.nombre)
        nombre = u.nombre
    return JSONResponse(
        content={"token": crear_token(email, nombre), "email": email, "nombre": nombre}
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
    from shared.config import settings

    def _f(v: Any) -> float | None:
        return float(v) if v is not None else None

    return {
        "folio": c.folio,
        "cliente": c.cliente_nombre,
        "linea_negocio": c.linea_negocio,
        "origen": c.origen,
        "destino": c.destino,
        "precio_final": _f(c.precio_final),
        "margen_aplicado": _f(c.margen_aplicado),
        "moneda": c.moneda,
        "estado": c.estado,
        "efectiva": c.efectiva,
        "seguimiento_nota": c.seguimiento_nota,
        "pdf_url": f"{settings.pdf_base_url}/pdfs/{c.folio}.pdf" if c.pdf_path else None,
        "created_at": c.created_at.isoformat() if c.created_at else None,
    }


@router.get("/web/mis-cotizaciones")
def mis_cotizaciones(authorization: str | None = Header(default=None)) -> JSONResponse:
    """Lista las cotizaciones del comercial autenticado, para seguimiento."""
    try:
        usuario = _usuario(authorization)
    except TokenInvalido:
        return JSONResponse(status_code=401, content={"error": "Inicia sesión de nuevo."})

    from shared.db import get_session
    from shared.repos import CotizacionRepo

    with get_session() as s:
        filas = CotizacionRepo.por_comercial(s, usuario["email"] or "")
        cotizaciones = [_cotizacion_dict(c) for c in filas]
    return JSONResponse(content={"total": len(cotizaciones), "cotizaciones": cotizaciones})


async def _invocar_mcp(nombre: str, argumentos: dict[str, Any]) -> dict[str, Any]:
    """Invoca una herramienta del MCP (por HTTP con bearer, o in-process en dev)."""
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

    El remitente es el comercial autenticado (enmascaramiento). Solo puede enviar
    cotizaciones propias. Invoca `enviar_correo` del MCP (que valida, deduplica y
    traza el envío en `correos_enviados`).
    """
    try:
        usuario = _usuario(authorization)
    except TokenInvalido:
        return JSONResponse(status_code=401, content={"error": "Inicia sesión de nuevo."})

    from shared.db import get_session
    from shared.repos import CotizacionRepo

    with get_session() as s:
        cot = CotizacionRepo.obtener_por_folio(s, folio)
        if cot is None or cot.comercial_email != usuario["email"]:
            return JSONResponse(
                status_code=404, content={"error": "Cotización no encontrada o no es tuya."}
            )
        cot_id = str(cot.id)
        asunto = cuerpo.asunto or f"Cotización {folio} — Botero Soto"

    salida = await _invocar_mcp(
        "enviar_correo",
        {
            "cotizacion_id": cot_id,
            "para": cuerpo.para,
            "cc": cuerpo.cc,
            "asunto": asunto,
            "cuerpo": cuerpo.cuerpo,
            "confirmado_por": usuario["email"],  # remitente = comercial autenticado
        },
    )
    if "error" in salida:
        return JSONResponse(status_code=400, content=salida)
    # Marca la cotización como enviada.
    with get_session() as s:
        c = CotizacionRepo.obtener_por_folio(s, folio)
        if c is not None:
            c.estado = "enviada"
    return JSONResponse(content={"enviado": True, "message_id": salida.get("message_id")})


@router.post("/web/cotizaciones/{folio}/seguimiento")
def marcar_seguimiento(
    folio: str, cuerpo: SeguimientoWeb, authorization: str | None = Header(default=None)
) -> JSONResponse:
    """Marca la cotización como efectiva/no efectiva (+nota) — solo si es del usuario."""
    try:
        usuario = _usuario(authorization)
    except TokenInvalido:
        return JSONResponse(status_code=401, content={"error": "Inicia sesión de nuevo."})

    from shared.db import get_session
    from shared.repos import CotizacionRepo

    with get_session() as s:
        cot = CotizacionRepo.marcar_seguimiento(
            s, folio, usuario["email"] or "", cuerpo.efectiva, cuerpo.nota
        )
        if cot is None:
            return JSONResponse(
                status_code=404, content={"error": "Cotización no encontrada o no es tuya."}
            )
        salida = _cotizacion_dict(cot)
    return JSONResponse(content=salida)
