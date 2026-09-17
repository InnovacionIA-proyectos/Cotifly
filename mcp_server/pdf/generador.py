"""Generación del PDF de cotización a partir del payload (§7.1 · F6).

- `construir_html`: elige el armador vendorizado según el payload —multi-sección
  (`secciones`) o tarifa única— y devuelve el HTML corporativo (puro, sin IO).
- `render_pdf` / `contar_paginas`: renderizan con weasyprint (libs nativas del
  contenedor). Si weasyprint no puede cargar sus libs, elevan
  `PdfRenderNoDisponible` en vez de un error críptico.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

from mcp_server.pdf.scripts import armar_cotizacion, armar_multi

# Los scripts resuelven ASSETS relativo a su ubicación; usamos la misma carpeta.
ASSETS = Path(armar_cotizacion.__file__).resolve().parent.parent / "assets"


class PdfRenderNoDisponible(Exception):
    """weasyprint no está instalado o no puede cargar sus librerías nativas."""


def construir_html(cotizacion: dict[str, Any]) -> str:
    """HTML de la cotización. `secciones` (lista) → multi; si no, tarifa única."""
    if cotizacion.get("secciones"):
        return armar_multi.construir_html(cotizacion)
    return armar_cotizacion.construir_html(cotizacion)


def _documento(html: str) -> Any:
    # weasyprint carga sus libs nativas al importarse: en Windows sin GTK el
    # propio import lanza OSError. Se captura aquí y en el render.
    try:
        from weasyprint import HTML

        return HTML(string=html, base_url=str(ASSETS)).render()
    except (ImportError, OSError) as e:  # pragma: no cover - depende del entorno
        raise PdfRenderNoDisponible(f"weasyprint no disponible: {e}") from e


def render_pdf(html: str, destino: str | Path) -> int:
    """Renderiza el HTML a `destino` (.pdf) y devuelve el número de páginas."""
    destino = Path(destino)
    destino.parent.mkdir(parents=True, exist_ok=True)
    doc = _documento(html)
    doc.write_pdf(str(destino))
    return len(doc.pages)


def render_pdf_bytes(html: str) -> tuple[bytes, int]:
    """Renderiza el HTML a **bytes** (para storage agnóstico: volumen o S3).

    Devuelve `(pdf_bytes, num_paginas)`. No escribe en disco: quien llama decide
    dónde guardarlo (ver `shared.almacen_pdf`).
    """
    doc = _documento(html)
    datos = doc.write_pdf()
    return datos, len(doc.pages)


def contar_paginas(html: str) -> int:
    """Número de páginas que produciría el HTML (sin escribir archivo)."""
    return len(_documento(html).pages)
