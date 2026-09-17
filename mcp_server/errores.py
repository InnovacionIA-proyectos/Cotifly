"""Convención de error estructurado del MCP (Fase 2).

Regla de la arquitectura: una herramienta **nunca** propaga una excepción cruda
al LLM; ante un fallo previsible devuelve un dict con la forma:

    {"error": {"codigo": "...", "mensaje": "...", "detalle": {...}}}

De esta manera el modelo recibe siempre un contrato estable (JSON) y puede
decidir qué hacer (pedir un dato, reintentar, informar al comercial) en lugar de
recibir un traceback. Los fallos *inesperados* (excepciones no controladas) los
captura el middleware de auditoría, que los audita y los convierte igualmente en
un error estructurado (ver `auditoria.py`).
"""

from __future__ import annotations

from typing import Any


def error_estructurado(
    codigo: str,
    mensaje: str,
    detalle: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Construye el cuerpo de error estándar de las herramientas del MCP.

    `codigo`: identificador estable y legible (p. ej. `TARIFA_NO_ENCONTRADA`).
    `mensaje`: descripción orientada al modelo/comercial.
    `detalle`: datos estructurados opcionales para desambiguar (candidatas, etc.).
    """
    error: dict[str, Any] = {"codigo": codigo, "mensaje": mensaje}
    if detalle:
        error["detalle"] = detalle
    return {"error": error}


def es_error(salida: Any) -> bool:
    """True si la salida de una herramienta es un error estructurado."""
    return isinstance(salida, dict) and "error" in salida
