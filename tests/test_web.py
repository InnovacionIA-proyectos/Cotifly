"""Tests del front web: login, chat autenticado, mis-cotizaciones y seguimiento."""

from __future__ import annotations

from typing import Any

import pytest
from fastapi.testclient import TestClient

from orquestador.app import crear_app
from orquestador.web_auth import crear_token
from tests.conftest import requiere_pg
from tests.test_chat_webhook import _ColaFake, _VerificadorFake

_SESION = "abcd1234efgh5678"
_EMAIL = "dguerra@boterosoto.com.co"


class _RunnerFake:
    def __init__(self) -> None:
        self.llamadas: list[dict[str, Any]] = []

    async def ejecutar_turno(self, mensaje: str, **kw: Any) -> Any:
        from orquestador.agente import ResultadoTurno

        self.llamadas.append({"mensaje": mensaje, **kw})
        return ResultadoTurno(
            texto="Previsualización lista.",
            iteraciones=2,
            herramientas_invocadas=["buscar_tarifa", "calcular_precio"],
            detener_por="stop",
            salidas_herramientas=[
                {"nombre": "calcular_precio",
                 "salida": {"costo": 1800000, "margen": 0.15, "precio": 2117647,
                            "utilidad": 317647}},
            ],
        )


def _app(runner: _RunnerFake | None = None) -> TestClient:
    app = crear_app(verificador=_VerificadorFake(), cola=_ColaFake())  # type: ignore[arg-type]
    if runner is not None:
        app.state.runner_web = runner
    return TestClient(app)


def _auth() -> dict[str, str]:
    return {"Authorization": f"Bearer {crear_token(_EMAIL, 'Daniel Guerra')}"}


# --- página + login ----------------------------------------------------------


def test_pagina_chat_se_sirve() -> None:
    r = _app().get("/web")
    assert r.status_code == 200
    assert "Bot Cotizador" in r.text and "/web/chat" in r.text
    assert "no-store" in r.headers.get("cache-control", "")


def test_login_rechaza_correo_no_corporativo() -> None:
    r = _app().post("/web/login", json={"email": "alguien@gmail.com", "nombre": "X"})
    assert r.status_code == 400


@requiere_pg
def test_login_corporativo_emite_token(db_engine: object) -> None:
    try:
        r = _app().post("/web/login", json={"email": _EMAIL, "nombre": "Daniel"})
        assert r.status_code == 200
        d = r.json()
        assert d["email"] == _EMAIL and d["token"]
    finally:
        _limpiar()


# --- chat requiere token -----------------------------------------------------


def test_chat_sin_token_401() -> None:
    r = _app(_RunnerFake()).post("/web/chat", json={"mensaje": "hola", "sesion_id": _SESION})
    assert r.status_code == 401


@requiere_pg
def test_chat_con_token_corre_turno_con_identidad(db_engine: object) -> None:
    runner = _RunnerFake()
    try:
        r = _app(runner).post(
            "/web/chat",
            json={"mensaje": "Cotiza BUN a CALI", "sesion_id": _SESION},
            headers=_auth(),
        )
        assert r.status_code == 200
        d = r.json()
        assert d["precio"]["precio"] == 2117647
        ll = runner.llamadas[0]
        assert ll["space_id"] == f"web/{_SESION}"
        assert ll["usuario_email"] == _EMAIL  # identidad viene del token, no del cliente
    finally:
        _limpiar()


# --- mis cotizaciones + seguimiento ------------------------------------------


@requiere_pg
def test_mis_cotizaciones_y_seguimiento(db_engine: object) -> None:
    from shared.db import get_session
    from shared.repos import CotizacionRepo

    try:
        with get_session() as s:
            CotizacionRepo.crear(
                s, folio="PCN-WEB1", cliente_nombre="ACME", linea_negocio="Nacional",
                origen="CALI", destino="MEDELLIN", precio_final=1000000, estado="generada",
                comercial_email=_EMAIL, pdf_path="s3://b/PCN-WEB1.pdf",
            )
        cli = _app()
        # lista
        r = cli.get("/web/mis-cotizaciones", headers=_auth())
        assert r.status_code == 200
        d = r.json()
        folios = [c["folio"] for c in d["cotizaciones"]]
        assert "PCN-WEB1" in folios
        assert next(c for c in d["cotizaciones"] if c["folio"] == "PCN-WEB1")["efectiva"] is None
        # marcar efectiva
        r2 = cli.post(
            "/web/cotizaciones/PCN-WEB1/seguimiento",
            json={"efectiva": True, "nota": "Cliente aceptó"}, headers=_auth(),
        )
        assert r2.status_code == 200 and r2.json()["efectiva"] is True
        # verificar persistencia
        with get_session() as s:
            cot = CotizacionRepo.obtener_por_folio(s, "PCN-WEB1")
            assert cot.efectiva is True and cot.seguimiento_nota == "Cliente aceptó"
    finally:
        _limpiar()


@requiere_pg
def test_seguimiento_de_otro_comercial_404(db_engine: object) -> None:
    from shared.db import get_session
    from shared.repos import CotizacionRepo

    try:
        with get_session() as s:
            CotizacionRepo.crear(
                s, folio="PCN-WEB2", cliente_nombre="X", comercial_email="otro@boterosoto.com.co",
            )
        r = _app().post(
            "/web/cotizaciones/PCN-WEB2/seguimiento", json={"efectiva": True}, headers=_auth()
        )
        assert r.status_code == 404  # no es del usuario autenticado
    finally:
        _limpiar()


@requiere_pg
def test_enviar_cotizacion_invoca_mcp_y_marca_enviada(
    db_engine: object, monkeypatch: pytest.MonkeyPatch
) -> None:
    """El endpoint de envío invoca enviar_correo del MCP (mockeado) y marca 'enviada'."""
    import orquestador.web as web
    from shared.db import get_session
    from shared.repos import CotizacionRepo

    async def _fake_mcp(nombre: str, args: dict[str, Any]) -> dict[str, Any]:
        assert nombre == "enviar_correo"
        assert args["confirmado_por"] == _EMAIL  # remitente = comercial autenticado
        assert args["para"] == ["cliente@x.com"]
        return {"enviado": True, "message_id": "<abc@boterosoto.com.co>"}

    monkeypatch.setattr(web, "_invocar_mcp", _fake_mcp)
    try:
        with get_session() as s:
            CotizacionRepo.crear(
                s, folio="PCN-WEB3", cliente_nombre="ACME", comercial_email=_EMAIL,
                pdf_path="s3://b/PCN-WEB3.pdf", estado="generada",
            )
        r = _app().post(
            "/web/cotizaciones/PCN-WEB3/enviar",
            json={"para": ["cliente@x.com"]}, headers=_auth(),
        )
        assert r.status_code == 200 and r.json()["enviado"] is True
        with get_session() as s:
            assert CotizacionRepo.obtener_por_folio(s, "PCN-WEB3").estado == "enviada"
    finally:
        _limpiar()


def _limpiar() -> None:
    from sqlalchemy import delete

    from shared.db import get_session
    from shared.models import Cotizacion, EventoConversacion, Sesion, UsuarioWeb

    clave = f"web/{_SESION}"
    with get_session() as s:
        s.execute(delete(EventoConversacion).where(EventoConversacion.space_id == clave))
        s.execute(delete(Sesion).where(Sesion.space_id == clave))
        s.execute(delete(Cotizacion).where(Cotizacion.folio.like("PCN-WEB%")))
        correos = [_EMAIL, "otro@boterosoto.com.co"]
        s.execute(delete(UsuarioWeb).where(UsuarioWeb.email.in_(correos)))
