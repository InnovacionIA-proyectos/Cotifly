"""Servidor de PDFs del Orquestador (§4 · F9).

Expone `GET /pdfs/{archivo}.pdf` para que el comercial abra la cotización desde el
link publicado o el visor embebido del front. Lee del **almacén configurado**
(volumen local o S3/MinIO) por streaming, así la URL pública es la misma sin
importar el backend. Valida el nombre (anti path traversal) y responde 404 si no
existe. Se sirve **inline** para previsualizar en el navegador.
"""

from __future__ import annotations

import re

from fastapi import APIRouter, HTTPException
from fastapi.responses import Response

from shared.almacen_pdf import construir_almacen

router = APIRouter(tags=["pdfs"])

# Solo folios corporativos: PCN-DDMMAAAA(-vN). Bloquea '/', '..', etc.
_ARCHIVO_RE = re.compile(r"^[A-Za-z0-9_-]+\.pdf$")


@router.get("/pdfs/{archivo}")
def descargar_pdf(archivo: str) -> Response:
    """Devuelve el PDF de la cotización desde el almacén, o 404 si no existe.

    Inline (no descarga) para previsualizarlo embebido; "Guardar como" conserva el
    nombre del folio.
    """
    if not _ARCHIVO_RE.match(archivo):
        raise HTTPException(status_code=404, detail="Nombre de archivo inválido.")
    datos = construir_almacen().leer(archivo)
    if datos is None:
        raise HTTPException(status_code=404, detail="PDF no encontrado.")
    return Response(
        content=datos,
        media_type="application/pdf",
        headers={"Content-Disposition": f'inline; filename="{archivo}"'},
    )
