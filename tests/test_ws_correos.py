"""Tests de `EnviadorWsCorreos` (servicio SOAP corporativo). Puros: sin red.

Verifican el contrato del WSDL WSCorreos: remitente = comercial que confirma,
destinatarios unidos, **adjunto como ruta en disco** (el PDF se deja en la carpeta
del proyecto y se borra tras enviar), parseo del resultado y manejo de fallo del
servicio como error estructurado (no excepción cruda).
"""

from __future__ import annotations

from pathlib import Path

import pytest

from mcp_server.correo import (
    EntradaEnviarCorreo,
    EnviadorWsCorreos,
    EnvioSmtpError,
)


class _RespFalsa:
    def __init__(self, status_code: int, text: str) -> None:
        self.status_code = status_code
        self.text = text


class _PostEspia:
    """Captura la llamada SOAP y devuelve una respuesta predefinida."""

    def __init__(self, resp: _RespFalsa) -> None:
        self._resp = resp
        self.url: str | None = None
        self.content: str | None = None
        self.headers: dict[str, str] = {}

    def __call__(self, url: str, *, content: str, headers: dict[str, str]) -> _RespFalsa:
        self.url = url
        self.content = content
        self.headers = headers
        return self._resp


_OK = (
    '<?xml version="1.0"?><soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope">'
    "<soap:Body><CrearCorreoObjResponse xmlns=\"http://boterosoto.com.co/Correo\">"
    "<CrearCorreoObjResult>OK-12345</CrearCorreoObjResult>"
    "</CrearCorreoObjResponse></soap:Body></soap:Envelope>"
)


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


def _env(espia_o_post: object, tmp: Path) -> EnviadorWsCorreos:
    return EnviadorWsCorreos(
        "http://ws/correos.asmx",
        dir_local=str(tmp),
        dir_remoto="D:\\bot-cotizador",
        http_post=espia_o_post,  # type: ignore[arg-type]
    )


def test_envio_ok_remitente_comercial_y_adjunto_ruta_disco(tmp_path: Path) -> None:
    espia = _PostEspia(_RespFalsa(200, _OK))
    mid = _env(espia, tmp_path).enviar(_entrada(), b"%PDF", "PCN-1.pdf")

    assert mid == "OK-12345"
    cuerpo = espia.content or ""
    # El remitente es el comercial que confirmó (enmascaramiento del origen).
    assert "<REMITENTE>nvargas@boterosoto.com.co</REMITENTE>" in cuerpo
    # Destinatarios: para + cc unidos por ';'.
    assert "cliente@empresa.com;jefe@boterosoto.com.co" in cuerpo
    # Adjunto como RUTA en el disco del servidor (D:\proyecto\archivo), no bytes ni URL.
    assert "D:\\bot-cotizador\\PCN-1.pdf" in cuerpo
    # SOAP 1.2 con la acción correcta.
    assert "application/soap+xml" in espia.headers.get("Content-Type", "")
    assert "CrearCorreoObj" in espia.headers.get("Content-Type", "")


def test_pdf_temporal_se_borra_tras_enviar(tmp_path: Path) -> None:
    espia = _PostEspia(_RespFalsa(200, _OK))
    _env(espia, tmp_path).enviar(_entrada(), b"%PDF contenido", "PCN-9.pdf")
    # El adjunto es temporal: no debe quedar en disco tras el envío.
    assert not (tmp_path / "PCN-9.pdf").exists()


def test_pdf_temporal_se_borra_aunque_falle(tmp_path: Path) -> None:
    with pytest.raises(EnvioSmtpError):
        _env(_PostEspia(_RespFalsa(500, "boom")), tmp_path).enviar(_entrada(), b"%PDF", "PCN-8.pdf")
    assert not (tmp_path / "PCN-8.pdf").exists()  # limpieza incluso ante fallo


def test_resultado_vacio_sintetiza_message_id(tmp_path: Path) -> None:
    espia = _PostEspia(_RespFalsa(200, _OK.replace("OK-12345", "")))
    mid = _env(espia, tmp_path).enviar(_entrada(), b"%PDF", "PCN-1.pdf")
    assert mid  # se sintetiza un Message-ID trazable


def test_http_500_eleva_error_de_envio(tmp_path: Path) -> None:
    with pytest.raises(EnvioSmtpError):
        _env(_PostEspia(_RespFalsa(500, "boom")), tmp_path).enviar(_entrada(), b"%PDF", "PCN-1.pdf")


def test_excepcion_de_transporte_se_envuelve(tmp_path: Path) -> None:
    def _rompe(url: str, *, content: str, headers: dict[str, str]) -> _RespFalsa:
        raise ConnectionError("DNS")

    with pytest.raises(EnvioSmtpError):
        _env(_rompe, tmp_path).enviar(_entrada(), b"%PDF", "PCN-1.pdf")


def test_escapa_caracteres_xml_en_asunto(tmp_path: Path) -> None:
    espia = _PostEspia(_RespFalsa(200, _OK))
    _env(espia, tmp_path).enviar(_entrada(asunto="Tarifa < 3000 & envío"), b"%PDF", "PCN-1.pdf")
    cuerpo = espia.content or ""
    assert "&lt; 3000 &amp; env" in cuerpo  # sin romper el XML
