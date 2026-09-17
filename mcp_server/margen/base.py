"""Contratos y utilidades de la capa de margen (§5.1, §7.2 · F4)."""

from __future__ import annotations

from abc import ABC, abstractmethod

from pydantic import BaseModel, Field

# Umbrales de anomalía (§7.2): fuera de [0, 60%] se marca para validación humana.
UMBRAL_MIN = 0.0
UMBRAL_MAX = 0.6


class EntradaObtenerMargen(BaseModel):
    """Entrada de `obtener_margen`."""

    linea_negocio: str
    cliente_id: str | None = None  # OrderPartyId (BP SAP); None = cliente nuevo
    sales_org: str | None = None  # default configurable (sap_sales_org_default)


class ResultadoMargen(BaseModel):
    """Salida de `obtener_margen` (contrato §5.1 + marca de anomalía)."""

    margen: float  # decimal (0.135 = 13,5%)
    origen: str  # "sap_tm" | "politica_linea"
    trq_type: str | None = None
    anomalo: bool = False
    detalle: str = ""


class FuenteMargenNoDisponible(Exception):
    """No se pudo leer la fuente de margen (export ausente, SAP caído/timeout…)."""


def normalizar_value(value: str | float) -> float:
    """Convierte el `Value` de SAP (porcentaje) a decimal: '13.5' → 0.135."""
    return round(float(value) / 100, 6)


def es_anomalo(margen: float) -> bool:
    """True si el margen decimal cae fuera de [0, 0.6] (negativo o atípico)."""
    return margen < UMBRAL_MIN or margen > UMBRAL_MAX


class FuenteMargen(ABC):
    """Strategy de origen del margen. `modo` etiqueta la fuente (export_json|odata)."""

    modo: str

    @abstractmethod
    def obtener_value(self, sales_org: str, trq_type: str, cliente_id: str) -> str | None:
        """Devuelve el `Value` (porcentaje, como viene de SAP) o None si no existe."""


class _RegistroMargen(BaseModel):
    """Fila cruda del servicio SAP (OData o export), útil para índice/parseo."""

    sales_org: str = Field(alias="SalesOrgId")
    trq_type: str = Field(alias="TrqType")
    order_party_id: str = Field(alias="OrderPartyId")
    value: str = Field(alias="Value")

    model_config = {"populate_by_name": True}
