"""Vista de trazabilidad para Gerencia Comercial (§9 · F15).

`v_ajustes_margen_por_comercial`: agrega los ajustes de margen por comercial y
línea de negocio, para que Gerencia audite cuánto se desvía cada ejecutivo de la
política. Es una vista (solo lectura) sobre `ajustes_margen` ⋈ `cotizaciones`.

Revision ID: 0003_vista_trazabilidad
Revises: 0002_seed_config
Create Date: 2026-07-17
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

revision: str = "0003_vista_trazabilidad"
down_revision: str | None = "0002_seed_config"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_VISTA = "v_ajustes_margen_por_comercial"

_CREATE = f"""
CREATE VIEW {_VISTA} AS
SELECT
    a.comercial_email,
    c.linea_negocio,
    COUNT(*)              AS num_ajustes,
    AVG(a.diferencia)     AS diferencia_promedio,
    MIN(a.diferencia)     AS diferencia_min,
    MAX(a.diferencia)     AS diferencia_max,
    MAX(a.created_at)     AS ultimo_ajuste
FROM ajustes_margen a
LEFT JOIN cotizaciones c ON c.id = a.cotizacion_id
GROUP BY a.comercial_email, c.linea_negocio;
"""


def upgrade() -> None:
    op.execute(_CREATE)


def downgrade() -> None:
    op.execute(f"DROP VIEW IF EXISTS {_VISTA};")
