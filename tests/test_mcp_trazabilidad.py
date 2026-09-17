"""Tests puros de la trazabilidad del MCP y el contrato de error uniforme.

- `_datos_trazabilidad`: extrae costo/márgenes/precio/destino del payload de forma
  tolerante (§7.3) para que `generar_pdf` los persista.
- `_tool_segura`: cualquier excepción inesperada se convierte en `{"error":{...}}`
  (§5.2b), nunca escapa cruda; los resultados normales pasan sin tocar.
"""

from __future__ import annotations

import json
from pathlib import Path

from mcp_server.errores import es_error
from mcp_server.server import _datos_trazabilidad, _num, _tool_segura

FIX = Path(__file__).parent / "fixtures" / "cotizacion" / "ejemplo-solicitud.json"


# --- _num --------------------------------------------------------------------


def test_num_tolerante() -> None:
    assert _num(2850000) == 2850000.0
    assert _num("3.294.798") == 3294798.0  # formato COP con puntos de miles
    assert _num(0.135) == 0.135
    assert _num(None) is None
    assert _num("") is None


# --- _datos_trazabilidad -----------------------------------------------------


def test_extrae_destino_del_primer_renglon_y_campos_top_level() -> None:
    cot = json.loads(FIX.read_text(encoding="utf-8"))
    cot.update(
        {
            "costo": 3000000,
            "margen_politica": 0.135,
            "margen_aplicado": 0.11,
            "precio_final": 3370787,
            "fuente_tarifa": "drive_flat_files",
            "origen_margen": "sap_tm",
            "cliente_id_sap": "15927",
        }
    )
    d = _datos_trazabilidad(cot)
    assert d["destino"] == "BUN"  # del primer renglón de tarifa.filas
    assert d["costo"] == 3000000.0
    assert d["margen_politica"] == 0.135
    assert d["margen_aplicado"] == 0.11
    assert d["precio_final"] == 3370787.0
    assert d["fuente_tarifa"] == "drive_flat_files"
    assert d["origen_margen"] == "sap_tm"
    assert d["cliente_id_sap"] == "15927"


def test_campos_ausentes_quedan_none_sin_romper() -> None:
    d = _datos_trazabilidad({"tarifa": {"origen": "YUMBO"}})
    assert d["costo"] is None
    assert d["destino"] is None
    assert d["precio_final"] is None
    assert d["origen_margen"] is None


def test_precio_acepta_alias_precio() -> None:
    assert _datos_trazabilidad({"precio": 999})["precio_final"] == 999.0


# --- _tool_segura ------------------------------------------------------------


def test_tool_segura_convierte_excepcion_en_error_estructurado() -> None:
    @_tool_segura
    def rompe(x: int) -> dict:
        raise RuntimeError("BD caída")

    salida = rompe(1)
    assert es_error(salida)
    assert salida["error"]["codigo"] == "ERROR_INTERNO"
    assert "BD caída" not in json.dumps(salida)  # no filtra el detalle interno


def test_tool_segura_deja_pasar_resultado_normal() -> None:
    @_tool_segura
    def ok() -> dict:
        return {"valor": 42}

    assert ok() == {"valor": 42}


def test_tool_segura_conserva_nombre_y_firma() -> None:
    import inspect

    @_tool_segura
    def buscar(linea: str, origen: str) -> dict:
        return {}

    assert buscar.__name__ == "buscar"
    assert list(inspect.signature(buscar).parameters) == ["linea", "origen"]
