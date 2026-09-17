"""Tests del `EnviadorSmtpRelay` (relay interno 10.10.40.62). Puros: sin red."""

from __future__ import annotations

from email.message import EmailMessage

import pytest

from mcp_server.correo import EntradaEnviarCorreo, EnviadorSmtpRelay, EnvioSmtpError


def _entrada(**kw: object) -> EntradaEnviarCorreo:
    base = {
        "cotizacion_id": "abc",
        "para": ["cliente@empresa.com"],
        "asunto": "Cotización PCN-1 — Botero Soto",
        "cuerpo": "Adjuntamos su cotización.",
        "confirmado_por": "nvargas@boterosoto.com.co",
        "cc": ["jefe@boterosoto.com.co"],
    }
    base.update(kw)
    return EntradaEnviarCorreo(**base)  # type: ignore[arg-type]


class _Espia:
    def __init__(self, fallar: bool = False) -> None:
        self.msg: EmailMessage | None = None
        self._fallar = fallar

    def __call__(self, msg: EmailMessage) -> None:
        if self._fallar:
            raise OSError("relay inalcanzable")
        self.msg = msg


def test_relay_arma_mensaje_con_remitente_comercial_y_adjunto() -> None:
    espia = _Espia()
    env = EnviadorSmtpRelay("10.10.40.62", transporte=espia)
    mid = env.enviar(_entrada(), b"%PDF-1.4 datos", "PCN-1.pdf")

    assert mid and "@boterosoto.com.co" in mid
    m = espia.msg
    assert m is not None
    # Remitente = comercial que confirma (enmascaramiento).
    assert m["From"] == "nvargas@boterosoto.com.co"
    assert m["Reply-To"] == "nvargas@boterosoto.com.co"
    assert m["To"] == "cliente@empresa.com"
    assert m["Cc"] == "jefe@boterosoto.com.co"
    assert "PCN-1" in m["Subject"]
    # Multipart con cuerpo (texto+html) y un adjunto PDF.
    adjuntos = [p for p in m.walk() if p.get_content_disposition() == "attachment"]
    assert len(adjuntos) == 1
    assert adjuntos[0].get_filename() == "PCN-1.pdf"
    assert adjuntos[0].get_content_type() == "application/pdf"
    # Hay una parte HTML.
    assert any(p.get_content_type() == "text/html" for p in m.walk())


def test_relay_sin_confirmado_usa_fallback() -> None:
    espia = _Espia()
    env = EnviadorSmtpRelay("10.10.40.62", remitente_fallback="notificaciones@boterosoto.com.co",
                            transporte=espia)
    env.enviar(_entrada(confirmado_por=""), b"%PDF", "PCN-2.pdf")
    assert espia.msg["From"] == "notificaciones@boterosoto.com.co"


def test_relay_fallo_de_red_eleva_error() -> None:
    env = EnviadorSmtpRelay("10.10.40.62", transporte=_Espia(fallar=True))
    with pytest.raises(EnvioSmtpError):
        env.enviar(_entrada(), b"%PDF", "PCN-3.pdf")
