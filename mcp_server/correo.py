"""Envío de la cotización por correo (§5.1 `enviar_correo` · F7).

Invariante del flujo: **ningún correo sale sin `confirmado_por`** (el comercial lo
autoriza explícitamente en el chat). Además:
- idempotencia: rechaza un duplicado exacto (misma cotización + destinatarios) en < 5 min;
- adjunta el PDF de la cotización y usa una plantilla de cuerpo;
- traza todo en `correos_enviados` (enviado/fallido, message_id, confirmado_por);
- ante fallo SMTP no propaga excepción cruda: lo reporta como error estructurado.

El transporte SMTP es inyectable para poder probar sin servidor real.
"""

from __future__ import annotations

import contextlib
import smtplib
from collections.abc import Callable
from email.header import Header
from email.message import EmailMessage
from email.mime.application import MIMEApplication
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from email.utils import formatdate, make_msgid
from pathlib import Path
from typing import Protocol, runtime_checkable
from uuid import UUID
from xml.sax.saxutils import escape

from pydantic import BaseModel, Field
from sqlalchemy.orm import Session

from shared.config import settings
from shared.models import Cotizacion
from shared.repos import CorreoRepo

Transporte = Callable[[EmailMessage], None]


@runtime_checkable
class EnviadorDeCorreo(Protocol):
    """Contrato del transporte de correo que consume `ServicioCorreo`.

    Cualquier objeto con este método sirve como enviador (SMTP o servicio web),
    de modo que conmutar el transporte no toca la lógica de confirmación/dedup.
    """

    def enviar(self, entrada: EntradaEnviarCorreo, pdf_bytes: bytes, pdf_nombre: str) -> str:
        """Envía el correo con el PDF adjunto y devuelve un identificador del envío."""
        ...


# --- Contrato ----------------------------------------------------------------


class EntradaEnviarCorreo(BaseModel):
    cotizacion_id: str
    para: list[str] = Field(min_length=1)
    asunto: str
    cuerpo: str
    confirmado_por: str = ""
    cc: list[str] = Field(default_factory=list)


class ResultadoEnvio(BaseModel):
    enviado: bool
    message_id: str
    registrado_en_bd: bool


# --- Excepciones -------------------------------------------------------------


class ConfirmacionRequerida(Exception):
    """No hay `confirmado_por`: el envío exige autorización explícita del comercial."""


class CotizacionNoEncontrada(Exception):
    """No existe la cotización referida por `cotizacion_id`."""


class EnvioDuplicado(Exception):
    """Ya se envió a los mismos destinatarios hace menos de 5 minutos."""


class CorreoPdfNoEncontrado(Exception):
    """La cotización no tiene un PDF en disco para adjuntar."""


class EnvioSmtpError(Exception):
    """Fallo del transporte de correo (red, autenticación, rechazo del servidor/servicio).

    El nombre se conserva por compatibilidad; aplica también al servicio SOAP
    (ver alias `EnvioCorreoError`).
    """


# Alias legible para el transporte por servicio web (misma semántica de fallo).
EnvioCorreoError = EnvioSmtpError


# --- Cliente SMTP ------------------------------------------------------------


class EnviadorCorreo:
    """Construye el mensaje con el PDF adjunto y lo envía por SMTP."""

    def __init__(
        self,
        host: str,
        port: int,
        remitente: str,
        user: str = "",
        password: str = "",
        usar_tls: bool = True,
        transporte: Transporte | None = None,
    ) -> None:
        self._host = host
        self._port = port
        self._remitente = remitente
        self._user = user
        self._password = password
        self._usar_tls = usar_tls
        self._transporte = transporte or self._enviar_smtp

    def enviar(self, entrada: EntradaEnviarCorreo, pdf_bytes: bytes, pdf_nombre: str) -> str:
        msg = EmailMessage()
        msg["From"] = self._remitente
        msg["To"] = ", ".join(entrada.para)
        if entrada.cc:
            msg["Cc"] = ", ".join(entrada.cc)
        msg["Subject"] = entrada.asunto
        message_id = make_msgid()
        msg["Message-ID"] = message_id
        msg.set_content(entrada.cuerpo)
        msg.add_attachment(
            pdf_bytes, maintype="application", subtype="pdf", filename=pdf_nombre
        )
        try:
            self._transporte(msg)
        except (OSError, smtplib.SMTPException) as e:
            raise EnvioSmtpError(str(e)) from e
        return message_id

    def _enviar_smtp(self, msg: EmailMessage) -> None:  # pragma: no cover - red SMTP real
        with smtplib.SMTP(self._host, self._port, timeout=15) as s:
            if self._usar_tls:
                s.starttls()
            if self._user:
                s.login(self._user, self._password)
            s.send_message(msg)


class EnviadorSmtpRelay:
    """Envía por el **relay SMTP interno** de Botero Soto (`10.10.40.62:25`).

    El relay es Microsoft ESMTP sin `AUTH` ni `STARTTLS` (control por red/IP): se
    conecta al puerto 25 en texto plano y hace `send_message` sin login. Arma un
    `multipart/mixed` con cuerpo HTML + texto plano y el PDF adjunto en base64.

    El **remitente** (envelope `MAIL FROM` y header `From`) es el comercial que
    autorizó el envío (`confirmado_por`): el cliente recibe el correo enmascarado
    con la dirección del ejecutivo. `Reply-To` también apunta al comercial. El
    transporte SMTP es inyectable para pruebas (sin red).
    """

    def __init__(
        self,
        host: str,
        port: int = 25,
        remitente_fallback: str = "notificaciones@boterosoto.com.co",
        transporte: Transporte | None = None,
    ) -> None:
        self._host = host
        self._port = port
        self._fallback = remitente_fallback
        self._transporte = transporte or self._enviar_smtp

    def enviar(self, entrada: EntradaEnviarCorreo, pdf_bytes: bytes, pdf_nombre: str) -> str:
        remitente = (entrada.confirmado_por or "").strip() or self._fallback
        msg = MIMEMultipart("mixed")
        msg["From"] = remitente
        msg["To"] = ", ".join(entrada.para)
        if entrada.cc:
            msg["Cc"] = ", ".join(entrada.cc)
        msg["Reply-To"] = remitente
        msg["Subject"] = str(Header(entrada.asunto, "utf-8"))
        msg["Date"] = formatdate(localtime=True)
        message_id = make_msgid(domain="boterosoto.com.co")
        msg["Message-ID"] = message_id

        # Cuerpo: texto plano + HTML (multipart/alternative) para buena entrega.
        alt = MIMEMultipart("alternative")
        alt.attach(MIMEText(entrada.cuerpo, "plain", "utf-8"))
        alt.attach(MIMEText(_cuerpo_html(entrada.cuerpo), "html", "utf-8"))
        msg.attach(alt)

        # Adjunto PDF (base64).
        adjunto = MIMEApplication(pdf_bytes, _subtype="pdf")
        adjunto.add_header("Content-Disposition", "attachment", filename=pdf_nombre)
        msg.attach(adjunto)

        # Envelope: MAIL FROM = remitente; RCPT TO = para + cc.
        destinatarios = [*entrada.para, *entrada.cc]
        try:
            self._transporte_envelope(msg, remitente, destinatarios)
        except smtplib.SMTPRecipientsRefused as e:
            # 550 5.7.1 "Unable to relay": el relay NO acepta relayar a estos
            # destinatarios desde la IP de origen (o el dominio no está permitido).
            # No es un fallo del bot: la IP del servidor debe autorizarse en el relay.
            def _txt(v: object) -> str:
                return v.decode(errors="replace") if isinstance(v, bytes) else str(v)

            detalle = "; ".join(f"{r} → {_txt(m[1])}" for r, m in e.recipients.items())
            raise EnvioSmtpError(
                f"El relay {self._host} rechazó los destinatarios: no está autorizado a "
                f"relayar desde la IP de este servidor (o el dominio destino no está "
                f"permitido). Pídele a TI habilitar la IP del servidor en el relay "
                f"{self._host}. Detalle: {detalle}"
            ) from e
        except (OSError, smtplib.SMTPException) as e:
            raise EnvioSmtpError(f"Relay SMTP {self._host} falló: {e}") from e
        return message_id

    def _transporte_envelope(
        self, msg: MIMEMultipart, remitente: str, destinatarios: list[str]
    ) -> None:
        # Si hay transporte inyectado (pruebas), se le pasa el mensaje; si no, SMTP real.
        if self._transporte is not self._enviar_smtp:
            self._transporte(msg)  # type: ignore[arg-type]
            return
        self._enviar_smtp_envelope(msg, remitente, destinatarios)  # pragma: no cover

    def _enviar_smtp_envelope(  # pragma: no cover - red SMTP real
        self, msg: MIMEMultipart, remitente: str, destinatarios: list[str]
    ) -> None:
        with smtplib.SMTP(self._host, self._port, timeout=20) as s:
            s.send_message(msg, from_addr=remitente, to_addrs=destinatarios)

    def _enviar_smtp(self, msg: EmailMessage) -> None:  # pragma: no cover - firma compat
        with smtplib.SMTP(self._host, self._port, timeout=20) as s:
            s.send_message(msg)


def _cuerpo_html(texto: str) -> str:
    """Envuelve el cuerpo de texto en un HTML corporativo simple (azul Botero Soto)."""
    from xml.sax.saxutils import escape as _esc

    cuerpo = _esc(texto).replace("\n", "<br>")
    return (
        '<div style="font-family:Arial,Helvetica,sans-serif;color:#1A2A3A;font-size:14px;'
        'line-height:1.5">'
        f"{cuerpo}"
        '<hr style="border:none;border-top:1px solid #D9DEE5;margin:16px 0">'
        '<div style="font-size:12px;color:#5F6B7A">'
        '<b style="color:#005AA8">Botero Soto</b> · Soluciones Logísticas</div>'
        "</div>"
    )


# --- Cliente servicio SOAP WSCorreos ----------------------------------------

# Namespace y acción del servicio corporativo WSCorreos (ver WSDL).
_WS_NS = "http://boterosoto.com.co/Correo"
_WS_ACTION = f"{_WS_NS}/CrearCorreoObj"

_HttpPost = Callable[..., "HttpResp"]


class HttpResp(Protocol):
    """Subconjunto de la respuesta HTTP que usa `EnviadorWsCorreos` (duck-typing).

    Se declaran como propiedades de solo lectura para que `httpx.Response` (cuyo
    `text` es de solo lectura) satisfaga el protocolo.
    """

    @property
    def status_code(self) -> int: ...

    @property
    def text(self) -> str: ...


class EnviadorWsCorreos:
    """Envía el correo por el servicio SOAP corporativo WSCorreos (`CrearCorreoObj`).

    El **remitente** es el comercial que autorizó el envío (`confirmado_por`): así el
    cliente recibe el correo enmascarado con la dirección del ejecutivo que atiende.

    El adjunto se entrega como **ruta en disco**: el MCP escribe el PDF en la carpeta
    del proyecto del servidor de correo (`dir_local`, que es el montaje de la `D:` de
    tbsservicios) y pasa al SOAP esa misma ruta como la ve el servidor (`dir_remoto`,
    p. ej. `D:\\bot-cotizador\\PCN-...pdf`). El archivo es **temporal**: se borra en
    cuanto el envío retorna (éxito o fallo), para no sobrecargar el servidor. El
    transporte HTTP (`http_post`) es inyectable para pruebas.
    """

    def __init__(
        self,
        url: str,
        dir_local: str,
        dir_remoto: str,
        timeout_seg: int = 20,
        http_post: _HttpPost | None = None,
    ) -> None:
        self._url = url
        self._dir_local = Path(dir_local)
        self._dir_remoto = dir_remoto.rstrip("/\\")
        self._timeout = timeout_seg
        self._http_post = http_post

    def _ruta_remota(self, pdf_nombre: str) -> str:
        # Ruta Windows como la ve el servidor SOAP (backslash).
        return f"{self._dir_remoto}\\{pdf_nombre}"

    def _sobre(self, entrada: EntradaEnviarCorreo, adjunto: str) -> str:
        destinatarios = ";".join([*entrada.para, *entrada.cc])
        return (
            '<?xml version="1.0" encoding="utf-8"?>'
            '<soap12:Envelope xmlns:soap12="http://www.w3.org/2003/05/soap-envelope">'
            "<soap12:Body>"
            f'<CrearCorreoObj xmlns="{_WS_NS}">'
            "<objCorreo>"
            f"<REMITENTE>{escape(entrada.confirmado_por)}</REMITENTE>"
            f"<DESTINATARIOS>{escape(destinatarios)}</DESTINATARIOS>"
            f"<ASUNTO>{escape(entrada.asunto)}</ASUNTO>"
            f"<CUERPO>{escape(entrada.cuerpo)}</CUERPO>"
            f"<ARCHIVOSADJUNTOS><string>{escape(adjunto)}</string></ARCHIVOSADJUNTOS>"
            "</objCorreo>"
            "</CrearCorreoObj>"
            "</soap12:Body>"
            "</soap12:Envelope>"
        )

    def enviar(self, entrada: EntradaEnviarCorreo, pdf_bytes: bytes, pdf_nombre: str) -> str:
        # 1) Deja el PDF en la carpeta del proyecto que ve el servidor de correo.
        self._dir_local.mkdir(parents=True, exist_ok=True)
        ruta_local = self._dir_local / pdf_nombre
        ruta_local.write_bytes(pdf_bytes)
        try:
            # 2) Envía indicando la ruta del adjunto tal como la ve el servidor SOAP.
            cuerpo = self._sobre(entrada, self._ruta_remota(pdf_nombre))
            headers = {
                "Content-Type": f'application/soap+xml; charset=utf-8; action="{_WS_ACTION}"'
            }
            try:
                resp = self._post(cuerpo, headers)
            except Exception as e:  # red, DNS, timeout…
                raise EnvioSmtpError(f"WSCorreos inaccesible: {e}") from e
            if resp.status_code >= 400:
                raise EnvioSmtpError(f"WSCorreos respondió {resp.status_code}: {resp.text[:300]}")
            # El servicio no devuelve un Message-ID formal: se sintetiza uno trazable.
            return _extraer_resultado_ws(resp.text) or make_msgid()
        finally:
            # 3) El adjunto es temporal: se elimina pase lo que pase (best-effort).
            with contextlib.suppress(OSError):
                ruta_local.unlink(missing_ok=True)

    def _post(self, cuerpo: str, headers: dict[str, str]) -> HttpResp:
        if self._http_post is not None:
            return self._http_post(self._url, content=cuerpo, headers=headers)
        import httpx  # pragma: no cover - transporte HTTP real

        return httpx.post(  # pragma: no cover
            self._url, content=cuerpo, headers=headers, timeout=self._timeout
        )


def _extraer_resultado_ws(xml: str) -> str | None:
    """Extrae `CrearCorreoObjResult` de la respuesta SOAP (tolerante a namespaces)."""
    import re

    m = re.search(r"<[^>]*CrearCorreoObjResult[^>]*>(.*?)</[^>]*CrearCorreoObjResult>", xml, re.S)
    if not m:
        return None
    valor = m.group(1).strip()
    return valor or None


# --- Servicio ----------------------------------------------------------------

_PLANTILLA = (
    "Estimado cliente,\n\n"
    "Adjuntamos la cotización solicitada. Quedamos atentos a cualquier inquietud.\n\n"
    "Cordialmente,\nEquipo Comercial — Botero Soto"
)


class ServicioCorreo:
    def __init__(self, enviador: EnviadorDeCorreo) -> None:
        self._enviador = enviador

    def enviar(self, session: Session, entrada: EntradaEnviarCorreo) -> str:
        """Valida, verifica duplicado, adjunta el PDF y envía. Devuelve el message_id.

        Persiste la fila `enviado`. No captura el fallo SMTP: lo propaga para que la
        herramienta lo trace como `fallido` en una transacción propia.
        """
        if not entrada.confirmado_por.strip():
            raise ConfirmacionRequerida("Falta 'confirmado_por': el envío no está autorizado.")

        cot = session.get(Cotizacion, UUID(entrada.cotizacion_id))
        if cot is None:
            raise CotizacionNoEncontrada(f"No existe la cotización {entrada.cotizacion_id}.")

        if CorreoRepo.duplicado_reciente(session, cot.id, entrada.para):
            raise EnvioDuplicado("Envío duplicado a los mismos destinatarios en < 5 min.")

        # Lee el PDF del almacén configurado (volumen o S3/MinIO), no del disco local.
        from shared.almacen_pdf import construir_almacen

        ruta_pdf = cot.pdf_path or ""
        pdf_bytes = construir_almacen().leer_por_ruta(ruta_pdf) if ruta_pdf else None
        if not pdf_bytes:
            raise CorreoPdfNoEncontrado(f"La cotización {cot.folio} no tiene PDF disponible.")

        message_id = self._enviador.enviar(entrada, pdf_bytes, Path(ruta_pdf).name)

        CorreoRepo.registrar(
            session,
            cotizacion_id=cot.id,
            para=entrada.para,
            cc=entrada.cc or None,
            asunto=entrada.asunto,
            message_id=message_id,
            # `confirmado_por` en BD ahora es el id de usuarios_web (HU-002.2); sin
            # ClientePro (HU-004) no hay id que guardar. El envío real igual usa
            # `entrada.confirmado_por` (el correo) como remitente, sin cambios.
            confirmado_por=None,
            estado="enviado",
        )
        return message_id


def cuerpo_por_defecto() -> str:
    return _PLANTILLA


def construir_servicio_correo() -> ServicioCorreo:
    """Fábrica de producción: transporte según `CORREO_MODO` (wscorreos | smtp).

    Por defecto usa el servicio SOAP corporativo WSCorreos; `CORREO_MODO=smtp`
    vuelve al envío SMTP directo. En ambos casos la lógica de confirmación, dedup y
    trazabilidad de `ServicioCorreo` es la misma.
    """
    enviador: EnviadorDeCorreo
    if settings.correo_modo == "smtp_relay":
        enviador = EnviadorSmtpRelay(
            host=settings.smtp_relay_host,
            port=settings.smtp_relay_port,
            remitente_fallback=settings.smtp_relay_from_fallback,
        )
    elif settings.correo_modo == "smtp":
        enviador = EnviadorCorreo(
            host=settings.smtp_host,
            port=settings.smtp_port,
            remitente=settings.smtp_from,
            user=settings.smtp_user,
            password=settings.smtp_pass,
        )
    else:
        enviador = EnviadorWsCorreos(
            url=settings.wscorreos_url,
            dir_local=settings.wscorreos_adjunto_dir_local,
            dir_remoto=settings.wscorreos_adjunto_dir_remoto,
            timeout_seg=settings.wscorreos_timeout_seg,
        )
    return ServicioCorreo(enviador)
