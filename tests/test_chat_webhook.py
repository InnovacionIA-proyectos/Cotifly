"""Tests del webhook de Google Chat (Fase 12): JWT, ACK asíncrono y encolado.

- JWT: token válido → 200/ACK; ausente o inválido → 401 (fixtures jwt_valido/invalido).
- ACK: el endpoint responde sin esperar al procesamiento (encola y retorna).
- Worker: procesa los eventos encolados y sobrevive a un evento que falla.
- Encolado: el worker corre el Agent Runner y crea la sesión por space+thread.
- Verificador real (google-auth): pasa audience/certs correctos y exige el emisor.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import delete

from orquestador.agente import AgentRunner, HerramientasMCP
from orquestador.app import crear_app
from orquestador.gchat import EventoChat, JwtInvalido, parsear_evento
from orquestador.procesador import ProcesadorCotizacion
from orquestador.worker import ColaEventos
from tests.conftest import requiere_pg
from tests.test_agente import _RESP_FINAL, _LLMFake, _servidor_precio

_FIXTURES = Path(__file__).parent / "fixtures" / "chat"
_TOKEN_VALIDO = (_FIXTURES / "jwt_valido.txt").read_text(encoding="utf-8").strip()
_TOKEN_INVALIDO = (_FIXTURES / "jwt_invalido.txt").read_text(encoding="utf-8").strip()
_EVENTO_REAL = json.loads((_FIXTURES / "evento_mensaje.json").read_text(encoding="utf-8"))

class _VerificadorFake:
    """Acepta solo el token de la fixture 'valido'; el resto lanza JwtInvalido."""

    def verificar(self, token: str) -> dict[str, Any]:
        if token != _TOKEN_VALIDO:
            raise JwtInvalido("token de prueba no reconocido")
        return {"iss": "chat@system.gserviceaccount.com", "aud": "123456"}


class _ColaFake:
    """Cola que solo registra lo encolado (sin worker); ACK sin procesamiento."""

    def __init__(self) -> None:
        self.eventos: list[EventoChat] = []

    async def iniciar(self) -> None: ...
    async def detener(self) -> None: ...
    async def encolar(self, evento: EventoChat) -> None:
        self.eventos.append(evento)


def _app_con_cola_fake() -> tuple[TestClient, _ColaFake]:
    cola = _ColaFake()
    app = crear_app(verificador=_VerificadorFake(), cola=cola)  # type: ignore[arg-type]
    return TestClient(app), cola


@pytest.fixture(autouse=True)
def _jwt_activo(monkeypatch: pytest.MonkeyPatch) -> None:
    """Fuerza la verificación de JWT en este módulo (el .env local puede apagarla).

    Los tests de este archivo validan el comportamiento con verificación activa; el
    caso desactivado se prueba aparte poniéndola en False explícitamente.
    """
    from shared.config import settings

    monkeypatch.setattr(settings, "gchat_verificar_jwt", True)


# --- JWT ---------------------------------------------------------------------


def test_jwt_valido_ack_200() -> None:
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post(
            "/chat/events", json=_EVENTO_REAL, headers={"Authorization": f"Bearer {_TOKEN_VALIDO}"}
        )
    assert r.status_code == 200
    assert r.json()["text"]  # recibo de ACK
    assert len(cola.eventos) == 1  # el mensaje quedó encolado


def test_jwt_ausente_401() -> None:
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post("/chat/events", json=_EVENTO_REAL)
    assert r.status_code == 401
    assert cola.eventos == []  # sin JWT no se encola nada


def test_jwt_invalido_401() -> None:
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post(
            "/chat/events",
            json=_EVENTO_REAL,
            headers={"Authorization": f"Bearer {_TOKEN_INVALIDO}"},
        )
    assert r.status_code == 401
    assert cola.eventos == []


def test_bearer_malformado_401() -> None:
    client, _ = _app_con_cola_fake()
    with client:
        r = client.post(
            "/chat/events", json=_EVENTO_REAL, headers={"Authorization": _TOKEN_VALIDO}
        )
    assert r.status_code == 401  # falta el prefijo "Bearer "


# --- ACK asíncrono y parseo --------------------------------------------------


def test_ack_no_espera_procesamiento() -> None:
    """El endpoint solo encola: retorna sin invocar al procesador (ACK < 30 s)."""
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post(
            "/chat/events", json=_EVENTO_REAL, headers={"Authorization": f"Bearer {_TOKEN_VALIDO}"}
        )
    assert r.status_code == 200
    # El evento se parseó y encoló con la clave correcta del hilo.
    ev = cola.eventos[0]
    assert ev.space_id == "spaces/AAAAbot_cotizador"
    assert ev.thread_id == "spaces/AAAAbot_cotizador/threads/tLongyin19032026"
    assert ev.usuario_email == "nvargas@boterosoto.com.co"
    assert "YUMBO" in (ev.texto or "")


def test_evento_no_mensaje_no_encola() -> None:
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post(
            "/chat/events",
            json={"type": "ADDED_TO_SPACE", "space": {"name": "spaces/X"}},
            headers={"Authorization": f"Bearer {_TOKEN_VALIDO}"},
        )
    assert r.status_code == 200
    assert r.json() == {}  # ACK sin cuerpo
    assert cola.eventos == []


def test_parsear_evento_real() -> None:
    ev = parsear_evento(_EVENTO_REAL)
    assert ev.es_mensaje is True
    assert ev.tipo == "MESSAGE"
    assert ev.space_id == "spaces/AAAAbot_cotizador"


def test_parsear_captura_nombre_del_comercial() -> None:
    # F2.1: además del correo, se captura el displayName para el contexto/firma.
    ev = parsear_evento(_EVENTO_REAL)
    assert ev.usuario_email == "nvargas@boterosoto.com.co"
    assert ev.usuario_nombre == "Nathaly Vargas Garcia"


def test_parsear_formato_nuevo_chat_messagepayload() -> None:
    """Modelo HTTP nuevo: los datos vienen en chat.messagePayload (sin type)."""
    payload = {
        "chat": {
            "messagePayload": {
                "message": {
                    "text": "Cotiza YUMBO a BUN",
                    "thread": {"name": "spaces/S/threads/T"},
                    "sender": {
                        "email": "nvargas@boterosoto.com.co",
                        "displayName": "Nathaly Vargas",
                    },
                },
                "space": {"name": "spaces/S"},
            }
        }
    }
    ev = parsear_evento(payload)
    assert ev.es_mensaje is True  # tipo inferido como MESSAGE
    assert ev.space_id == "spaces/S"
    assert ev.thread_id == "spaces/S/threads/T"
    assert ev.texto == "Cotiza YUMBO a BUN"
    assert ev.usuario_email == "nvargas@boterosoto.com.co"
    assert ev.usuario_nombre == "Nathaly Vargas"


def test_webhook_sin_jwt_cuando_verificacion_desactivada(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Con GCHAT_VERIFICAR_JWT=false, un evento sin JWT se acepta y se encola."""
    from shared.config import settings

    monkeypatch.setattr(settings, "gchat_verificar_jwt", False)
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post("/chat/events", json=_EVENTO_REAL)  # sin Authorization
    assert r.status_code == 200
    assert len(cola.eventos) == 1


def test_webhook_formato_addon_ack_hostappdataaction(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Evento del modelo add-on → ACK envuelto en hostAppDataAction (como exige)."""
    from shared.config import settings

    monkeypatch.setattr(settings, "gchat_verificar_jwt", False)
    payload = {
        "chat": {
            "messagePayload": {
                "message": {
                    "text": "hola",
                    "thread": {"name": "spaces/S/threads/T"},
                    "sender": {"email": "d@x.co", "displayName": "D"},
                },
                "space": {"name": "spaces/S"},
            }
        }
    }
    client, cola = _app_con_cola_fake()
    with client:
        r = client.post("/chat/events", json=payload)
    assert r.status_code == 200
    cuerpo = r.json()
    accion = cuerpo["hostAppDataAction"]["chatDataAction"]["createMessageAction"]
    assert accion["message"]["text"]  # el recibo va en formato add-on
    assert len(cola.eventos) == 1
    assert cola.eventos[0].texto == "hola"


# --- Worker ------------------------------------------------------------------


async def test_worker_procesa_encolados() -> None:
    recibidos: list[EventoChat] = []

    async def proc(ev: EventoChat) -> None:
        recibidos.append(ev)

    cola = ColaEventos(proc)
    await cola.iniciar()
    ev = EventoChat(tipo="MESSAGE", space_id="spaces/W", thread_id="t", texto="hola")
    await cola.encolar(ev)
    await cola.unir()
    await cola.detener()
    assert recibidos == [ev]


async def test_worker_sobrevive_a_evento_que_falla() -> None:
    procesados: list[str] = []

    async def proc(ev: EventoChat) -> None:
        if ev.texto == "boom":
            raise ValueError("fallo simulado")
        procesados.append(ev.texto or "")

    cola = ColaEventos(proc)
    await cola.iniciar()
    await cola.encolar(EventoChat(tipo="MESSAGE", space_id="s", thread_id="t", texto="boom"))
    await cola.encolar(EventoChat(tipo="MESSAGE", space_id="s", thread_id="t", texto="ok"))
    await cola.unir()
    await cola.detener()
    assert procesados == ["ok"]  # el segundo se procesó pese al fallo del primero


# --- Encolado → sesión (integración con el Agent Runner mockeado) ------------


@requiere_pg
async def test_worker_dispara_runner_y_crea_sesion(db_engine: object) -> None:
    space, thread = "spaces/WK", "spaces/WK/threads/1"
    try:
        runner = AgentRunner(
            completar=_LLMFake(_RESP_FINAL),
            herramientas=HerramientasMCP(_servidor_precio()),
        )
        cola = ColaEventos(ProcesadorCotizacion(runner=runner))
        await cola.iniciar()
        await cola.encolar(
            EventoChat(
                tipo="MESSAGE", space_id=space, thread_id=thread,
                texto="Cotiza YUMBO→BUN.", usuario_email="nvargas@boterosoto.com.co",
            )
        )
        await cola.unir()
        await cola.detener()

        # El worker corrió el turno: la sesión existe y quedaron los eventos.
        from shared.db import get_session
        from shared.models import Sesion
        from shared.repos import ConversacionRepo

        with get_session() as s:
            ses = s.get(Sesion, (space, thread))
            assert ses is not None and ses.estado == "activa"
            turnos = ConversacionRepo.historial(s, space, thread)
            assert [t.rol for t in turnos] == ["user", "assistant"]
            # HU-002.2: ya no se persiste identidad por correo (ver test_agente.py).
            assert turnos[0].usuario_id is None
    finally:
        _limpiar_sesion(space)


def _limpiar_sesion(space: str) -> None:
    from shared.db import get_session
    from shared.models import EventoConversacion, Sesion

    with get_session() as s:
        s.execute(delete(EventoConversacion).where(EventoConversacion.space_id == space))
        s.execute(delete(Sesion).where(Sesion.space_id == space))


# --- Verificador real (google-auth) ------------------------------------------


def test_google_verifier_pasa_audience_y_certs(monkeypatch: pytest.MonkeyPatch) -> None:
    from orquestador import gchat

    captura: dict[str, Any] = {}

    def _fake_verify(token: str, request: object, audience: Any = None, certs_url: Any = None):
        captura["audience"] = audience
        captura["certs_url"] = certs_url
        return {"iss": gchat.CHAT_ISSUER, "aud": audience, "email": gchat.CHAT_ISSUER}

    monkeypatch.setattr("google.oauth2.id_token.verify_token", _fake_verify)
    claims = gchat.GoogleChatJwtVerifier("999888").verificar("token-x")
    assert captura["audience"] == "999888"
    assert captura["certs_url"] == gchat.CHAT_CERTS_URL
    assert claims["iss"] == gchat.CHAT_ISSUER


def test_google_verifier_emisor_inesperado(monkeypatch: pytest.MonkeyPatch) -> None:
    from orquestador import gchat

    monkeypatch.setattr(
        "google.oauth2.id_token.verify_token",
        lambda *a, **k: {"iss": "atacante@evil.com", "email": "atacante@evil.com"},
    )
    with pytest.raises(JwtInvalido):
        gchat.GoogleChatJwtVerifier("999888").verificar("token-x")


def test_google_verifier_firma_invalida(monkeypatch: pytest.MonkeyPatch) -> None:
    from orquestador import gchat

    def _boom(*a: object, **k: object):
        raise ValueError("firma inválida")

    monkeypatch.setattr("google.oauth2.id_token.verify_token", _boom)
    with pytest.raises(JwtInvalido):
        gchat.GoogleChatJwtVerifier("999888").verificar("token-x")


def test_google_verifier_sin_project_number() -> None:
    from orquestador import gchat

    with pytest.raises(JwtInvalido):
        gchat.GoogleChatJwtVerifier("").verificar("token-x")
