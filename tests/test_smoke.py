"""Smoke tests de Fase 0: el andamiaje arranca y la configuración carga."""

from __future__ import annotations

from fastapi.testclient import TestClient

from orquestador.app import app
from shared.config import settings

client = TestClient(app)


def test_health_ok() -> None:
    resp = client.get("/health")
    assert resp.status_code == 200
    body = resp.json()
    assert body["status"] == "ok"
    assert body["servicio"] == "orquestador"


def test_config_defaults() -> None:
    # Decisiones de proyecto de esta etapa: SAP sobre export JSON y tarifas por Drive.
    assert settings.sap_tm_modo == "export_json"
    assert settings.tarifa_provider_default == "drive_flat_files"
    assert settings.orquestador_port == 8000
