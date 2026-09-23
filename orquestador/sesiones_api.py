"""Endpoint interno de inspección de sesiones (§4.2 · soporte).

`GET /sesiones/{space_id}`: lista las sesiones de un space y, por cada hilo, los
últimos turnos de conversación. Es una herramienta de **soporte**, no pública: se
protege con un bearer estático (`INTERNAL_API_TOKEN`). Si el token no está
configurado, el endpoint responde 404 (deshabilitado), para no exponer datos de
conversación por accidente. Solo lee la BD; no dispara lógica de negocio.
"""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Header, HTTPException

from shared.config import settings

router = APIRouter(tags=["soporte"])

# Cuántos turnos recientes se devuelven por hilo (evita respuestas enormes).
_MAX_TURNOS = 20


def _autorizar(authorization: str | None) -> None:
    """Exige el bearer interno. Sin token configurado, el endpoint no existe (404)."""
    if not settings.internal_api_token:
        raise HTTPException(status_code=404, detail="No encontrado.")
    esperado = f"Bearer {settings.internal_api_token}"
    if authorization != esperado:
        raise HTTPException(status_code=401, detail="No autorizado.")


@router.get("/sesiones/{space_id:path}")
def inspeccionar_sesion(
    space_id: str,
    authorization: str | None = Header(default=None),
) -> dict[str, Any]:
    """Devuelve las sesiones del space y los últimos turnos de cada hilo."""
    _autorizar(authorization)

    from shared.db import get_session
    from shared.repos import ConversacionRepo, SesionRepo

    with get_session() as s:
        sesiones = SesionRepo.por_space(s, space_id)
        salida = []
        for ses in sesiones:
            turnos = ConversacionRepo.historial(s, ses.space_id, ses.thread_id)
            salida.append(
                {
                    "thread_id": ses.thread_id,
                    "estado": ses.estado,
                    "ultima_actividad": (
                        ses.ultima_actividad.isoformat() if ses.ultima_actividad else None
                    ),
                    "num_turnos": len(turnos),
                    "turnos": [
                        {
                            "rol": t.rol,
                            # HU-002.2: `usuario_email` ya no existe (ahora `usuario_id`,
                            # entero, sin fuente hasta ClientePro/HU-004).
                            "usuario_id": t.usuario_id,
                            "contenido": t.contenido,
                            "herramientas": t.herramientas,
                            "created_at": t.created_at.isoformat() if t.created_at else None,
                        }
                        for t in turnos[-_MAX_TURNOS:]
                    ],
                }
            )
    return {"space_id": space_id, "total_sesiones": len(salida), "sesiones": salida}
