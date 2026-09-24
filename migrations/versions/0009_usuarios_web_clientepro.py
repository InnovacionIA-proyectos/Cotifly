"""Completa `usuarios_web` con las columnas de identidad de §2.6 (ClientePro/HU-004).

HU-002.2 (migración 0006) redujo `usuarios_web` a `id`/`telefono` porque en ese momento
no se ejecutó el rediseño completo de §2.6 (mapeo de `tblSAPUsuariosApp`). Decisión
2026-09-22: construir ya las columnas que faltan (`nombreUsuario`, `nombreCompleto`,
`email`), igual que se hizo con `costeo_id`/`bd_cliente`/`linea_negocio_id` -- se
agregan sin poblar todavía, hasta que ClientePro (HU-004) exista y las llene.

Revision ID: 0009_usuarios_web_clientepro
Revises: 0008_backfill_linea_negocio_id
Create Date: 2026-09-22

Nota: el id de revisión se acortó a 28 caracteres a propósito -- `alembic_version.
version_num` es `VARCHAR(32)` por defecto, y el nombre "completo" (con "_columnas")
lo excedía y hacía fallar el `UPDATE` de versión (error real encontrado al aplicar).
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "0009_usuarios_web_clientepro"
down_revision: str | None = "0008_backfill_linea_negocio_id"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column("usuarios_web", sa.Column("nombre_usuario", sa.Text(), nullable=True))
    op.add_column("usuarios_web", sa.Column("nombre_completo", sa.Text(), nullable=True))
    op.add_column("usuarios_web", sa.Column("email", sa.Text(), nullable=True))


def downgrade() -> None:
    op.drop_column("usuarios_web", "email")
    op.drop_column("usuarios_web", "nombre_completo")
    op.drop_column("usuarios_web", "nombre_usuario")
