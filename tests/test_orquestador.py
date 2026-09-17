"""Tests del API Orquestador (Fase 9): health, readiness y servidor de PDFs."""

from __future__ import annotations

from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from orquestador.app import app
from shared.config import settings
from tests.conftest import requiere_pg

client = TestClient(app)


def test_health_liveness() -> None:
    resp = client.get("/health")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ok"


@requiere_pg
def test_health_readiness_ok() -> None:
    resp = client.get("/health/ready")
    assert resp.status_code == 200
    assert resp.json()["status"] == "ready"


def test_health_readiness_falla_503(monkeypatch: pytest.MonkeyPatch) -> None:
    from orquestador import app as appmod

    class _EngineRoto:
        def connect(self) -> object:
            raise OSError("BD no alcanzable")

    monkeypatch.setattr(appmod, "engine", _EngineRoto())
    resp = client.get("/health/ready")
    assert resp.status_code == 503
    assert resp.json()["status"] == "unavailable"


def test_descarga_pdf_ok(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")
    (tmp_path / "PCN-19032026.pdf").write_bytes(b"%PDF-1.4 contenido")
    resp = client.get("/pdfs/PCN-19032026.pdf")
    assert resp.status_code == 200
    assert resp.headers["content-type"] == "application/pdf"
    assert resp.content == b"%PDF-1.4 contenido"


def test_descarga_pdf_inexistente_404(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")
    resp = client.get("/pdfs/PCN-NOEXISTE.pdf")
    assert resp.status_code == 404


def test_descarga_pdf_nombre_invalido_404(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr(settings, "pdf_storage_dir", str(tmp_path))
    monkeypatch.setattr(settings, "pdf_storage_modo", "volumen")
    # Extensión no .pdf y un intento de traversal: ambos rechazados por el patrón.
    assert client.get("/pdfs/factura.txt").status_code == 404
    assert client.get("/pdfs/..%2f..%2fetc%2fpasswd").status_code == 404
