"""Vistas de trazabilidad completa (§7.3 · captura de usuario y acciones).

Sobre el mismo PostgreSQL de trazabilidad, dos vistas de solo lectura para
Gerencia Comercial y soporte:

- `v_trazabilidad_cotizaciones`: una fila por cotización con su precio/margen,
  el comercial que la generó, cuántos ajustes tuvo y cuántos correos se enviaron.
- `v_actividad_por_comercial`: actividad agregada por comercial (cotizaciones,
  ajustes, correos y última actividad) — quién usa el bot y cuánto.

La vista `v_ajustes_margen_por_comercial` (migración 0003) se conserva.

Revision ID: 0004_trazabilidad
Revises: 0003_vista_trazabilidad
Create Date: 2026-07-21
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

revision: str = "0004_trazabilidad"
down_revision: str | None = "0003_vista_trazabilidad"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_V_COTIZ = "v_trazabilidad_cotizaciones"
_V_ACTIV = "v_actividad_por_comercial"

_CREATE_COTIZ = f"""
CREATE VIEW {_V_COTIZ} AS
SELECT
    c.id                       AS cotizacion_id,
    c.folio,
    c.created_at,
    c.estado,
    c.comercial_email,
    c.space_id,
    c.thread_id,
    c.linea_negocio,
    c.cliente_nombre,
    c.cliente_id_sap,
    c.origen,
    c.destino,
    c.costo,
    c.margen_politica,
    c.margen_aplicado,
    c.precio_final,
    c.moneda,
    c.fuente_tarifa,
    c.origen_margen,
    COALESCE(aj.num_ajustes, 0)        AS num_ajustes,
    COALESCE(co.num_correos, 0)        AS num_correos,
    COALESCE(co.num_correos_ok, 0)     AS num_correos_enviados,
    co.ultimo_envio
FROM cotizaciones c
LEFT JOIN (
    SELECT cotizacion_id, COUNT(*) AS num_ajustes
    FROM ajustes_margen
    WHERE cotizacion_id IS NOT NULL
    GROUP BY cotizacion_id
) aj ON aj.cotizacion_id = c.id
LEFT JOIN (
    SELECT
        cotizacion_id,
        COUNT(*)                                       AS num_correos,
        COUNT(*) FILTER (WHERE estado = 'enviado')     AS num_correos_ok,
        MAX(created_at)                                AS ultimo_envio
    FROM correos_enviados
    GROUP BY cotizacion_id
) co ON co.cotizacion_id = c.id;
"""

_CREATE_ACTIV = f"""
CREATE VIEW {_V_ACTIV} AS
SELECT
    c.comercial_email,
    COUNT(DISTINCT c.id)                                   AS num_cotizaciones,
    COUNT(DISTINCT c.id) FILTER (WHERE c.estado = 'enviada') AS num_cotizaciones_enviadas,
    COALESCE(SUM(sub.num_ajustes), 0)                      AS num_ajustes,
    MAX(c.created_at)                                      AS ultima_cotizacion
FROM cotizaciones c
LEFT JOIN (
    SELECT cotizacion_id, COUNT(*) AS num_ajustes
    FROM ajustes_margen
    WHERE cotizacion_id IS NOT NULL
    GROUP BY cotizacion_id
) sub ON sub.cotizacion_id = c.id
WHERE c.comercial_email IS NOT NULL
GROUP BY c.comercial_email;
"""


def upgrade() -> None:
    op.execute(_CREATE_COTIZ)
    op.execute(_CREATE_ACTIV)


def downgrade() -> None:
    op.execute(f"DROP VIEW IF EXISTS {_V_ACTIV};")
    op.execute(f"DROP VIEW IF EXISTS {_V_COTIZ};")
