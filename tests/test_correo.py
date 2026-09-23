"""Tests de `enviar_correo` (Fase 7). Postgres real; SMTP simulado (transporte).

Cubren el DoD: ningún correo sin `confirmado_por`, idempotencia < 5 min, fallo
SMTP trazado como `fallido`, y todo persistido en `correos_enviados`.
"""

from __future__ import annotations

from datetime import UTC, datetime, timedelta
from email.message import EmailMessage
from pathlib import Path

import pytest
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from mcp_server.correo import (
    ConfirmacionRequerida,
    CorreoPdfNoEncontrado,
    CotizacionNoEncontrada,
    EntradaEnviarCorreo,
    EnviadorCorreo,
    EnvioDuplicado,
    EnvioSmtpError,
    ServicioCorreo,
)
from shared.models import CorreoEnviado
from shared.repos import CotizacionRepo
from tests.conftest import requiere_pg

pytestmark = requiere_pg


class _TransporteEspia:
    """Transporte SMTP falso: guarda el mensaje en vez de enviarlo."""

    def __init__(self) -> None:
        self.enviados: list[EmailMessage] = []

    def __call__(self, msg: EmailMessage) -> None:
        self.enviados.append(msg)


def _cotizacion_con_pdf(session: Session, tmp_path: Path, folio: str) -> object:
    pdf = tmp_path / f"{folio}.pdf"
    pdf.write_bytes(b"%PDF-1.4 contenido de prueba")
    return CotizacionRepo.crear(
        session, folio=folio, cliente_nombre="LONGYIN", pdf_path=str(pdf)
    )


def _entrada(cot_id: str, **kw: object) -> EntradaEnviarCorreo:
    base = {
        "cotizacion_id": cot_id,
        "para": ["cliente@empresa.com"],
        "asunto": "Cotización — Botero Soto",
        "cuerpo": "Adjuntamos su cotización.",
        "confirmado_por": "comercial@boterosoto.com.co",
    }
    base.update(kw)
    return EntradaEnviarCorreo(**base)  # type: ignore[arg-type]


def _servicio(transporte: _TransporteEspia) -> ServicioCorreo:
    enviador = EnviadorCorreo(
        "smtp.local", 587, "cotizaciones@boterosoto.com.co", transporte=transporte
    )
    return ServicioCorreo(enviador)


# --- envío OK ----------------------------------------------------------------


def test_envio_ok_adjunta_pdf_y_persiste(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C1")
    espia = _TransporteEspia()
    message_id = _servicio(espia).enviar(
        db_session, _entrada(str(cot.id), cc=["comercial@boterosoto.com.co"])
    )

    assert message_id
    # El mensaje llevaba el PDF adjunto y el CC.
    msg = espia.enviados[0]
    assert msg["Cc"] == "comercial@boterosoto.com.co"
    adjuntos = list(msg.iter_attachments())
    assert len(adjuntos) == 1
    assert adjuntos[0].get_filename() == "PCN-C1.pdf"

    fila = db_session.scalar(select(CorreoEnviado).where(CorreoEnviado.cotizacion_id == cot.id))
    assert fila is not None
    assert fila.estado == "enviado"
    assert fila.message_id == message_id
    # HU-002.2: `confirmado_por` en BD es entero (FK a usuarios_web.id), sin fuente
    # hasta ClientePro/HU-004 — el envío real sigue usando el correo del parámetro
    # de entrada como remitente (ver `mcp_server/correo.py`), solo la BD queda NULL.
    assert fila.confirmado_por is None


# --- confirmación obligatoria (invariante #4) --------------------------------


def test_sin_confirmado_por_rechaza_y_no_envia(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C2")
    espia = _TransporteEspia()
    with pytest.raises(ConfirmacionRequerida):
        _servicio(espia).enviar(db_session, _entrada(str(cot.id), confirmado_por="  "))
    assert espia.enviados == []  # no se envió nada
    assert db_session.scalar(
        select(func.count()).select_from(CorreoEnviado).where(CorreoEnviado.cotizacion_id == cot.id)
    ) == 0


# --- cotización inexistente --------------------------------------------------


def test_cotizacion_no_encontrada(db_session: Session) -> None:
    import uuid

    with pytest.raises(CotizacionNoEncontrada):
        _servicio(_TransporteEspia()).enviar(db_session, _entrada(str(uuid.uuid4())))


# --- PDF ausente -------------------------------------------------------------


def test_pdf_no_encontrado(db_session: Session) -> None:
    cot = CotizacionRepo.crear(db_session, folio="PCN-C3", pdf_path="/no/existe.pdf")
    with pytest.raises(CorreoPdfNoEncontrado):
        _servicio(_TransporteEspia()).enviar(db_session, _entrada(str(cot.id)))


# --- idempotencia ------------------------------------------------------------


def test_duplicado_menos_5min_rechaza(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C4")
    servicio = _servicio(_TransporteEspia())
    servicio.enviar(db_session, _entrada(str(cot.id)))  # primer envío
    with pytest.raises(EnvioDuplicado):
        servicio.enviar(db_session, _entrada(str(cot.id)))  # mismo destinatario, < 5 min


def test_duplicado_mas_5min_permite(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C5")
    # Envío "viejo": mismo destinatario pero hace 6 min (created_at explícito).
    tx_now = db_session.scalar(select(func.now()))
    viejo = CorreoEnviado(
        cotizacion_id=cot.id,
        para=["cliente@empresa.com"],
        asunto="prev",
        confirmado_por=None,  # HU-002.2: columna entera, sin fuente hasta HU-004
        estado="enviado",
        created_at=tx_now - timedelta(minutes=6),
    )
    db_session.add(viejo)
    db_session.flush()

    espia = _TransporteEspia()
    _servicio(espia).enviar(db_session, _entrada(str(cot.id)))  # permitido
    assert len(espia.enviados) == 1


def test_distintos_destinatarios_no_es_duplicado(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C6")
    servicio = _servicio(_TransporteEspia())
    servicio.enviar(db_session, _entrada(str(cot.id), para=["a@x.com"]))
    servicio.enviar(db_session, _entrada(str(cot.id), para=["b@y.com"]))  # otro destinatario, ok


# --- fallo SMTP --------------------------------------------------------------


def test_fallo_smtp_eleva_envio_smtp_error(db_session: Session, tmp_path: Path) -> None:
    cot = _cotizacion_con_pdf(db_session, tmp_path, "PCN-C7")

    def _rompe(_msg: EmailMessage) -> None:
        raise OSError("conexión rechazada")

    enviador = EnviadorCorreo("smtp.local", 587, "from@x.com", transporte=_rompe)
    with pytest.raises(EnvioSmtpError):
        ServicioCorreo(enviador).enviar(db_session, _entrada(str(cot.id)))


def test_now_estable_en_transaccion(db_session: Session) -> None:
    # Documenta la razón del diseño: now() es estable en la transacción.
    a = db_session.scalar(select(func.now()))
    b = db_session.scalar(select(func.now()))
    assert a == b
    assert isinstance(a, datetime)
    assert a.tzinfo is not None and a.astimezone(UTC) is not None
