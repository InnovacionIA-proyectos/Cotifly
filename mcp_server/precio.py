"""Cálculo determinista de precio (§5.1 · F5). El LLM **no** hace la aritmética.

Dos métodos:
- `margen`: `precio = costo / (1 - m)` (margen sobre el precio de venta).
- `markup`: `precio = costo * (1 + m)` (recargo sobre el costo).

Función pura, sin dependencias externas. Los importes se redondean a pesos
enteros (COP no maneja centavos). Rechaza entradas sin sentido de negocio
(costo ≤ 0, margen negativo, o `margen` con `m ≥ 1` → división por cero).
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel

Metodo = Literal["margen", "markup"]


class PrecioInvalido(Exception):
    """Entrada que no permite un cálculo de precio válido."""


class EntradaCalcularPrecio(BaseModel):
    costo: float
    margen: float
    metodo: Metodo = "margen"


class ResultadoPrecio(BaseModel):
    precio: float
    costo: float
    margen: float
    metodo: Metodo
    utilidad: float


def calcular_precio(costo: float, margen: float, metodo: Metodo = "margen") -> ResultadoPrecio:
    """Devuelve precio y utilidad redondeados a pesos. Lanza `PrecioInvalido` si no aplica."""
    if costo <= 0:
        raise PrecioInvalido(f"El costo debe ser positivo (recibido: {costo}).")
    if margen < 0:
        raise PrecioInvalido(f"El margen no puede ser negativo (recibido: {margen}).")
    if metodo not in ("margen", "markup"):
        raise PrecioInvalido(f"Método desconocido: '{metodo}'. Use 'margen' o 'markup'.")

    if metodo == "margen":
        if margen >= 1:
            raise PrecioInvalido(
                f"Con método 'margen' el margen debe ser < 1 (recibido: {margen})."
            )
        bruto = costo / (1 - margen)
    else:  # markup
        bruto = costo * (1 + margen)

    precio = float(round(bruto))
    return ResultadoPrecio(
        precio=precio,
        costo=costo,
        margen=margen,
        metodo=metodo,
        utilidad=round(precio - costo, 2),
    )
