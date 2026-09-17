"""ExportJsonFuente: lee el export JSON de SAP TM (mismo esquema OData) — F4.

Es la fuente activa mientras no haya conectividad al host SAP. El archivo tiene
la forma `{"d": {"results": [ {SalesOrgId, TrqType, OrderPartyId, Value}, ... ]}}`.
Se indexa una sola vez por (SalesOrgId, TrqType, OrderPartyId) para búsquedas O(1).
"""

from __future__ import annotations

import json
from pathlib import Path

from mcp_server.margen.base import FuenteMargen, FuenteMargenNoDisponible, _RegistroMargen


class ExportJsonFuente(FuenteMargen):
    modo = "export_json"

    def __init__(self, ruta: str | Path) -> None:
        self._ruta = Path(ruta)
        self._indice: dict[tuple[str, str, str], str] | None = None

    def _cargar(self) -> dict[tuple[str, str, str], str]:
        if self._indice is not None:
            return self._indice
        if not self._ruta.exists():
            raise FuenteMargenNoDisponible(f"No existe el export SAP en {self._ruta}.")
        try:
            datos = json.loads(self._ruta.read_text(encoding="utf-8"))
            resultados = datos["d"]["results"]
        except (ValueError, KeyError, TypeError) as e:
            raise FuenteMargenNoDisponible(f"Export SAP ilegible: {e}") from e

        indice: dict[tuple[str, str, str], str] = {}
        for cruda in resultados:
            reg = _RegistroMargen.model_validate(cruda)
            indice[(reg.sales_org, reg.trq_type, reg.order_party_id)] = reg.value
        self._indice = indice
        return indice

    def obtener_value(self, sales_org: str, trq_type: str, cliente_id: str) -> str | None:
        return self._cargar().get((sales_org, trq_type, cliente_id))
