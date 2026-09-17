"""Alertas de error a un space de soporte de Google Chat (§9 · F15).

Cuando el worker falla procesando un evento, además de dejar la traza en el log,
publica una card de alerta en un **space de soporte** para que operación se entere
sin depender de revisar logs. Reutiliza el `PublicadorChat` (F13).
"""

from __future__ import annotations

from typing import Any


def card_alerta_error(contexto: str, detalle: str) -> dict[str, Any]:
    """Card compacta de alerta para el space de soporte."""
    return {
        "text": f"⚠️ Error del bot cotizador: {contexto}",
        "cardsV2": [
            {
                "cardId": "alerta_error",
                "card": {
                    "header": {"title": "Error del Bot Cotizador", "subtitle": contexto},
                    "sections": [
                        {"widgets": [{"decoratedText": {"topLabel": "Detalle", "text": detalle}}]}
                    ],
                },
            }
        ],
    }


class NotificadorErrores:
    """Publica alertas de error en el space de soporte (si está configurado)."""

    def __init__(self, publicador: Any, space_soporte: str) -> None:
        self._publicador = publicador
        self._space = space_soporte

    async def notificar(self, contexto: str, error: BaseException) -> None:
        """Publica la alerta; nunca propaga (una alerta fallida no debe romper nada)."""
        if not self._space or self._publicador is None:
            return
        detalle = f"{type(error).__name__}: {error}"
        try:
            await self._publicador.publicar(self._space, None, card_alerta_error(contexto, detalle))
        except Exception:  # noqa: BLE001 - la alerta es best-effort
            pass
