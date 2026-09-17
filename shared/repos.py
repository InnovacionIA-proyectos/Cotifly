"""Repositorios de acceso a datos (SQLAlchemy 2.x).

Encapsulan las consultas que usan el MCP Server (trazabilidad, política de
margen, mapeo TrqType) y el Orquestador (sesiones, conversación). Reciben la
`Session` para que la transacción la controle quien llama (ver shared.db).
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from shared.models import (
    AjusteMargen,
    CorreoEnviado,
    Cotizacion,
    EventoConversacion,
    EventoHerramienta,
    MapeoTrqType,
    MargenPolitica,
    Sesion,
    UsuarioWeb,
)


class CotizacionRepo:
    @staticmethod
    def crear(session: Session, **datos: Any) -> Cotizacion:
        cot = Cotizacion(**datos)
        session.add(cot)
        session.flush()  # asigna id/folio sin cerrar la transacción
        return cot

    @staticmethod
    def obtener_por_folio(session: Session, folio: str) -> Cotizacion | None:
        return session.scalar(select(Cotizacion).where(Cotizacion.folio == folio))

    @staticmethod
    def folio_versionado(session: Session, base: str) -> str:
        """Devuelve `base` si está libre; si no, la primera variante `-vN` disponible."""
        if CotizacionRepo.obtener_por_folio(session, base) is None:
            return base
        version = 2
        while CotizacionRepo.obtener_por_folio(session, f"{base}-v{version}") is not None:
            version += 1
        return f"{base}-v{version}"

    @staticmethod
    def buscar(
        session: Session,
        *,
        cliente: str | None = None,
        comercial: str | None = None,
        folio: str | None = None,
    ) -> list[Cotizacion]:
        stmt = select(Cotizacion)
        if folio:
            stmt = stmt.where(Cotizacion.folio == folio)
        if cliente:
            stmt = stmt.where(Cotizacion.cliente_nombre.ilike(f"%{cliente}%"))
        if comercial:
            stmt = stmt.where(Cotizacion.comercial_email == comercial)
        stmt = stmt.order_by(Cotizacion.created_at.desc())
        return list(session.scalars(stmt).all())

    @staticmethod
    def por_comercial(session: Session, comercial: str, limite: int = 100) -> list[Cotizacion]:
        """Cotizaciones de un comercial, de la más reciente a la más antigua."""
        stmt = (
            select(Cotizacion)
            .where(Cotizacion.comercial_email == comercial)
            .order_by(Cotizacion.created_at.desc())
            .limit(limite)
        )
        return list(session.scalars(stmt).all())

    @staticmethod
    def marcar_seguimiento(
        session: Session,
        folio: str,
        comercial: str,
        efectiva: bool | None,
        nota: str | None,
        cuando: datetime | None = None,
    ) -> Cotizacion | None:
        """Marca efectividad/nota de la cotización del comercial. None si no es suya."""
        cot = session.scalar(
            select(Cotizacion).where(
                Cotizacion.folio == folio, Cotizacion.comercial_email == comercial
            )
        )
        if cot is None:
            return None
        cot.efectiva = efectiva
        cot.seguimiento_nota = nota
        cot.seguimiento_actualizado = cuando or datetime.now(UTC)
        session.flush()
        return cot


class AjusteMargenRepo:
    @staticmethod
    def registrar(
        session: Session,
        *,
        margen_politica: float,
        margen_aplicado: float,
        motivo: str,
        comercial_email: str,
        cotizacion_id: Any | None = None,
        folio: str | None = None,
    ) -> AjusteMargen:
        aj = AjusteMargen(
            cotizacion_id=cotizacion_id,
            folio=folio,
            margen_politica=margen_politica,
            margen_aplicado=margen_aplicado,
            diferencia=round(margen_aplicado - margen_politica, 6),
            motivo=motivo,
            comercial_email=comercial_email,
        )
        session.add(aj)
        session.flush()
        return aj


class CorreoRepo:
    @staticmethod
    def registrar(
        session: Session,
        *,
        cotizacion_id: Any,
        para: list[str],
        asunto: str | None,
        confirmado_por: str,
        estado: str,
        cc: list[str] | None = None,
        message_id: str | None = None,
        error: str | None = None,
    ) -> CorreoEnviado:
        correo = CorreoEnviado(
            cotizacion_id=cotizacion_id,
            para=para,
            cc=cc,
            asunto=asunto,
            message_id=message_id,
            confirmado_por=confirmado_por,
            estado=estado,
            error=error,
        )
        session.add(correo)
        session.flush()
        return correo

    @staticmethod
    def duplicado_reciente(
        session: Session,
        cotizacion_id: Any,
        para: list[str],
        ventana_min: int = 5,
    ) -> bool:
        """True si ya hay un envío OK a los mismos destinatarios dentro de la ventana."""
        # now() es estable dentro de la transacción → sin depender del reloj del cliente.
        stmt = select(CorreoEnviado).where(
            CorreoEnviado.cotizacion_id == cotizacion_id,
            CorreoEnviado.estado == "enviado",
            CorreoEnviado.created_at
            >= func.now() - func.make_interval(0, 0, 0, 0, 0, ventana_min),
        )
        objetivo = set(para)
        return any(set(fila.para) == objetivo for fila in session.scalars(stmt))


class AuditoriaRepo:
    @staticmethod
    def registrar(
        session: Session,
        *,
        herramienta: str,
        input: dict | None,
        output_resumen: dict | None,
        exito: bool,
        duracion_ms: int,
        thread_id: str | None = None,
    ) -> EventoHerramienta:
        ev = EventoHerramienta(
            herramienta=herramienta,
            input=input,
            output_resumen=output_resumen,
            exito=exito,
            duracion_ms=duracion_ms,
            thread_id=thread_id,
        )
        session.add(ev)
        session.flush()
        return ev


class ConversacionRepo:
    @staticmethod
    def registrar_turno(
        session: Session,
        *,
        space_id: str,
        thread_id: str,
        rol: str,
        contenido: str | None,
        usuario_email: str | None = None,
        herramientas: dict | None = None,
        cuando: datetime | None = None,
    ) -> EventoConversacion:
        # `cuando` fija created_at: da orden cronológico fiable (el PK es UUID, no
        # ordenable, y now() es constante dentro de una misma transacción).
        ev = EventoConversacion(
            space_id=space_id,
            thread_id=thread_id,
            usuario_email=usuario_email,
            rol=rol,
            contenido=contenido,
            herramientas=herramientas,
        )
        if cuando is not None:
            ev.created_at = cuando
        session.add(ev)
        session.flush()
        return ev

    @staticmethod
    def historial(
        session: Session, space_id: str, thread_id: str
    ) -> list[EventoConversacion]:
        """Turnos del hilo en orden cronológico (el primero es la solicitud original)."""
        stmt = (
            select(EventoConversacion)
            .where(
                EventoConversacion.space_id == space_id,
                EventoConversacion.thread_id == thread_id,
            )
            .order_by(EventoConversacion.created_at.asc(), EventoConversacion.id.asc())
        )
        return list(session.scalars(stmt).all())


class SesionRepo:
    @staticmethod
    def obtener_o_crear(session: Session, space_id: str, thread_id: str) -> Sesion:
        ses = session.get(Sesion, (space_id, thread_id))
        if ses is None:
            ses = Sesion(space_id=space_id, thread_id=thread_id, estado="activa", contexto={})
            session.add(ses)
            session.flush()
        return ses

    @staticmethod
    def tocar(
        session: Session, space_id: str, thread_id: str, cuando: datetime | None = None
    ) -> None:
        ses = SesionRepo.obtener_o_crear(session, space_id, thread_id)
        ses.ultima_actividad = cuando or datetime.now(UTC)
        session.flush()

    @staticmethod
    def archivar(session: Session, space_id: str, thread_id: str) -> None:
        ses = session.get(Sesion, (space_id, thread_id))
        if ses is not None:
            ses.estado = "archivada"
            session.flush()

    @staticmethod
    def inactivas(session: Session, umbral: datetime) -> list[Sesion]:
        """Sesiones aún activas cuya última actividad es anterior al umbral."""
        stmt = select(Sesion).where(
            Sesion.estado == "activa", Sesion.ultima_actividad < umbral
        )
        return list(session.scalars(stmt).all())

    @staticmethod
    def por_space(session: Session, space_id: str) -> list[Sesion]:
        """Sesiones de un space, de la más reciente a la más antigua (soporte §4.2)."""
        stmt = (
            select(Sesion)
            .where(Sesion.space_id == space_id)
            .order_by(Sesion.ultima_actividad.desc())
        )
        return list(session.scalars(stmt).all())


class UsuarioWebRepo:
    @staticmethod
    def upsert(session: Session, email: str, nombre: str | None) -> UsuarioWeb:
        """Crea o actualiza el comercial y marca su última sesión."""
        u = session.get(UsuarioWeb, email)
        ahora = datetime.now(UTC)
        if u is None:
            u = UsuarioWeb(email=email, nombre=nombre, ultima_sesion=ahora)
            session.add(u)
        else:
            if nombre:
                u.nombre = nombre
            u.ultima_sesion = ahora
        session.flush()
        return u


class MargenPoliticaRepo:
    @staticmethod
    def obtener(session: Session, linea_negocio: str) -> MargenPolitica | None:
        return session.get(MargenPolitica, linea_negocio)


class MapeoTrqTypeRepo:
    @staticmethod
    def linea_de(session: Session, trq_type: str, sales_org: str = "") -> str | None:
        # Preferir coincidencia específica por sales_org; si no, el comodín ('').
        stmt = (
            select(MapeoTrqType)
            .where(
                MapeoTrqType.trq_type == trq_type,
                MapeoTrqType.sales_org.in_([sales_org, ""]),
                MapeoTrqType.activo.is_(True),
            )
            .order_by(MapeoTrqType.sales_org.desc())
        )
        fila = session.scalars(stmt).first()
        return fila.linea_negocio if fila else None

    @staticmethod
    def trqtype_de(session: Session, linea_negocio: str, sales_org: str = "") -> str | None:
        stmt = select(MapeoTrqType).where(
            MapeoTrqType.linea_negocio == linea_negocio,
            MapeoTrqType.sales_org.in_([sales_org, ""]),
            MapeoTrqType.activo.is_(True),
        )
        fila = session.scalars(stmt).first()
        return fila.trq_type if fila else None
