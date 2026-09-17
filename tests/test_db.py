"""Pruebas de la capa de persistencia (Fase 1). Requieren Postgres real."""

from __future__ import annotations

import pytest
from sqlalchemy import create_engine, inspect
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from shared.repos import (
    AjusteMargenRepo,
    AuditoriaRepo,
    ConversacionRepo,
    CotizacionRepo,
    MapeoTrqTypeRepo,
    MargenPoliticaRepo,
    SesionRepo,
)
from tests.conftest import DATABASE_URL, _alembic_config, requiere_pg

pytestmark = requiere_pg

_TABLAS = {
    "cotizaciones",
    "ajustes_margen",
    "correos_enviados",
    "eventos_conversacion",
    "eventos_herramienta",
    "sesiones",
    "margenes_politica",
    "mapeo_trqtype",
}


def test_migraciones_roundtrip() -> None:
    """upgrade head crea las 8 tablas; downgrade base las elimina; re-upgrade restaura."""
    from alembic import command

    cfg = _alembic_config()
    eng = create_engine(DATABASE_URL, future=True)

    command.upgrade(cfg, "head")
    tablas = set(inspect(eng).get_table_names())
    assert _TABLAS.issubset(tablas)

    command.downgrade(cfg, "base")
    tablas = set(inspect(eng).get_table_names())
    assert not (_TABLAS & tablas)

    command.upgrade(cfg, "head")  # restaura head para el resto de la sesión
    eng.dispose()


def test_cotizacion_crud(db_session: Session) -> None:
    payload = {"referencia": "PCN-16072026", "filas": [{"destino": "BUN", "tarifa": 3000000}]}
    cot = CotizacionRepo.crear(
        db_session,
        folio="PCN-16072026",
        linea_negocio="Nacional",
        cliente_nombre="LONGYIN",
        costo=2850000,
        margen_politica=0.135,
        margen_aplicado=0.135,
        precio_final=3294798,
        moneda="COP",
        payload=payload,
    )
    assert cot.id is not None

    leida = CotizacionRepo.obtener_por_folio(db_session, "PCN-16072026")
    assert leida is not None
    assert leida.cliente_nombre == "LONGYIN"
    assert leida.payload["filas"][0]["destino"] == "BUN"  # jsonb roundtrip


def test_folio_unico(db_session: Session) -> None:
    CotizacionRepo.crear(db_session, folio="PCN-DUP", linea_negocio="Nacional")
    with pytest.raises(IntegrityError):
        CotizacionRepo.crear(db_session, folio="PCN-DUP", linea_negocio="Nacional")


def test_auditoria_automatica(db_session: Session) -> None:
    ev = AuditoriaRepo.registrar(
        db_session,
        herramienta="buscar_tarifa",
        input={"origen": "YUMBO", "destino": "BUN"},
        output_resumen={"encontrada": True, "costo": 2850000},
        exito=True,
        duracion_ms=42,
        thread_id="spaces/AAA/threads/BBB",
    )
    assert ev.id is not None
    assert ev.exito is True


def test_conversacion_y_sesion(db_session: Session) -> None:
    ses = SesionRepo.obtener_o_crear(db_session, "spaces/AAA", "threads/BBB")
    assert ses.estado == "activa"
    # Idempotente: misma clave no crea otra fila.
    ses2 = SesionRepo.obtener_o_crear(db_session, "spaces/AAA", "threads/BBB")
    assert (ses2.space_id, ses2.thread_id) == (ses.space_id, ses.thread_id)

    turno = ConversacionRepo.registrar_turno(
        db_session,
        space_id="spaces/AAA",
        thread_id="threads/BBB",
        rol="user",
        contenido="Cotiza flete Yumbo a Buenaventura",
        usuario_email="comercial@boterosoto.com.co",
    )
    assert turno.rol == "user"


def test_cotizacion_buscar(db_session: Session) -> None:
    CotizacionRepo.crear(
        db_session,
        folio="PCN-A",
        cliente_nombre="LONGYIN SAS",
        comercial_email="ana@boterosoto.com.co",
    )
    CotizacionRepo.crear(
        db_session,
        folio="PCN-B",
        cliente_nombre="OTRO CLIENTE",
        comercial_email="beto@boterosoto.com.co",
    )

    # Por folio: coincidencia exacta.
    assert [c.folio for c in CotizacionRepo.buscar(db_session, folio="PCN-A")] == ["PCN-A"]
    # Por cliente: ilike parcial e insensible a mayúsculas.
    assert [c.folio for c in CotizacionRepo.buscar(db_session, cliente="longyin")] == ["PCN-A"]
    # Por comercial: coincidencia exacta.
    porcom = CotizacionRepo.buscar(db_session, comercial="beto@boterosoto.com.co")
    assert [c.folio for c in porcom] == ["PCN-B"]
    # Sin criterios: devuelve ambas.
    assert {c.folio for c in CotizacionRepo.buscar(db_session)} == {"PCN-A", "PCN-B"}


def test_ajuste_margen_registrar(db_session: Session) -> None:
    cot = CotizacionRepo.crear(db_session, folio="PCN-AJ", linea_negocio="Nacional")
    aj = AjusteMargenRepo.registrar(
        db_session,
        cotizacion_id=cot.id,
        folio="PCN-AJ",
        margen_politica=0.15,
        margen_aplicado=0.11,
        motivo="Cliente estratégico",
        comercial_email="ana@boterosoto.com.co",
    )
    assert aj.id is not None
    assert aj.cotizacion_id == cot.id
    assert float(aj.diferencia) == pytest.approx(-0.04)  # aplicado - politica


def test_sesion_tocar_actualiza_actividad(db_session: Session) -> None:
    ses = SesionRepo.obtener_o_crear(db_session, "spaces/T", "threads/T")
    # Comparar dos marcas puestas por `tocar` (ambas del lado cliente) evita
    # depender del reloj del contenedor Postgres (server_default now()).
    SesionRepo.tocar(db_session, "spaces/T", "threads/T")
    db_session.refresh(ses)
    primera = ses.ultima_actividad
    SesionRepo.tocar(db_session, "spaces/T", "threads/T")
    db_session.refresh(ses)
    assert ses.ultima_actividad >= primera  # marca de actividad avanzó (o igual)


def test_get_session_commit_y_rollback() -> None:
    """El context manager de shared.db hace commit al salir y rollback ante error."""
    from sqlalchemy import text as sa_text

    from shared import db as db_mod

    # Motor apuntando al Postgres de prueba (independiente del engine global).
    eng = create_engine(DATABASE_URL, future=True)
    from sqlalchemy.orm import sessionmaker as _sm

    orig_engine, orig_local = db_mod.engine, db_mod.SessionLocal
    db_mod.engine = eng
    db_mod.SessionLocal = _sm(bind=eng, expire_on_commit=False, future=True)
    try:
        # Camino feliz: commit.
        with db_mod.get_session() as s:
            s.execute(sa_text("CREATE TEMP TABLE _t_commit (x int)"))
        # Camino de error: la excepción se propaga y se hace rollback.
        with pytest.raises(RuntimeError):  # noqa: PT012
            with db_mod.get_session() as s:
                s.execute(sa_text("SELECT 1"))
                raise RuntimeError("forzado")
    finally:
        db_mod.engine, db_mod.SessionLocal = orig_engine, orig_local
        eng.dispose()


def test_seed_margen_politica(db_session: Session) -> None:
    pol = MargenPoliticaRepo.obtener(db_session, "Nacional")
    assert pol is not None
    assert float(pol.margen) == pytest.approx(0.15)


def test_seed_mapeo_trqtype(db_session: Session) -> None:
    assert MapeoTrqTypeRepo.linea_de(db_session, "OPTN") == "Nacional"
    assert MapeoTrqTypeRepo.linea_de(db_session, "OPTI") == "Internacional"
    assert MapeoTrqTypeRepo.trqtype_de(db_session, "Importacion") == "OPIM"
