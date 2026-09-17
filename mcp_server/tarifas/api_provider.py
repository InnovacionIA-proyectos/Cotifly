"""ApiFletesProvider + doble lectura: tarifas desde la API Fletes (F16 · antes F3B).

⚠️ CONTRATO PROVISIONAL. El equipo Tarifario aún no publica el contrato definitivo
de la API Fletes (bloqueo de negocio #5). Este provider asume un REST simple y vive
tras la **misma interfaz `TarifaProvider`**, de modo que migrar una línea sea SOLO
configuración (`TARIFA_PROVIDER__<Linea>=api_fletes`) sin tocar la herramienta,
el Orquestador ni la skill. Al llegar el contrato real, se ajustan rutas/campos
aquí y nada más.

Contrato asumido:
    GET {base_url}/tarifas?linea=&origen=&destino=[&tipo_vehiculo=&tipo_carga=]
    Authorization: Bearer <token>
    200 → {"resultados": [{origen, destino, tipo_vehiculo, tipo_carga,
                           tarifa, moneda, vigencia}, …]}
    404 → sin tarifa para la ruta (encontrada:false, no inventa)

Reglas de salida idénticas a Drive (§5.1): 1 match exacto → costo; 0/ambiguo →
candidatas (≤5); nunca inventa. Fallo de red/5xx (tras reintentos) → error
estructurado (FuenteTarifaNoDisponible), sin tumbar el MCP.
"""

from __future__ import annotations

import logging
from typing import Any

import httpx

from mcp_server.tarifas.base import (
    Candidata,
    FuenteTarifaNoDisponible,
    ResultadoTarifa,
    TarifaProvider,
    normalizar,
)

_MAX_CANDIDATAS = 5
_log = logging.getLogger("mcp.tarifas.api_fletes")


class ApiFletesProvider(TarifaProvider):
    fuente = "api_fletes"

    def __init__(
        self,
        base_url: str,
        api_token: str = "",
        timeout_seg: int = 10,
        reintentos: int = 2,
        cliente: httpx.Client | None = None,
    ) -> None:
        self._base = base_url.rstrip("/")
        self._token = api_token
        self._timeout = timeout_seg
        self._reintentos = max(0, reintentos)
        self._cliente = cliente  # inyectable en pruebas

    def _get(self, params: dict[str, Any]) -> httpx.Response:
        """GET con reintentos ante timeout/red/5xx. Agota → FuenteTarifaNoDisponible."""
        headers = {"Authorization": f"Bearer {self._token}"} if self._token else {}
        url = f"{self._base}/tarifas"
        ultimo: Exception | None = None
        for _ in range(self._reintentos + 1):
            try:
                if self._cliente is not None:
                    resp = self._cliente.get(url, params=params, headers=headers)
                else:
                    with httpx.Client(timeout=self._timeout) as cliente:
                        resp = cliente.get(url, params=params, headers=headers)
                if resp.status_code >= 500:
                    ultimo = FuenteTarifaNoDisponible(f"API Fletes respondió {resp.status_code}")
                    continue
                return resp
            except (httpx.TimeoutException, httpx.TransportError) as e:
                ultimo = FuenteTarifaNoDisponible(f"API Fletes inaccesible: {e}")
        raise ultimo or FuenteTarifaNoDisponible("API Fletes: fallo desconocido")

    def buscar(
        self,
        linea_negocio: str,
        origen: str,
        destino: str,
        filtros: dict[str, Any] | None = None,
    ) -> ResultadoTarifa:
        filtros = filtros or {}
        params: dict[str, Any] = {"linea": linea_negocio, "origen": origen, "destino": destino}
        if filtros.get("tipo_vehiculo"):
            params["tipo_vehiculo"] = filtros["tipo_vehiculo"]
        if filtros.get("tipo_carga"):
            params["tipo_carga"] = filtros["tipo_carga"]

        resp = self._get(params)
        if resp.status_code == 404:
            return ResultadoTarifa(
                encontrada=False, fuente=self.fuente, archivo_o_referencia=self._base
            )
        if resp.status_code != 200:
            raise FuenteTarifaNoDisponible(f"API Fletes respondió {resp.status_code}")
        try:
            resultados = list(resp.json().get("resultados", []))
        except (ValueError, AttributeError) as e:
            raise FuenteTarifaNoDisponible(f"API Fletes: respuesta no interpretable: {e}") from e

        filtradas, por_od = _match(resultados, origen, destino, filtros)
        if len(filtradas) == 1:
            fila = filtradas[0]
            return ResultadoTarifa(
                encontrada=True,
                costo=float(fila["tarifa"]),
                moneda=fila.get("moneda") or "COP",
                fuente=self.fuente,
                archivo_o_referencia=self._base,
            )
        base = filtradas or por_od or resultados
        candidatas = [
            _a_candidata(f) for f in base[:_MAX_CANDIDATAS] if f.get("tarifa") is not None
        ]
        return ResultadoTarifa(
            encontrada=False,
            fuente=self.fuente,
            archivo_o_referencia=self._base,
            candidatas=candidatas,
        )


def _match(
    resultados: list[dict[str, Any]], origen: str, destino: str, filtros: dict[str, Any]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Filtra por O/D exacto + filtros opcionales (mismas reglas que Drive)."""
    por_od = [
        f
        for f in resultados
        if normalizar(f.get("origen")) == normalizar(origen)
        and normalizar(f.get("destino")) == normalizar(destino)
    ]
    filtradas = por_od
    if filtros.get("tipo_vehiculo"):
        objetivo = normalizar(filtros["tipo_vehiculo"])
        filtradas = [f for f in filtradas if normalizar(f.get("tipo_vehiculo")) == objetivo]
    if filtros.get("tipo_carga"):
        objetivo = normalizar(filtros["tipo_carga"])
        filtradas = [f for f in filtradas if normalizar(f.get("tipo_carga")) == objetivo]
    return filtradas, por_od


def _a_candidata(fila: dict[str, Any]) -> Candidata:
    return Candidata(
        origen=fila.get("origen", ""),
        destino=fila.get("destino", ""),
        tipo_vehiculo=fila.get("tipo_vehiculo"),
        tipo_carga=fila.get("tipo_carga"),
        tarifa=float(fila["tarifa"]),
        moneda=fila.get("moneda") or "COP",
        vigencia=fila.get("vigencia"),
    )


class DobleLecturaProvider(TarifaProvider):
    """Transición: sirve el resultado `principal` y **sombra-lee** `sombra`, logueando
    diferencias. Nunca rompe la cotización si la fuente sombra falla.

    Uso típico durante la migración: principal=Drive (autoritativo), sombra=API
    Fletes. Cuando las diferencias sean cero de forma estable, se promueve la línea
    a `api_fletes` directamente (solo config).
    """

    def __init__(self, principal: TarifaProvider, sombra: TarifaProvider) -> None:
        self._principal = principal
        self._sombra = sombra
        self.fuente = principal.fuente

    def buscar(
        self,
        linea_negocio: str,
        origen: str,
        destino: str,
        filtros: dict[str, Any] | None = None,
    ) -> ResultadoTarifa:
        resultado = self._principal.buscar(linea_negocio, origen, destino, filtros)
        try:
            sombra = self._sombra.buscar(linea_negocio, origen, destino, filtros)
        except Exception as e:  # noqa: BLE001 - la sombra jamás rompe la cotización
            _log.warning(
                "doble-lectura: fuente sombra '%s' falló (%s→%s): %s",
                self._sombra.fuente, origen, destino, e,
            )
            return resultado
        _comparar(linea_negocio, origen, destino, resultado, sombra)
        return resultado


def _comparar(
    linea: str, origen: str, destino: str, principal: ResultadoTarifa, sombra: ResultadoTarifa
) -> None:
    if principal.encontrada != sombra.encontrada or principal.costo != sombra.costo:
        _log.warning(
            "doble-lectura DIFERENCIA %s %s→%s: %s(enc=%s,costo=%s) vs %s(enc=%s,costo=%s)",
            linea, origen, destino,
            principal.fuente, principal.encontrada, principal.costo,
            sombra.fuente, sombra.encontrada, sombra.costo,
        )
    else:
        _log.info(
            "doble-lectura OK %s %s→%s: %s coincide con %s (costo=%s)",
            linea, origen, destino, principal.fuente, sombra.fuente, principal.costo,
        )
