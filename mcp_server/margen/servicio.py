"""MargenResolver: orquesta mapeo, consulta a la fuente y fallback a política — F4.

Pasos:
1. Resuelve `linea_negocio` → `TrqType` con el mapeo de BD (§7.2, semilla F1).
2. Si hay `cliente_id`, consulta la fuente (export/OData) por (sales_org, TrqType, cliente).
3. Con valor → normaliza a decimal, marca anomalía y `origen:"sap_tm"`.
4. Sin valor (o sin cliente) → fallback a `margenes_politica`, `origen:"politica_linea"`.
"""

from __future__ import annotations

from sqlalchemy.orm import Session

from mcp_server.margen.base import (
    EntradaObtenerMargen,
    FuenteMargen,
    FuenteMargenNoDisponible,
    ResultadoMargen,
    es_anomalo,
    normalizar_value,
)
from shared.config import settings
from shared.repos import MapeoTrqTypeRepo, MargenPoliticaRepo


class MargenLineaDesconocida(Exception):
    """La línea no tiene mapeo TrqType ni política de margen configurada."""


class MargenResolver:
    def __init__(self, fuente: FuenteMargen, sales_org_default: str) -> None:
        self._fuente = fuente
        self._sales_org_default = sales_org_default

    def resolver(self, session: Session, entrada: EntradaObtenerMargen) -> ResultadoMargen:
        sales_org = entrada.sales_org or self._sales_org_default
        trq_type = MapeoTrqTypeRepo.trqtype_de(session, entrada.linea_negocio, sales_org)

        # 1) Intento por SAP TM (solo si hay cliente y mapeo a TrqType).
        if entrada.cliente_id and trq_type:
            value = self._fuente.obtener_value(sales_org, trq_type, entrada.cliente_id)
            if value is not None:
                margen = normalizar_value(value)
                anomalo = es_anomalo(margen)
                detalle = (
                    f"Margen propuesto SAP TM para cliente {entrada.cliente_id} "
                    f"en {trq_type}: {value}%"
                )
                if anomalo:
                    detalle += " — ANÓMALO (fuera de [0, 60%]): validar antes de aplicar."
                return ResultadoMargen(
                    margen=margen,
                    origen="sap_tm",
                    trq_type=trq_type,
                    anomalo=anomalo,
                    detalle=detalle,
                )

        # 2) Fallback a la política por línea.
        pol = MargenPoliticaRepo.obtener(session, entrada.linea_negocio)
        if pol is None:
            raise MargenLineaDesconocida(
                f"Sin mapeo ni política para la línea '{entrada.linea_negocio}'."
            )
        margen = round(float(pol.margen), 6)
        motivo = "cliente nuevo" if not entrada.cliente_id else "sin registro en SAP TM"
        return ResultadoMargen(
            margen=margen,
            origen="politica_linea",
            trq_type=trq_type,
            anomalo=es_anomalo(margen),
            detalle=f"Política de línea {entrada.linea_negocio} ({motivo}): {margen}.",
        )


def construir_resolver() -> MargenResolver:
    """Fábrica de producción: elige la fuente según `SAP_TM_MODO`."""
    if settings.sap_tm_modo == "odata":
        from mcp_server.margen.odata_fuente import ODataFuente

        fuente: FuenteMargen = ODataFuente(
            base_url=settings.sap_tm_url,
            user=settings.sap_tm_user,
            password=settings.sap_tm_pass,
        )
    else:
        from mcp_server.margen.export_fuente import ExportJsonFuente

        fuente = ExportJsonFuente(settings.sap_tm_export_path)
    return MargenResolver(fuente, settings.sap_sales_org_default)


__all__ = [
    "FuenteMargenNoDisponible",
    "MargenLineaDesconocida",
    "MargenResolver",
    "construir_resolver",
]
