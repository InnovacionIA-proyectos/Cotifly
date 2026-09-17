"""Modelos ORM de la BD de trazabilidad (§7.3 de la arquitectura).

Seis tablas de trazabilidad + dos de configuración/semilla:
- margenes_politica: fallback de margen por línea (cuando SAP no tiene el cliente).
- mapeo_trqtype:     mapeo TrqType ↔ línea de negocio (§7.2), ajustable por negocio.

Tipos específicos de PostgreSQL (UUID, JSONB, ARRAY, timestamptz) a propósito:
la BD de prueba es Postgres real, no SQLite.
"""

from __future__ import annotations

import uuid
from datetime import datetime

from sqlalchemy import (
    Boolean,
    ForeignKey,
    Integer,
    Numeric,
    String,
    Text,
    UniqueConstraint,
    func,
    text,
)
from sqlalchemy.dialects.postgresql import ARRAY, JSONB, TIMESTAMP, UUID
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column


class Base(DeclarativeBase):
    pass


def _uuid_pk() -> Mapped[uuid.UUID]:
    return mapped_column(
        UUID(as_uuid=True),
        primary_key=True,
        server_default=text("gen_random_uuid()"),
    )


class Cotizacion(Base):
    __tablename__ = "cotizaciones"

    id: Mapped[uuid.UUID] = _uuid_pk()
    folio: Mapped[str] = mapped_column(Text, unique=True)  # PCN-DDMMAAAA(-v2)
    linea_negocio: Mapped[str | None] = mapped_column(Text)
    cliente_nombre: Mapped[str | None] = mapped_column(Text)
    cliente_id_sap: Mapped[str | None] = mapped_column(Text)
    origen: Mapped[str | None] = mapped_column(Text)
    destino: Mapped[str | None] = mapped_column(Text)
    costo: Mapped[float | None] = mapped_column(Numeric)
    margen_politica: Mapped[float | None] = mapped_column(Numeric)
    margen_aplicado: Mapped[float | None] = mapped_column(Numeric)
    metodo_calculo: Mapped[str | None] = mapped_column(Text)
    precio_final: Mapped[float | None] = mapped_column(Numeric)
    moneda: Mapped[str | None] = mapped_column(Text, default="COP")
    fuente_tarifa: Mapped[str | None] = mapped_column(Text)  # drive_flat_files|api_fletes|manual
    origen_margen: Mapped[str | None] = mapped_column(Text)  # sap_tm|politica_linea
    payload: Mapped[dict | None] = mapped_column(JSONB)
    pdf_path: Mapped[str | None] = mapped_column(Text)
    estado: Mapped[str | None] = mapped_column(Text)  # generada|enviada|reenviada
    comercial_email: Mapped[str | None] = mapped_column(Text)
    space_id: Mapped[str | None] = mapped_column(Text)
    thread_id: Mapped[str | None] = mapped_column(Text)
    # Seguimiento comercial (front web): ¿la cotización fue efectiva? + nota.
    efectiva: Mapped[bool | None] = mapped_column(Boolean)  # None=sin marcar
    seguimiento_nota: Mapped[str | None] = mapped_column(Text)
    seguimiento_actualizado: Mapped[datetime | None] = mapped_column(TIMESTAMP(timezone=True))
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )
    updated_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class AjusteMargen(Base):
    __tablename__ = "ajustes_margen"

    id: Mapped[uuid.UUID] = _uuid_pk()
    cotizacion_id: Mapped[uuid.UUID | None] = mapped_column(
        UUID(as_uuid=True), ForeignKey("cotizaciones.id")
    )
    folio: Mapped[str | None] = mapped_column(Text)
    margen_politica: Mapped[float | None] = mapped_column(Numeric)
    margen_aplicado: Mapped[float | None] = mapped_column(Numeric)
    diferencia: Mapped[float | None] = mapped_column(Numeric)
    motivo: Mapped[str | None] = mapped_column(Text)
    comercial_email: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )


class CorreoEnviado(Base):
    __tablename__ = "correos_enviados"

    id: Mapped[uuid.UUID] = _uuid_pk()
    cotizacion_id: Mapped[uuid.UUID] = mapped_column(
        UUID(as_uuid=True), ForeignKey("cotizaciones.id")
    )
    para: Mapped[list[str]] = mapped_column(ARRAY(Text))
    cc: Mapped[list[str] | None] = mapped_column(ARRAY(Text))
    asunto: Mapped[str | None] = mapped_column(Text)
    message_id: Mapped[str | None] = mapped_column(Text)
    confirmado_por: Mapped[str | None] = mapped_column(Text)
    estado: Mapped[str | None] = mapped_column(Text)  # enviado|fallido
    error: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )


class EventoConversacion(Base):
    __tablename__ = "eventos_conversacion"

    id: Mapped[uuid.UUID] = _uuid_pk()
    space_id: Mapped[str | None] = mapped_column(Text)
    thread_id: Mapped[str | None] = mapped_column(Text)
    usuario_email: Mapped[str | None] = mapped_column(Text)
    rol: Mapped[str] = mapped_column(Text)  # user|assistant
    contenido: Mapped[str | None] = mapped_column(Text)
    herramientas: Mapped[dict | None] = mapped_column(JSONB)
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )


class EventoHerramienta(Base):
    __tablename__ = "eventos_herramienta"

    id: Mapped[uuid.UUID] = _uuid_pk()
    herramienta: Mapped[str] = mapped_column(Text)
    input: Mapped[dict | None] = mapped_column(JSONB)
    output_resumen: Mapped[dict | None] = mapped_column(JSONB)
    exito: Mapped[bool | None] = mapped_column(Boolean)
    duracion_ms: Mapped[int | None] = mapped_column(Integer)
    thread_id: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )


class Sesion(Base):
    __tablename__ = "sesiones"

    space_id: Mapped[str] = mapped_column(Text, primary_key=True)
    thread_id: Mapped[str] = mapped_column(Text, primary_key=True)
    estado: Mapped[str | None] = mapped_column(Text)  # activa|archivada
    contexto: Mapped[dict | None] = mapped_column(JSONB)
    ultima_actividad: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )


class UsuarioWeb(Base):
    """Comercial que usa el front web. Identifica sesiones y cotizaciones por correo."""

    __tablename__ = "usuarios_web"

    email: Mapped[str] = mapped_column(Text, primary_key=True)
    nombre: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now()
    )
    ultima_sesion: Mapped[datetime | None] = mapped_column(TIMESTAMP(timezone=True))


class MargenPolitica(Base):
    """Fallback de margen por línea cuando SAP TM no tiene registro del cliente."""

    __tablename__ = "margenes_politica"

    linea_negocio: Mapped[str] = mapped_column(String(40), primary_key=True)
    margen: Mapped[float] = mapped_column(Numeric)  # decimal (0.135 = 13,5%)
    metodo_calculo: Mapped[str] = mapped_column(Text, default="margen")
    updated_at: Mapped[datetime] = mapped_column(
        TIMESTAMP(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class MapeoTrqType(Base):
    """Mapeo TrqType ↔ línea de negocio (§7.2). Configurable por negocio."""

    __tablename__ = "mapeo_trqtype"
    __table_args__ = (UniqueConstraint("trq_type", "sales_org", name="uq_trqtype_salesorg"),)

    trq_type: Mapped[str] = mapped_column(String(8), primary_key=True)
    linea_negocio: Mapped[str] = mapped_column(String(40))
    sales_org: Mapped[str | None] = mapped_column(String(12), primary_key=True, default="")
    activo: Mapped[bool] = mapped_column(Boolean, default=True)
