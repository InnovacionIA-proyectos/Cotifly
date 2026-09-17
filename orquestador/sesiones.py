"""Gestor de sesiones conversacionales del Orquestador (§4.4 · F10).

El estado vive en Postgres por clave `space_id + thread_id`:
- un hilo de Chat = una cotización; un hilo nuevo abre una sesión nueva (aislamiento);
- el historial se entrega al LLM con una **ventana deslizante por tokens** que
  **conserva siempre el primer mensaje** (la solicitud original del comercial);
- tras `ttl_horas` de inactividad la sesión se **archiva** (la traza queda en BD).

El reloj y el estimador de tokens son inyectables para pruebas deterministas.
"""

from __future__ import annotations

from collections.abc import Callable
from datetime import UTC, datetime, timedelta

from sqlalchemy.orm import Session

from shared.config import settings
from shared.models import EventoConversacion, Sesion
from shared.repos import ConversacionRepo, SesionRepo


def estimar_tokens(texto: str | None) -> int:
    """Estimación barata de tokens (~4 caracteres/token). Suficiente para la ventana."""
    if not texto:
        return 0
    return max(1, len(texto) // 4)


class GestorSesiones:
    def __init__(
        self,
        ttl_horas: int | None = None,
        limite_tokens: int | None = None,
        reloj: Callable[[], datetime] | None = None,
        estimador: Callable[[str | None], int] | None = None,
    ) -> None:
        horas = ttl_horas if ttl_horas is not None else settings.sesion_ttl_horas
        self._ttl = timedelta(hours=horas)
        self._limite = limite_tokens if limite_tokens is not None else settings.sesion_limite_tokens
        self._reloj = reloj or (lambda: datetime.now(UTC))
        self._estimar = estimador or estimar_tokens

    def recuperar_o_crear(self, session: Session, space_id: str, thread_id: str) -> Sesion:
        """Devuelve la sesión del hilo, creándola activa si no existe."""
        return SesionRepo.obtener_o_crear(session, space_id, thread_id)

    def registrar_mensaje(
        self,
        session: Session,
        space_id: str,
        thread_id: str,
        rol: str,
        contenido: str | None,
        usuario_email: str | None = None,
        herramientas: dict | None = None,
    ) -> EventoConversacion:
        """Registra un turno y refresca la marca de actividad de la sesión."""
        self.recuperar_o_crear(session, space_id, thread_id)
        ahora = self._reloj()
        turno = ConversacionRepo.registrar_turno(
            session,
            space_id=space_id,
            thread_id=thread_id,
            rol=rol,
            contenido=contenido,
            usuario_email=usuario_email,
            herramientas=herramientas,
            cuando=ahora,
        )
        SesionRepo.tocar(session, space_id, thread_id, cuando=ahora)
        return turno

    def historial(
        self, session: Session, space_id: str, thread_id: str
    ) -> list[EventoConversacion]:
        """Ventana deslizante por tokens que conserva siempre el primer mensaje."""
        turnos = ConversacionRepo.historial(session, space_id, thread_id)
        if not turnos:
            return []

        primero = turnos[0]
        presupuesto = self._limite - self._estimar(primero.contenido)

        # Desde el más reciente hacia atrás, incluir lo que quepa en el presupuesto.
        recientes: list[EventoConversacion] = []
        for turno in reversed(turnos[1:]):
            costo = self._estimar(turno.contenido)
            if presupuesto - costo < 0:
                break
            presupuesto -= costo
            recientes.append(turno)

        return [primero, *reversed(recientes)]

    def archivar_inactivas(self, session: Session) -> int:
        """Archiva las sesiones activas cuya inactividad supera el TTL. Devuelve cuántas."""
        umbral = self._reloj() - self._ttl
        inactivas = SesionRepo.inactivas(session, umbral)
        for ses in inactivas:
            SesionRepo.archivar(session, ses.space_id, ses.thread_id)
        return len(inactivas)
