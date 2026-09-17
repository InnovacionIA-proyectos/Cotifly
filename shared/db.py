"""Motor de base de datos y fábrica de sesiones (SQLAlchemy 2.x, sync).

Se usa el estilo síncrono por simplicidad y testeabilidad; las capas asíncronas
(worker del Orquestador) invocan los repositorios en un threadpool.
"""

from __future__ import annotations

from collections.abc import Iterator
from contextlib import contextmanager

from sqlalchemy import create_engine
from sqlalchemy.orm import Session, sessionmaker

from shared.config import settings

engine = create_engine(settings.database_url, pool_pre_ping=True, future=True)
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False, future=True)


@contextmanager
def get_session() -> Iterator[Session]:
    """Sesión transaccional: commit al salir sin error, rollback ante excepción."""
    session = SessionLocal()
    try:
        yield session
        session.commit()
    except Exception:
        session.rollback()
        raise
    finally:
        session.close()
