"""Tests del gestor de sesiones (Fase 10). Postgres real, reloj/estimador inyectados."""

from __future__ import annotations

from datetime import UTC, datetime, timedelta

from sqlalchemy.orm import Session

from orquestador.sesiones import GestorSesiones, estimar_tokens
from shared.models import Sesion
from tests.conftest import requiere_pg

pytestmark = requiere_pg

_T0 = datetime(2026, 7, 16, 12, 0, 0, tzinfo=UTC)


def _reloj_fijo(valor: list[datetime]):
    return lambda: valor[0]


def _reloj_incremental():
    """Reloj que avanza 1 s por llamada → timestamps distintos y ordenados."""
    estado = {"n": 0}

    def _r() -> datetime:
        estado["n"] += 1
        return _T0 + timedelta(seconds=estado["n"])

    return _r


# --- sesión por clave + aislamiento por hilo --------------------------------


def test_recuperar_o_crear_y_aislamiento(db_session: Session) -> None:
    g = GestorSesiones()
    s1 = g.recuperar_o_crear(db_session, "spaces/A", "threads/1")
    assert s1.estado == "activa"
    # Misma clave → misma sesión.
    s1b = g.recuperar_o_crear(db_session, "spaces/A", "threads/1")
    assert (s1b.space_id, s1b.thread_id) == (s1.space_id, s1.thread_id)
    # Hilo nuevo → sesión nueva (cotización nueva).
    s2 = g.recuperar_o_crear(db_session, "spaces/A", "threads/2")
    assert (s2.space_id, s2.thread_id) != (s1.space_id, s1.thread_id)


def test_registrar_mensaje_actualiza_actividad(db_session: Session) -> None:
    reloj = [_T0]
    g = GestorSesiones(reloj=_reloj_fijo(reloj))
    g.registrar_mensaje(db_session, "spaces/A", "threads/M", "user", "Hola")
    ses = db_session.get(Sesion, ("spaces/A", "threads/M"))
    assert ses is not None
    assert ses.ultima_actividad == _T0


# --- ventana deslizante por tokens ------------------------------------------


def test_historial_completo_si_cabe(db_session: Session) -> None:
    g = GestorSesiones(limite_tokens=10_000, reloj=_reloj_incremental())
    for i in range(5):
        g.registrar_mensaje(db_session, "spaces/H", "threads/full", "user", f"mensaje {i}")
    hist = g.historial(db_session, "spaces/H", "threads/full")
    assert [t.contenido for t in hist] == [f"mensaje {i}" for i in range(5)]


def test_historial_trunca_conservando_primero(db_session: Session) -> None:
    # Estimador simple: 1 token por mensaje; límite 3 → primero + 2 más recientes.
    g = GestorSesiones(limite_tokens=3, estimador=lambda _t: 1, reloj=_reloj_incremental())
    contenidos = ["ORIGINAL", "m1", "m2", "m3", "m4"]
    for c in contenidos:
        g.registrar_mensaje(db_session, "spaces/H", "threads/trunc", "user", c)
    hist = [t.contenido for t in g.historial(db_session, "spaces/H", "threads/trunc")]
    # Siempre conserva el primero; llena con los más recientes que quepan.
    assert hist[0] == "ORIGINAL"
    assert hist[-1] == "m4"
    assert "m1" not in hist  # el del medio se descarta (ventana deslizante)
    assert len(hist) == 3


def test_historial_conserva_primero_aunque_exceda(db_session: Session) -> None:
    g = GestorSesiones(limite_tokens=1, estimador=lambda _t: 5, reloj=_reloj_incremental())
    g.registrar_mensaje(db_session, "spaces/H", "threads/big", "user", "solicitud original")
    g.registrar_mensaje(db_session, "spaces/H", "threads/big", "assistant", "respuesta")
    hist = g.historial(db_session, "spaces/H", "threads/big")
    assert [t.contenido for t in hist] == ["solicitud original"]


def test_historial_vacio(db_session: Session) -> None:
    assert GestorSesiones().historial(db_session, "spaces/H", "threads/none") == []


# --- timeout / archivado -----------------------------------------------------


def test_archivar_inactivas_por_timeout(db_session: Session) -> None:
    reloj = [_T0]
    g = GestorSesiones(ttl_horas=24, reloj=_reloj_fijo(reloj))

    # Sesión "vieja": último mensaje hace 25 h.
    g.registrar_mensaje(db_session, "spaces/T", "threads/vieja", "user", "hace rato")
    # Sesión "fresca": actividad reciente (avanzamos el reloj 25 h para todos).
    reloj[0] = _T0 + timedelta(hours=25)
    g.registrar_mensaje(db_session, "spaces/T", "threads/fresca", "user", "ahora")

    archivadas = g.archivar_inactivas(db_session)
    assert archivadas == 1
    assert db_session.get(Sesion, ("spaces/T", "threads/vieja")).estado == "archivada"
    assert db_session.get(Sesion, ("spaces/T", "threads/fresca")).estado == "activa"


# --- estimador ---------------------------------------------------------------


def test_estimar_tokens() -> None:
    assert estimar_tokens(None) == 0
    assert estimar_tokens("") == 0
    assert estimar_tokens("abcd") == 1
    assert estimar_tokens("a" * 40) == 10
