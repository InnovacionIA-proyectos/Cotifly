"""Tests del endpoint interno GET /sesiones/{space_id} (§4.2 · soporte).

- Sin token configurado → 404 (deshabilitado).
- Con token configurado: bearer correcto → 200 con sesiones y turnos; incorrecto → 401.
Requiere Postgres real (lee sesiones/eventos).
"""

from __future__ import annotations

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient
from sqlalchemy import delete

from orquestador.sesiones_api import router
from shared.config import settings
from tests.conftest import requiere_pg

pytestmark = requiere_pg

_SPACE = "spaces/API_SES"
_THREAD = "spaces/API_SES/threads/1"


def _app() -> TestClient:
    app = FastAPI()
    app.include_router(router)
    return TestClient(app)


def _sembrar() -> None:
    from shared.db import get_session
    from shared.repos import ConversacionRepo, SesionRepo

    with get_session() as s:
        SesionRepo.obtener_o_crear(s, _SPACE, _THREAD)
        ConversacionRepo.registrar_turno(
            s, space_id=_SPACE, thread_id=_THREAD, rol="user",
            contenido="Cotiza YUMBO→BUN.",
            # HU-002.2: `usuario_email` ya no existe (ahora `usuario_id`, sin fuente
            # hasta ClientePro/HU-004).
            usuario_id=None,
        )


def _limpiar() -> None:
    from shared.db import get_session
    from shared.models import EventoConversacion, Sesion

    with get_session() as s:
        s.execute(delete(EventoConversacion).where(EventoConversacion.space_id == _SPACE))
        s.execute(delete(Sesion).where(Sesion.space_id == _SPACE))


def test_sin_token_configurado_devuelve_404(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(settings, "internal_api_token", "")
    r = _app().get(f"/sesiones/{_SPACE}", headers={"Authorization": "Bearer x"})
    assert r.status_code == 404


def test_bearer_incorrecto_401(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(settings, "internal_api_token", "secreto")
    r = _app().get(f"/sesiones/{_SPACE}", headers={"Authorization": "Bearer malo"})
    assert r.status_code == 401


def test_bearer_correcto_devuelve_sesiones(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(settings, "internal_api_token", "secreto")
    try:
        _sembrar()
        r = _app().get(f"/sesiones/{_SPACE}", headers={"Authorization": "Bearer secreto"})
        assert r.status_code == 200
        cuerpo = r.json()
        assert cuerpo["space_id"] == _SPACE
        assert cuerpo["total_sesiones"] >= 1
        ses = next(s for s in cuerpo["sesiones"] if s["thread_id"] == _THREAD)
        assert ses["num_turnos"] >= 1
        # HU-002.2: `orquestador/sesiones_api.py` expone `usuario_id`, no `usuario_email`.
        assert ses["turnos"][0]["usuario_id"] is None
    finally:
        _limpiar()
