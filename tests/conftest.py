"""Fixtures de prueba para la capa de BD (Fase 1).

Las pruebas de BD usan Postgres real (no SQLite). Si no hay Postgres alcanzable
(p. ej. Docker aún no está levantado), se **saltan con un motivo claro** en vez
de fallar, para no bloquear el resto de la suite. Con `docker compose up -d
postgres` corriendo, se ejecutan de verdad.
"""

from __future__ import annotations

import os
from collections.abc import Iterator
from pathlib import Path

import pytest
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session, sessionmaker

from shared.config import settings

REPO_ROOT = Path(__file__).resolve().parent.parent
DATABASE_URL = os.environ.get("DATABASE_URL", settings.database_url)


def _postgres_disponible() -> bool:
    try:
        eng = create_engine(DATABASE_URL, connect_args={"connect_timeout": 2})
        with eng.connect() as conn:
            conn.execute(text("SELECT 1"))
        eng.dispose()
        return True
    except Exception:
        return False


requiere_pg = pytest.mark.skipif(
    not _postgres_disponible(),
    reason=f"Postgres no disponible en {DATABASE_URL} (levanta: docker compose up -d postgres)",
)


def _weasyprint_disponible() -> bool:
    """True si weasyprint puede renderizar (libs nativas presentes)."""
    try:
        from mcp_server.pdf import PdfRenderNoDisponible, contar_paginas

        try:
            contar_paginas("<p>x</p>")
            return True
        except PdfRenderNoDisponible:
            return False
    except Exception:
        return False


requiere_weasyprint = pytest.mark.skipif(
    not _weasyprint_disponible(),
    reason="weasyprint sin librerías nativas (instala el extra [pdf] + libpango en Linux)",
)


def _alembic_config():
    from alembic.config import Config

    cfg = Config(str(REPO_ROOT / "alembic.ini"))
    cfg.set_main_option("script_location", str(REPO_ROOT / "db" / "migrations"))
    cfg.set_main_option("sqlalchemy.url", DATABASE_URL)
    os.environ["DATABASE_URL"] = DATABASE_URL
    return cfg


@pytest.fixture(scope="session")
def db_engine() -> Iterator[Engine]:
    """Aplica migraciones a head sobre un esquema limpio para toda la sesión."""
    from alembic import command

    cfg = _alembic_config()
    try:
        command.downgrade(cfg, "base")
    except Exception:
        pass  # no había versión aplicada; se ignora
    command.upgrade(cfg, "head")

    eng = create_engine(DATABASE_URL, future=True)
    yield eng
    eng.dispose()


@pytest.fixture
def db_session(db_engine: Engine) -> Iterator[Session]:
    """Sesión por test con rollback (aísla cada prueba; los seeds persisten)."""
    conn = db_engine.connect()
    trans = conn.begin()
    session = sessionmaker(bind=conn, expire_on_commit=False, future=True)()
    try:
        yield session
    finally:
        session.close()
        trans.rollback()
        conn.close()
