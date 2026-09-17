"""Construcción de mensajes y cards de Google Chat (§4.1 · F13).

Traduce el resultado de un turno del Agent Runner al mensaje que se publica en el
hilo del comercial:
- **Card de previsualización** (costo · margen · precio → ¿confirmas o ajustas?),
  con los puntos de control humano: confirmar (genera el PDF) o ajustar el margen.
- **Card de PDF** con el link a la cotización generada.
- Texto simple cuando el turno no produjo ni precio ni PDF (p. ej. una pregunta).

El gating de envío es una **invariante**: la card de previsualización siempre pide
confirmación explícita; el correo nunca se dispara desde aquí.
"""

from __future__ import annotations

from typing import Any

from orquestador.agente import ResultadoTurno

# Texto que ancla el punto de control humano (F13/§8, invariante de confirmación).
PROMPT_CONFIRMACION = (
    "¿Confirmas para generar el PDF, o quieres ajustar el margen? "
    "Responde «Confirmo» o «Ajusta el margen a X%»."
)


def formato_cop(valor: float | int) -> str:
    """Formatea un monto en pesos colombianos: 3294798 → '$3.294.798'."""
    return "$" + f"{round(valor):,}".replace(",", ".")


def formato_pct(valor: float) -> str:
    """Formatea un margen decimal como porcentaje: 0.135 → '13,5%'."""
    return f"{valor * 100:.1f}".replace(".", ",") + "%"


def _decorated(top_label: str, texto: str) -> dict[str, Any]:
    return {"decoratedText": {"topLabel": top_label, "text": texto}}


def card_previsualizacion(
    costo: float, margen_aplicado: float, precio: float, utilidad: float,
    margen_politica: float | None = None,
) -> dict[str, Any]:
    """Card con el resumen de previsualización y los botones de control humano."""
    politica = margen_politica if margen_politica is not None else margen_aplicado
    widgets: list[dict[str, Any]] = [
        _decorated("Costo", formato_cop(costo)),
        _decorated("Margen de política", formato_pct(politica)),
    ]
    if abs(margen_aplicado - politica) > 1e-9:
        widgets.append(_decorated("Margen aplicado", formato_pct(margen_aplicado)))
    widgets += [
        _decorated("Precio", formato_cop(precio)),
        _decorated("Utilidad", formato_cop(utilidad)),
        {
            "buttonList": {
                "buttons": [
                    {"text": "Confirmar y generar PDF",
                     "onClick": {"action": {"function": "confirmar_cotizacion"}}},
                    {"text": "Ajustar margen",
                     "onClick": {"action": {"function": "ajustar_margen"}}},
                ]
            }
        },
    ]
    return {
        "text": PROMPT_CONFIRMACION,
        "cardsV2": [
            {
                "cardId": "previsualizacion",
                "card": {
                    "header": {"title": "Previsualización de cotización",
                               "subtitle": "Revisa costo, margen y precio"},
                    "sections": [{"widgets": widgets}],
                },
            }
        ],
    }


def card_pdf(folio: str | None, pdf_url: str | None, texto: str | None = None) -> dict[str, Any]:
    """Card con el link al PDF de la cotización generada."""
    widgets: list[dict[str, Any]] = [_decorated("Folio", folio or "—")]
    if pdf_url:
        widgets.append(
            {
                "buttonList": {
                    "buttons": [
                        {"text": "Abrir PDF", "onClick": {"openLink": {"url": pdf_url}}}
                    ]
                }
            }
        )
    return {
        "text": texto or "Cotización generada.",
        "cardsV2": [
            {
                "cardId": "pdf",
                "card": {
                    "header": {"title": "Cotización lista", "subtitle": folio or ""},
                    "sections": [{"widgets": widgets}],
                },
            }
        ],
    }


def mensaje_para_resultado(resultado: ResultadoTurno) -> dict[str, Any]:
    """Elige el mensaje a publicar según lo que produjo el turno del agente."""
    pdf = resultado.ultima_salida("generar_pdf")
    if pdf and "error" not in pdf:
        return card_pdf(pdf.get("folio"), pdf.get("pdf_url"), resultado.texto)

    precio = resultado.ultima_salida("calcular_precio")
    if precio and "error" not in precio:
        margen_obt = resultado.ultima_salida("obtener_margen")
        politica = (
            margen_obt["margen"]
            if margen_obt and "error" not in margen_obt and margen_obt.get("margen") is not None
            else None
        )
        return card_previsualizacion(
            costo=precio["costo"],
            margen_aplicado=precio["margen"],
            precio=precio["precio"],
            utilidad=precio["utilidad"],
            margen_politica=politica,
        )

    return {"text": resultado.texto}
