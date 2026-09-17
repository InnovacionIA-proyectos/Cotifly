"""Tests de endurecimiento/despliegue (Fase 15).

- Decisión de red: `construir_runner` conecta al MCP interno por HTTP cuando hay
  `MCP_URL`, y usa el MCP in-process cuando no.
- Alertas de error del worker a un space de soporte.
- Vista de trazabilidad para Gerencia (`v_ajustes_margen_por_comercial`).
"""

from __future__ import annotations

from typing import Any

import pytest
from sqlalchemy import text
from sqlalchemy.orm import Session

from orquestador.alertas import NotificadorErrores, card_alerta_error
from orquestador.gchat import EventoChat
from orquestador.worker import ColaEventos
from tests.conftest import requiere_pg

# --- Decisión de red: runner-puente ------------------------------------------


def test_construir_runner_remoto_usa_http_transport(monkeypatch: pytest.MonkeyPatch) -> None:
    from orquestador import agente

    monkeypatch.setattr("shared.config.settings.llm_api_key", "test-key")
    monkeypatch.setattr("shared.config.settings.mcp_url", "http://bot-mcp:8080/mcp/")
    monkeypatch.setattr("shared.config.settings.mcp_auth_token", "token-interno")

    runner = agente.construir_runner()
    transporte = runner._herramientas._servidor
    assert type(transporte).__name__ == "StreamableHttpTransport"


def test_construir_runner_inprocess_por_defecto(monkeypatch: pytest.MonkeyPatch) -> None:
    from fastmcp import FastMCP

    from orquestador import agente

    monkeypatch.setattr("shared.config.settings.llm_api_key", "test-key")
    monkeypatch.setattr("shared.config.settings.mcp_url", "")

    runner = agente.construir_runner()
    assert isinstance(runner._herramientas._servidor, FastMCP)


def test_construir_runner_excluye_enviar_correo_en_esta_fase(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Correo aplazado: con correo_habilitado=False el LLM no ve `enviar_correo`."""
    from orquestador import agente

    monkeypatch.setattr("shared.config.settings.mcp_url", "")
    monkeypatch.setattr("shared.config.settings.correo_habilitado", False)
    runner = agente.construir_runner()
    assert "enviar_correo" in runner._herramientas._excluir
    assert "ping" in runner._herramientas._excluir

    monkeypatch.setattr("shared.config.settings.correo_habilitado", True)
    runner2 = agente.construir_runner()
    assert "enviar_correo" not in runner2._herramientas._excluir


# --- Alertas de error --------------------------------------------------------


def test_card_alerta_error_estructura() -> None:
    card = card_alerta_error("procesar evento", "ValueError: boom")
    assert card["cardsV2"][0]["cardId"] == "alerta_error"
    widget = card["cardsV2"][0]["card"]["sections"][0]["widgets"][0]
    assert widget["decoratedText"]["text"] == "ValueError: boom"


async def test_notificador_publica_en_soporte() -> None:
    publicados: list[tuple[Any, Any, Any]] = []

    class _Pub:
        async def publicar(self, space: Any, thread: Any, mensaje: Any) -> None:
            publicados.append((space, thread, mensaje))

    await NotificadorErrores(_Pub(), "spaces/soporte").notificar("procesar", ValueError("x"))
    assert publicados[0][0] == "spaces/soporte"
    assert "cardsV2" in publicados[0][2]


async def test_notificador_sin_space_no_publica() -> None:
    publicados: list[Any] = []

    class _Pub:
        async def publicar(self, *a: Any) -> None:
            publicados.append(a)

    await NotificadorErrores(_Pub(), "").notificar("procesar", ValueError("x"))
    assert publicados == []


async def test_worker_invoca_on_error_al_fallar() -> None:
    errores: list[tuple[EventoChat, BaseException]] = []

    async def _proc(_ev: EventoChat) -> None:
        raise ValueError("fallo simulado")

    async def _on_error(ev: EventoChat, e: BaseException) -> None:
        errores.append((ev, e))

    cola = ColaEventos(_proc, on_error=_on_error)
    await cola.iniciar()
    await cola.encolar(EventoChat(tipo="MESSAGE", space_id="s", thread_id="t", texto="x"))
    await cola.unir()
    await cola.detener()
    assert len(errores) == 1
    assert isinstance(errores[0][1], ValueError)


# --- Vista de trazabilidad para Gerencia -------------------------------------


@requiere_pg
def test_vista_ajustes_por_comercial(db_session: Session) -> None:
    from shared.repos import AjusteMargenRepo, CotizacionRepo

    cot = CotizacionRepo.crear(
        db_session, folio="PCN-VISTA-1", linea_negocio="Nacional", cliente_nombre="ACME"
    )
    AjusteMargenRepo.registrar(
        db_session, cotizacion_id=cot.id, margen_politica=0.15, margen_aplicado=0.11,
        motivo="a", comercial_email="ana@boterosoto.com.co",
    )
    AjusteMargenRepo.registrar(
        db_session, cotizacion_id=cot.id, margen_politica=0.15, margen_aplicado=0.13,
        motivo="b", comercial_email="ana@boterosoto.com.co",
    )
    db_session.flush()

    fila = db_session.execute(
        text(
            "SELECT num_ajustes, diferencia_promedio FROM v_ajustes_margen_por_comercial "
            "WHERE comercial_email = :c AND linea_negocio = :l"
        ),
        {"c": "ana@boterosoto.com.co", "l": "Nacional"},
    ).one()
    assert fila.num_ajustes == 2
    # Promedio de (-0.04, -0.02) = -0.03.
    assert float(fila.diferencia_promedio) == pytest.approx(-0.03)
