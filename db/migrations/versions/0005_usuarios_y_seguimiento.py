"""Usuarios del front web + seguimiento de efectividad de cotizaciones.

- `usuarios_web`: comercial que usa el front (login por correo corporativo).
- `cotizaciones`: columnas de seguimiento (`efectiva`, `seguimiento_nota`,
  `seguimiento_actualizado`) para marcar si la cotización fue efectiva y dar
  trazabilidad comercial por usuario.

Revision ID: 0005_usuarios_y_seguimiento
Revises: 0004_trazabilidad
Create Date: 2026-07-22
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "0005_usuarios_y_seguimiento"
down_revision: str | None = "0004_trazabilidad"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "usuarios_web",
        sa.Column("email", sa.Text(), primary_key=True),
        sa.Column("nombre", sa.Text(), nullable=True),
        sa.Column("created_at", sa.TIMESTAMP(timezone=True), server_default=sa.func.now()),
        sa.Column("ultima_sesion", sa.TIMESTAMP(timezone=True), nullable=True),
    )
    op.add_column("cotizaciones", sa.Column("efectiva", sa.Boolean(), nullable=True))
    op.add_column("cotizaciones", sa.Column("seguimiento_nota", sa.Text(), nullable=True))
    op.add_column(
        "cotizaciones",
        sa.Column("seguimiento_actualizado", sa.TIMESTAMP(timezone=True), nullable=True),
    )


def downgrade() -> None:
    op.drop_column("cotizaciones", "seguimiento_actualizado")
    op.drop_column("cotizaciones", "seguimiento_nota")
    op.drop_column("cotizaciones", "efectiva")
    op.drop_table("usuarios_web")
