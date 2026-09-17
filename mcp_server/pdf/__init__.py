"""Capa de generación de PDF del MCP (§5.1 `generar_pdf` · F6).

Envuelve los scripts vendorizados de la skill (portada/carta, tabla de tarifa,
condiciones, firma) y produce el PDF corporativo. La generación de HTML es pura
y siempre testeable; el render a PDF usa weasyprint (libs nativas en el contenedor).
"""

from __future__ import annotations

from mcp_server.pdf.generador import (
    PdfRenderNoDisponible,
    construir_html,
    contar_paginas,
    render_pdf,
    render_pdf_bytes,
)

__all__ = [
    "PdfRenderNoDisponible",
    "construir_html",
    "contar_paginas",
    "render_pdf",
    "render_pdf_bytes",
]
