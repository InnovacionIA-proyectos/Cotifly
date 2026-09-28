"""Backfill de `cotizaciones.linea_negocio_id` — corrección 2026-09-22.

HU-002.2 (migración 0006) agregó `linea_negocio_id` asumiendo que dependía de Nexo/HU-005
para tener datos. Corrección: el id sale de `margenes_politica`, sembrada localmente desde
la migración `0002_seed_config` — no depende de Nexo para nada. Se puede (y debe) poblar ya
mismo con un simple cruce por el mismo texto que usa hoy el flujo de negocio.

`mcp_server/server.py` (`generar_pdf`) ya se ajustó para llenar esta columna en las
cotizaciones nuevas; esta migración solo pone al día las filas que ya existían.

Revision ID: 0008_backfill_linea_negocio_id
Revises: 0007_ganada_perdida
Create Date: 2026-09-22
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

revision: str = "0008_backfill_linea_negocio_id"
down_revision: str | None = "0007_ganada_perdida"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.execute(
        """
        UPDATE cotizaciones c
        SET linea_negocio_id = mp.id
        FROM margenes_politica mp
        WHERE mp.linea_negocio = c.linea_negocio
          AND c.linea_negocio_id IS NULL
        """
    )


def downgrade() -> None:
    # Solo revierte el dato poblado por esta migración, no estructura.
    op.execute("UPDATE cotizaciones SET linea_negocio_id = NULL")
