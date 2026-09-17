"""Tests unitarios puros del MCP (sin Postgres ni servidor).

Cubren el contrato de error (`errores.py`) y los ayudantes de resumen/serialización
de la auditoría (`auditoria.py`), incluidas las ramas de borde (no serializable,
truncado, fallback a `content`).
"""

from __future__ import annotations

from types import SimpleNamespace

from mcp_server import auditoria
from mcp_server.errores import error_estructurado, es_error


def test_error_estructurado_sin_detalle() -> None:
    out = error_estructurado("COD", "mensaje")
    assert out == {"error": {"codigo": "COD", "mensaje": "mensaje"}}


def test_error_estructurado_con_detalle() -> None:
    out = error_estructurado("COD", "mensaje", {"candidatas": [1, 2]})
    assert out["error"]["detalle"] == {"candidatas": [1, 2]}


def test_es_error() -> None:
    assert es_error({"error": {"codigo": "X", "mensaje": "y"}}) is True
    assert es_error({"status": "ok"}) is False
    assert es_error("no soy dict") is False


def test_jsonable_normal() -> None:
    assert auditoria._jsonable({"a": 1}) == {"a": 1}


def test_jsonable_no_serializable() -> None:
    salida = auditoria._jsonable({"obj": object()})
    # Objetos no serializables se reducen a repr vía default=str (sigue siendo dict).
    assert isinstance(salida, dict)


def test_jsonable_referencia_circular() -> None:
    # Una referencia circular hace fallar json.dumps aun con default=str.
    circular: dict = {}
    circular["self"] = circular
    salida = auditoria._jsonable(circular)
    assert "repr" in salida


def test_jsonable_truncado() -> None:
    grande = {"x": "a" * (auditoria._MAX_RESUMEN_CHARS + 100)}
    salida = auditoria._jsonable(grande)
    assert salida["truncado"] is True
    assert "preview" in salida


def test_resumen_de_structured_content() -> None:
    res = SimpleNamespace(structured_content={"status": "ok"}, content=None)
    assert auditoria._resumen_de(res) == {"status": "ok"}


def test_resumen_de_content_fallback() -> None:
    res = SimpleNamespace(
        structured_content=None,
        content=[SimpleNamespace(text="hola"), SimpleNamespace(text=None)],
    )
    assert auditoria._resumen_de(res) == ["hola"]


def test_resumen_de_vacio() -> None:
    res = SimpleNamespace(structured_content=None, content=None)
    assert auditoria._resumen_de(res) is None
