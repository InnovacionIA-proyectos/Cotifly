"""Capa de fuentes de tarifa del MCP (§6 de la arquitectura).

Patrón Strategy/Provider: la herramienta `buscar_tarifa` no sabe de dónde vienen
los datos. Hoy la única fuente activa es `DriveFlatFileProvider` (archivos planos
en Google Drive); mañana `ApiFletesProvider` se enchufa por configuración sin
tocar nada aguas arriba (F16).
"""

from __future__ import annotations

from mcp_server.tarifas.api_provider import ApiFletesProvider, DobleLecturaProvider
from mcp_server.tarifas.base import (
    ArchivoTarifa,
    Candidata,
    EntradaBuscarTarifa,
    FuenteTarifaNoDisponible,
    LocalRepositorioArchivos,
    RepositorioArchivos,
    ResultadoTarifa,
    TarifaProvider,
    normalizar,
)
from mcp_server.tarifas.drive_provider import DriveFlatFileProvider
from mcp_server.tarifas.router import TarifaRouter, construir_router

__all__ = [
    "ApiFletesProvider",
    "ArchivoTarifa",
    "Candidata",
    "DobleLecturaProvider",
    "DriveFlatFileProvider",
    "EntradaBuscarTarifa",
    "FuenteTarifaNoDisponible",
    "LocalRepositorioArchivos",
    "RepositorioArchivos",
    "ResultadoTarifa",
    "TarifaProvider",
    "TarifaRouter",
    "construir_router",
    "normalizar",
]
