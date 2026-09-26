"""Completa el rediseño de §2.1/§2.8 de la propuesta: `efectiva`/`seguimiento_nota`/
`seguimiento_actualizado` -> `ganada`/`motivo_rechazo`/`marcado_at`.

Esquema únicamente (decisión 2026-09-22, mismo patrón de HU-002.2: se construye ya
aunque el consumidor -- el botón ganada/perdida y la llamada a `POST /api/cotizaciones/
{id}/resultado` de Nexo -- sea de HU-005 y todavía no exista). `ganada` mantiene la
semántica boolean de `efectiva` (True/False/None), solo cambia el nombre y su origen
de negocio (antes: seguimiento manual del comercial; ahora: resultado sincronizado
con Nexo).

Revision ID: 0007_ganada_perdida
Revises: 0006_hu002_2_esquema_completo
Create Date: 2026-09-22
"""

from __future__ import annotations

from collections.abc import Sequence

from alembic import op

revision: str = "0007_ganada_perdida"
down_revision: str | None = "0006_hu002_2_esquema_completo"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.alter_column("cotizaciones", "efectiva", new_column_name="ganada")
    op.alter_column("cotizaciones", "seguimiento_nota", new_column_name="motivo_rechazo")
    op.alter_column("cotizaciones", "seguimiento_actualizado", new_column_name="marcado_at")


def downgrade() -> None:
    op.alter_column("cotizaciones", "marcado_at", new_column_name="seguimiento_actualizado")
    op.alter_column("cotizaciones", "motivo_rechazo", new_column_name="seguimiento_nota")
    op.alter_column("cotizaciones", "ganada", new_column_name="efectiva")
