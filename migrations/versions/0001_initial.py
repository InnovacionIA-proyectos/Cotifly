"""Esquema inicial de trazabilidad (§7.3) + tablas de configuración.

Revision ID: 0001_initial
Revises:
Create Date: 2026-07-16
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0001_initial"
down_revision: str | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_UUID_PK = sa.text("gen_random_uuid()")
_NOW = sa.text("now()")


def upgrade() -> None:
    op.create_table(
        "cotizaciones",
        sa.Column("id", postgresql.UUID(as_uuid=True), server_default=_UUID_PK, primary_key=True),
        sa.Column("folio", sa.Text(), nullable=False),
        sa.Column("linea_negocio", sa.Text()),
        sa.Column("cliente_nombre", sa.Text()),
        sa.Column("cliente_id_sap", sa.Text()),
        sa.Column("origen", sa.Text()),
        sa.Column("destino", sa.Text()),
        sa.Column("costo", sa.Numeric()),
        sa.Column("margen_politica", sa.Numeric()),
        sa.Column("margen_aplicado", sa.Numeric()),
        sa.Column("metodo_calculo", sa.Text()),
        sa.Column("precio_final", sa.Numeric()),
        sa.Column("moneda", sa.Text()),
        sa.Column("fuente_tarifa", sa.Text()),
        sa.Column("origen_margen", sa.Text()),
        sa.Column("payload", postgresql.JSONB()),
        sa.Column("pdf_path", sa.Text()),
        sa.Column("estado", sa.Text()),
        sa.Column("comercial_email", sa.Text()),
        sa.Column("space_id", sa.Text()),
        sa.Column("thread_id", sa.Text()),
        sa.Column("created_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
        sa.Column("updated_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
        sa.UniqueConstraint("folio", name="uq_cotizaciones_folio"),
    )

    op.create_table(
        "ajustes_margen",
        sa.Column("id", postgresql.UUID(as_uuid=True), server_default=_UUID_PK, primary_key=True),
        sa.Column("cotizacion_id", postgresql.UUID(as_uuid=True)),
        sa.Column("folio", sa.Text()),
        sa.Column("margen_politica", sa.Numeric()),
        sa.Column("margen_aplicado", sa.Numeric()),
        sa.Column("diferencia", sa.Numeric()),
        sa.Column("motivo", sa.Text()),
        sa.Column("comercial_email", sa.Text()),
        sa.Column("created_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
        sa.ForeignKeyConstraint(["cotizacion_id"], ["cotizaciones.id"]),
    )

    op.create_table(
        "correos_enviados",
        sa.Column("id", postgresql.UUID(as_uuid=True), server_default=_UUID_PK, primary_key=True),
        sa.Column("cotizacion_id", postgresql.UUID(as_uuid=True), nullable=False),
        sa.Column("para", postgresql.ARRAY(sa.Text()), nullable=False),
        sa.Column("cc", postgresql.ARRAY(sa.Text())),
        sa.Column("asunto", sa.Text()),
        sa.Column("message_id", sa.Text()),
        sa.Column("confirmado_por", sa.Text()),
        sa.Column("estado", sa.Text()),
        sa.Column("error", sa.Text()),
        sa.Column("created_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
        sa.ForeignKeyConstraint(["cotizacion_id"], ["cotizaciones.id"]),
    )

    op.create_table(
        "eventos_conversacion",
        sa.Column("id", postgresql.UUID(as_uuid=True), server_default=_UUID_PK, primary_key=True),
        sa.Column("space_id", sa.Text()),
        sa.Column("thread_id", sa.Text()),
        sa.Column("usuario_email", sa.Text()),
        sa.Column("rol", sa.Text(), nullable=False),
        sa.Column("contenido", sa.Text()),
        sa.Column("herramientas", postgresql.JSONB()),
        sa.Column("created_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
    )

    op.create_table(
        "eventos_herramienta",
        sa.Column("id", postgresql.UUID(as_uuid=True), server_default=_UUID_PK, primary_key=True),
        sa.Column("herramienta", sa.Text(), nullable=False),
        sa.Column("input", postgresql.JSONB()),
        sa.Column("output_resumen", postgresql.JSONB()),
        sa.Column("exito", sa.Boolean()),
        sa.Column("duracion_ms", sa.Integer()),
        sa.Column("thread_id", sa.Text()),
        sa.Column("created_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
    )

    op.create_table(
        "sesiones",
        sa.Column("space_id", sa.Text(), primary_key=True),
        sa.Column("thread_id", sa.Text(), primary_key=True),
        sa.Column("estado", sa.Text()),
        sa.Column("contexto", postgresql.JSONB()),
        sa.Column("ultima_actividad", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
    )

    op.create_table(
        "margenes_politica",
        sa.Column("linea_negocio", sa.String(40), primary_key=True),
        sa.Column("margen", sa.Numeric(), nullable=False),
        sa.Column("metodo_calculo", sa.Text()),
        sa.Column("updated_at", postgresql.TIMESTAMP(timezone=True), server_default=_NOW),
    )

    op.create_table(
        "mapeo_trqtype",
        sa.Column("trq_type", sa.String(8), primary_key=True),
        sa.Column("sales_org", sa.String(12), primary_key=True),
        sa.Column("linea_negocio", sa.String(40), nullable=False),
        sa.Column("activo", sa.Boolean(), server_default=sa.text("true")),
    )

    # Índices de consulta frecuente (trazabilidad y sesiones).
    op.create_index("ix_cotizaciones_cliente", "cotizaciones", ["cliente_nombre"])
    op.create_index("ix_cotizaciones_comercial", "cotizaciones", ["comercial_email"])
    op.create_index("ix_eventos_conv_thread", "eventos_conversacion", ["space_id", "thread_id"])
    op.create_index("ix_eventos_herr_thread", "eventos_herramienta", ["thread_id"])


def downgrade() -> None:
    op.drop_index("ix_eventos_herr_thread", table_name="eventos_herramienta")
    op.drop_index("ix_eventos_conv_thread", table_name="eventos_conversacion")
    op.drop_index("ix_cotizaciones_comercial", table_name="cotizaciones")
    op.drop_index("ix_cotizaciones_cliente", table_name="cotizaciones")
    op.drop_table("mapeo_trqtype")
    op.drop_table("margenes_politica")
    op.drop_table("sesiones")
    op.drop_table("eventos_herramienta")
    op.drop_table("eventos_conversacion")
    op.drop_table("correos_enviados")
    op.drop_table("ajustes_margen")
    op.drop_table("cotizaciones")
