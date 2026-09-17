"""Capa de margen del MCP (Componente 2, §5.1 · F4).

`obtener_margen` toma el Margen Propuesto de SAP TM (por cliente y línea) con
fallback a la política por línea. La fuente de datos es intercambiable por
configuración (`SAP_TM_MODO=export_json|odata`) sin cambiar el contrato de la
herramienta: hoy se lee el export JSON; el salto a OData en vivo es solo un flag.
"""

from __future__ import annotations

from mcp_server.margen.base import (
    EntradaObtenerMargen,
    FuenteMargen,
    FuenteMargenNoDisponible,
    ResultadoMargen,
    es_anomalo,
    normalizar_value,
)
from mcp_server.margen.export_fuente import ExportJsonFuente
from mcp_server.margen.odata_fuente import ODataFuente
from mcp_server.margen.servicio import MargenResolver, construir_resolver

__all__ = [
    "EntradaObtenerMargen",
    "ExportJsonFuente",
    "FuenteMargen",
    "FuenteMargenNoDisponible",
    "MargenResolver",
    "ODataFuente",
    "ResultadoMargen",
    "construir_resolver",
    "es_anomalo",
    "normalizar_value",
]
