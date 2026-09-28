"""Semilla de configuración: mapeo TrqType↔línea (§7.2) y política de margen.

Revision ID: 0002_seed_config
Revises: 0001_initial
Create Date: 2026-07-16

NOTA DE NEGOCIO: el mapeo y los márgenes de política son valores PROVISIONALES
pendientes de validación con Gerencia Comercial (bloqueos #1 y #2 del plan).
Viven en tablas (no en código) para ajustarse sin desplegar.
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "0002_seed_config"
down_revision: str | None = "0001_initial"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

# §7.2 — mapeo confirmado/por confirmar con negocio. sales_org='' aplica a todas.
_MAPEO = [
    {"trq_type": "OPTN", "sales_org": "", "linea_negocio": "Nacional", "activo": True},
    {"trq_type": "OPDS", "sales_org": "", "linea_negocio": "Distribucion", "activo": True},
    {"trq_type": "OPIM", "sales_org": "", "linea_negocio": "Importacion", "activo": True},
    {"trq_type": "OPEX", "sales_org": "", "linea_negocio": "Exportacion", "activo": True},
    {"trq_type": "OPTI", "sales_org": "", "linea_negocio": "Internacional", "activo": True},
    {"trq_type": "OHUB", "sales_org": "", "linea_negocio": "Almacenamiento", "activo": True},
    {"trq_type": "OH3P", "sales_org": "", "linea_negocio": "Almacenamiento3PL", "activo": True},
    {"trq_type": "OPSC", "sales_org": "", "linea_negocio": "ITR", "activo": True},
]

# Margen de política por línea (decimal). PROVISIONAL — validar con negocio.
_POLITICA = [
    {"linea_negocio": "Nacional", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Distribucion", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Importacion", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Exportacion", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Internacional", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Almacenamiento", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "Almacenamiento3PL", "margen": 0.15, "metodo_calculo": "margen"},
    {"linea_negocio": "ITR", "margen": 0.15, "metodo_calculo": "margen"},
]


def upgrade() -> None:
    mapeo = sa.table(
        "mapeo_trqtype",
        sa.column("trq_type", sa.String),
        sa.column("sales_org", sa.String),
        sa.column("linea_negocio", sa.String),
        sa.column("activo", sa.Boolean),
    )
    politica = sa.table(
        "margenes_politica",
        sa.column("linea_negocio", sa.String),
        sa.column("margen", sa.Numeric),
        sa.column("metodo_calculo", sa.Text),
    )
    op.bulk_insert(mapeo, _MAPEO)
    op.bulk_insert(politica, _POLITICA)


def downgrade() -> None:
    op.execute("DELETE FROM mapeo_trqtype")
    op.execute("DELETE FROM margenes_politica")
