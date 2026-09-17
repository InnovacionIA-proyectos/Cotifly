"""Tests del contexto de usuario en el system y de la inyección space/thread.

- `_system_con_contexto`: antepone la identidad del comercial a la skill (§4.3).
- El puente `HerramientasMCP` inyecta space_id/thread_id en las tools que los
  aceptan, sin que el LLM tenga que pasarlos (auditoría por hilo, §7.3).
- Extremo a extremo (Anthropic mockeado): el `system` enviado lleva el contexto.
"""

from __future__ import annotations

from orquestador.agente import AgentRunner, HerramientasMCP, _system_con_contexto
from tests.test_agente import _RESP_FINAL, _LLMFake, _servidor_precio

# --- _system_con_contexto (puro) --------------------------------------------


def test_contexto_antepone_identidad_del_comercial() -> None:
    out = _system_con_contexto(
        "SKILL",
        usuario_email="nvargas@boterosoto.com.co",
        usuario_nombre="Nathaly",
        fecha_hoy="2026-07-21",
    )
    assert "# Contexto del comercial" in out
    assert "Nathaly" in out
    assert "nvargas@boterosoto.com.co" in out
    assert "2026-07-21" in out
    assert out.rstrip().endswith("SKILL")  # la skill queda al final, intacta


def test_contexto_vacio_no_modifica_la_skill() -> None:
    salida = _system_con_contexto("SKILL", usuario_email=None, usuario_nombre=None, fecha_hoy=None)
    assert salida == "SKILL"


# --- inyección de contexto en el puente -------------------------------------


class _CliFalso:
    """Cliente MCP mínimo: registra los argumentos con que se invocó la tool."""

    def __init__(self) -> None:
        self.ultimos_args: dict | None = None

    async def call_tool(self, nombre: str, args: dict) -> object:
        self.ultimos_args = args

        class _R:
            structured_content = {"ok": True}

        return _R()


async def test_invocar_inyecta_space_y_thread_en_tools_que_los_aceptan() -> None:
    h = HerramientasMCP(servidor=object())
    cli = _CliFalso()
    h._cli = cli  # type: ignore[assignment]
    h._acepta = {"buscar_tarifa": {"linea_negocio", "space_id", "thread_id"}}
    h.fijar_contexto(space_id="spaces/A", thread_id="threads/1")

    await h.invocar("buscar_tarifa", {"linea_negocio": "Nacional"})
    assert cli.ultimos_args == {
        "linea_negocio": "Nacional",
        "space_id": "spaces/A",
        "thread_id": "threads/1",
    }


async def test_invocar_no_pisa_valores_del_llm_ni_agrega_no_aceptados() -> None:
    h = HerramientasMCP(servidor=object())
    cli = _CliFalso()
    h._cli = cli  # type: ignore[assignment]
    # 'thread_id' NO está en los aceptados de esta tool.
    h._acepta = {"calcular_precio": {"costo", "margen", "space_id"}}
    h.fijar_contexto(space_id="spaces/A", thread_id="threads/1")

    await h.invocar("calcular_precio", {"costo": 100, "margen": 0.1, "space_id": "provisto"})
    args = cli.ultimos_args or {}
    assert args["space_id"] == "provisto"  # no se pisa lo que ya venía
    assert "thread_id" not in args  # la tool no lo acepta → no se agrega


# --- extremo a extremo: el system enviado lleva el contexto -----------------


async def test_ejecutar_turno_envia_system_con_contexto() -> None:
    fake = _LLMFake(_RESP_FINAL)
    runner = AgentRunner(completar=fake, herramientas=HerramientasMCP(_servidor_precio()))
    await runner.ejecutar_turno(
        "hola",
        usuario_email="nvargas@boterosoto.com.co",
        usuario_nombre="Nathaly Vargas",
        fecha_hoy="2026-07-21",
    )
    # El system va como primer mensaje (formato OpenAI) y lleva el contexto del comercial.
    system_msg = fake.llamadas[0]["messages"][0]
    assert system_msg["role"] == "system"
    assert "nvargas@boterosoto.com.co" in system_msg["content"]
    assert "Nathaly Vargas" in system_msg["content"]
