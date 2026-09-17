"""Tests de `calcular_precio` (Fase 5). Puros y deterministas, sin dependencias.

Incluyen los valores exactos del §5.1, markup vs margen, bordes (m=0, m→1,
margen negativo) y un property test (hypothesis): con m∈[0,0.6) precio ≥ costo.
"""

from __future__ import annotations

import pytest
from hypothesis import given
from hypothesis import strategies as st

from mcp_server.precio import PrecioInvalido, calcular_precio


def test_valores_exactos_arquitectura() -> None:
    r = calcular_precio(2850000, 0.135, "margen")
    assert r.precio == 3294798
    assert r.utilidad == 444798
    assert r.costo == 2850000
    assert r.margen == 0.135
    assert r.metodo == "margen"


def test_markup_vs_margen() -> None:
    margen = calcular_precio(2850000, 0.135, "margen")
    markup = calcular_precio(2850000, 0.135, "markup")
    assert margen.precio == 3294798
    assert markup.precio == 3234750
    assert markup.precio < margen.precio  # markup rinde menos precio que margen


def test_borde_margen_cero() -> None:
    r = calcular_precio(1000000, 0.0, "margen")
    assert r.precio == 1000000
    assert r.utilidad == 0


def test_borde_margen_uno_division_por_cero() -> None:
    with pytest.raises(PrecioInvalido, match="< 1"):
        calcular_precio(1000000, 1.0, "margen")


def test_markup_admite_margen_alto() -> None:
    # markup no divide por (1-m); un recargo alto es válido.
    r = calcular_precio(1000000, 1.5, "markup")
    assert r.precio == 2500000


def test_rechaza_margen_negativo() -> None:
    with pytest.raises(PrecioInvalido, match="negativo"):
        calcular_precio(1000000, -0.1, "margen")


def test_rechaza_costo_no_positivo() -> None:
    with pytest.raises(PrecioInvalido, match="positivo"):
        calcular_precio(0, 0.1, "margen")


def test_rechaza_metodo_desconocido() -> None:
    with pytest.raises(PrecioInvalido, match="Método"):
        calcular_precio(1000000, 0.1, "promedio")  # type: ignore[arg-type]


@given(
    costo=st.integers(min_value=1, max_value=10**9),
    margen=st.floats(min_value=0.0, max_value=0.5999, allow_nan=False, allow_infinity=False),
)
def test_property_precio_no_menor_que_costo(costo: int, margen: float) -> None:
    for metodo in ("margen", "markup"):
        r = calcular_precio(costo, margen, metodo)  # type: ignore[arg-type]
        assert r.precio >= costo
        assert r.utilidad == pytest.approx(r.precio - costo, abs=0.01)
