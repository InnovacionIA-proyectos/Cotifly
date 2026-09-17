"""Disparador bajo demanda de la sincronización BD → Google Sheets.

`POST /internal/sync-sheets`: fuerza un volcado inmediato de la trazabilidad al
libro de Sheets del tablero (además del barrido periódico del `lifespan`). Es una
utilidad de **operación/soporte**: se protege con el mismo bearer interno que
`/sesiones/...` (`INTERNAL_API_TOKEN`). Sin token configurado responde 404.
"""

from __future__ import annotations

import asyncio
from typing import Any

from fastapi import APIRouter, Header, HTTPException

from shared.config import settings

router = APIRouter(tags=["soporte"])


def _autorizar(authorization: str | None) -> None:
    """Exige el bearer interno. Sin token configurado, el endpoint no existe (404)."""
    if not settings.internal_api_token:
        raise HTTPException(status_code=404, detail="No encontrado.")
    if authorization != f"Bearer {settings.internal_api_token}":
        raise HTTPException(status_code=401, detail="No autorizado.")


@router.post("/internal/sync-sheets")
async def sync_sheets(authorization: str | None = Header(default=None)) -> dict[str, Any]:
    """Vuelca la BD al libro de Sheets ahora mismo y devuelve el conteo por hoja."""
    _autorizar(authorization)
    if not settings.sheets_spreadsheet_id:
        raise HTTPException(status_code=400, detail="Falta SHEETS_SPREADSHEET_ID.")

    from shared.db import get_session
    from shared.sheets_sync import SheetsNoDisponible, sincronizar

    def _volcar() -> dict[str, int]:
        with get_session() as s:
            return sincronizar(s)

    try:
        conteo = await asyncio.to_thread(_volcar)
    except SheetsNoDisponible as e:
        raise HTTPException(status_code=503, detail=str(e)) from e
    return {"sincronizado": True, "filas_por_hoja": conteo}
