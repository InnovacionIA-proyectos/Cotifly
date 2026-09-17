"""ODataFuente: consulta el Margen Propuesto contra SAP TM en vivo (HTTP/OData) — F4.

Misma salida que `ExportJsonFuente`: el salto export→OData es solo cambiar
`SAP_TM_MODO=odata` (más URL/credenciales). Se prueba con `respx` (unit) y contra
el host real bajo `@pytest.mark.sap`. Resiliencia: timeout/5xx/red → error
estructurado (`FuenteMargenNoDisponible`), nunca tumba el MCP.
"""

from __future__ import annotations

import httpx

from mcp_server.margen.base import FuenteMargen, FuenteMargenNoDisponible

_ENTITY_SET = "RentabClienteSet"


class ODataFuente(FuenteMargen):
    modo = "odata"

    def __init__(
        self,
        base_url: str,
        user: str = "",
        password: str = "",
        timeout: float = 10.0,
        client: httpx.Client | None = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._auth = (user, password) if user else None
        self._timeout = timeout
        self._client = client  # inyectable para pruebas; si None se crea por llamada

    def _get(self, params: dict[str, str]) -> httpx.Response:
        url = f"{self._base_url}/{_ENTITY_SET}"
        if self._client is not None:
            return self._client.get(url, params=params, auth=self._auth, timeout=self._timeout)
        with httpx.Client() as c:
            return c.get(url, params=params, auth=self._auth, timeout=self._timeout)

    def obtener_value(self, sales_org: str, trq_type: str, cliente_id: str) -> str | None:
        filtro = (
            f"SalesOrgId eq '{sales_org}' and TrqType eq '{trq_type}' "
            f"and OrderPartyId eq '{cliente_id}'"
        )
        try:
            resp = self._get({"$filter": filtro, "$format": "json"})
            resp.raise_for_status()
            resultados = resp.json()["d"]["results"]
        except (httpx.HTTPError, ValueError, KeyError, TypeError) as e:
            raise FuenteMargenNoDisponible(f"SAP TM no disponible o respuesta inválida: {e}") from e
        if not resultados:
            return None
        return str(resultados[0]["Value"])
