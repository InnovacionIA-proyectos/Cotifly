"""Middleware de auditoría del MCP Server (Fase 2).

Cada invocación de herramienta queda registrada en `eventos_herramienta`
**automáticamente**, sin que la herramienta ni el LLM tengan que hacer nada:
nombre, input, resumen del output, éxito y duración en ms. Es el mecanismo que
garantiza la trazabilidad de la §7.3 con independencia del modelo.

También es la última red de seguridad del contrato de error: detecta tanto los
errores de negocio (`{"error": {...}}`) como los resultados marcados `is_error`
por FastMCP y las excepciones que lleguen hasta aquí, y los audita como fallo. El
enmascarado de detalles internos lo garantiza el servidor con
`mask_error_details=True` (ver `server.py`), de modo que el cliente nunca ve un
traceback crudo.
"""

from __future__ import annotations

import json
import time
from typing import Any

from anyio import to_thread
from fastmcp.server.middleware import Middleware, MiddlewareContext
from mcp import types as mt

from mcp_server.errores import es_error
from shared.db import get_session
from shared.repos import AuditoriaRepo

# Límite defensivo del tamaño auditado (evita inflar la BD con outputs enormes).
_MAX_RESUMEN_CHARS = 4000


def _jsonable(valor: Any) -> Any:
    """Reduce el valor a algo serializable en JSONB; recorta si es gigante."""
    try:
        texto = json.dumps(valor, ensure_ascii=False, default=str)
    except (TypeError, ValueError):
        return {"repr": str(valor)[:_MAX_RESUMEN_CHARS]}
    if len(texto) > _MAX_RESUMEN_CHARS:
        return {"truncado": True, "preview": texto[:_MAX_RESUMEN_CHARS]}
    return valor


def _resumen_de(result: Any) -> Any:
    """Extrae un resumen JSON del resultado de la herramienta."""
    contenido = getattr(result, "structured_content", None)
    if contenido is not None:
        return _jsonable(contenido)
    bloques = getattr(result, "content", None)
    if bloques:
        textos = [getattr(b, "text", None) for b in bloques]
        return _jsonable([t for t in textos if t is not None])
    return None


def _persistir(
    herramienta: str,
    entrada: dict[str, Any] | None,
    resumen: Any,
    exito: bool,
    duracion_ms: int,
    thread_id: str | None,
) -> None:
    """Escribe la fila de auditoría. Nunca rompe la llamada si la BD falla."""
    try:
        with get_session() as s:
            AuditoriaRepo.registrar(
                s,
                herramienta=herramienta,
                input=_jsonable(entrada) if entrada else None,
                output_resumen=resumen if isinstance(resumen, dict) else {"valor": resumen},
                exito=exito,
                duracion_ms=duracion_ms,
                thread_id=thread_id,
            )
    except Exception:  # pragma: no cover - la auditoría no debe tumbar la tool
        pass


class AuditoriaMiddleware(Middleware):
    """Audita toda invocación de herramienta y blinda el contrato de error."""

    async def on_call_tool(
        self,
        context: MiddlewareContext[mt.CallToolRequestParams],
        call_next: Any,
    ) -> Any:
        inicio = time.perf_counter()
        params = context.message
        nombre = params.name
        args: dict[str, Any] = dict(params.arguments or {})
        thread_id = args.get("thread_id")

        exito = True
        resumen: Any = None
        try:
            result = await call_next(context)
            resumen = _resumen_de(result)
            # Fallo si FastMCP marcó is_error o si la tool devolvió {"error": {...}}.
            if getattr(result, "is_error", False) or es_error(
                getattr(result, "structured_content", None)
            ):
                exito = False
            return result
        except Exception:
            # Excepción propagada (FastMCP ya la envuelve en ToolError y enmascara el
            # detalle interno): se audita como fallo y se re-lanza sin tocar.
            exito = False
            resumen = {"error": {"codigo": "EXCEPCION"}}
            raise
        finally:
            duracion_ms = int((time.perf_counter() - inicio) * 1000)
            await to_thread.run_sync(
                _persistir, nombre, args, resumen, exito, duracion_ms, thread_id
            )
