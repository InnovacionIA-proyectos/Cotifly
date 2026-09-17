"""MCP Server del Bot Cotizador (Fase 2).

Esqueleto productivo del Componente 4:
- Transporte **Streamable HTTP** de FastMCP.
- **Auth bearer** con `MCP_AUTH_TOKEN` (token estático compartido con el
  Orquestador; se sustituye por un emisor real en despliegue si hace falta).
- **Middleware de auditoría** que registra cada invocación en
  `eventos_herramienta` sin depender del LLM (ver `auditoria.py`).
- **Contrato de error estructurado** (`{"error": {...}}`, ver `errores.py`).
- Una herramienta de humo (`ping`) que valida transporte, auth y auditoría; las
  7 herramientas de negocio se añaden en F3A–F8.

Requiere el extra `mcp`:  pip install -e ".[mcp]"
"""

from __future__ import annotations

import functools
import logging
from collections.abc import Callable
from typing import TYPE_CHECKING, Any, Literal
from uuid import UUID

from shared.config import settings

logger = logging.getLogger("bot_cotizador.mcp")

if TYPE_CHECKING:  # solo para anotaciones; el import real ocurre en build_server()
    from fastmcp import FastMCP

    from mcp_server.correo import ServicioCorreo
    from mcp_server.margen import MargenResolver
    from mcp_server.tarifas import TarifaRouter


def _auth_provider() -> Any:
    """Verificador de token estático a partir de `MCP_AUTH_TOKEN`."""
    from fastmcp.server.auth.providers.jwt import StaticTokenVerifier

    return StaticTokenVerifier(
        tokens={settings.mcp_auth_token: {"client_id": "orquestador", "scopes": []}},
    )


def _tool_segura(fn: Callable[..., dict[str, Any]]) -> Callable[..., dict[str, Any]]:
    """Red de seguridad: convierte cualquier excepción *inesperada* en `{"error":{...}}`.

    Los fallos previsibles ya se capturan dentro de cada herramienta y devuelven su
    propio error estructurado; este decorador solo atrapa lo no anticipado (BD caída,
    render inesperado, etc.) para que el LLM reciba siempre el contrato JSON estable
    (§5.2b) en vez de un `ToolError` genérico. El detalle interno se registra en el
    log del servidor, nunca se expone al cliente. `functools.wraps` conserva la firma
    y las anotaciones para que FastMCP derive el esquema de la herramienta.
    """
    from mcp_server.errores import error_estructurado

    @functools.wraps(fn)
    def envoltura(*args: Any, **kwargs: Any) -> dict[str, Any]:
        try:
            return fn(*args, **kwargs)
        except Exception:  # noqa: BLE001 - contrato de error uniforme del MCP
            logger.exception("Fallo inesperado en la herramienta %s", fn.__name__)
            return error_estructurado(
                "ERROR_INTERNO",
                "Ocurrió un error interno procesando la solicitud. "
                "Reintenta más tarde o contacta a soporte.",
            )

    return envoltura


def _texto(valor: Any, *claves: str) -> str | None:
    """Coerción tolerante a texto para columnas escalares de la BD.

    El LLM a veces manda un objeto donde el esquema pide un string (p. ej.
    `cliente: {"nombre": "LONGYIN", "id": "15927"}`). Se extrae la clave más
    razonable en vez de reventar el INSERT (§5.2: nunca excepción cruda).
    """
    if valor is None:
        return None
    if isinstance(valor, str):
        return valor.strip() or None
    if isinstance(valor, dict):
        for k in claves or ("nombre", "name", "id"):
            v = valor.get(k)
            if isinstance(v, (str, int, float)) and str(v).strip():
                return str(v).strip()
        import json

        return json.dumps(valor, ensure_ascii=False)[:200]
    return str(valor)


def _num(valor: Any) -> float | None:
    """Convierte a float de forma tolerante (acepta '3.294.798', 2850000, None)."""
    if valor is None or valor == "":
        return None
    if isinstance(valor, (int, float)):
        return float(valor)
    import re

    limpio = re.sub(r"[^\d.-]", "", str(valor).replace(".", "").replace(",", "."))
    try:
        return float(limpio) if limpio else None
    except ValueError:
        return None


def _datos_trazabilidad(cotizacion: dict[str, Any]) -> dict[str, Any]:
    """Extrae de forma tolerante los campos de trazabilidad de precio/margen (§7.3).

    El payload sigue el esquema de la skill (`ejemplo-solicitud.json`); el agente
    incorpora `costo`, `margen_politica`, `margen_aplicado`, `precio_final`,
    `fuente_tarifa` y `origen_margen` a partir de las salidas de `buscar_tarifa`,
    `obtener_margen` y `calcular_precio`. `destino` sale del primer renglón de la
    tabla de tarifa (o del bloque `tarifa`). Todo con `.get()` para no romper si el
    agente omite algún campo: lo que falte queda NULL, no aborta el registro.
    """
    tarifa = cotizacion.get("tarifa") or {}
    filas = tarifa.get("filas") or []
    destino = None
    if filas and isinstance(filas[0], dict):
        destino = filas[0].get("destino")
    destino = destino or tarifa.get("destino")
    cliente_id = cotizacion.get("cliente_id_sap") or cotizacion.get("cliente_id")
    if cliente_id is None and isinstance(cotizacion.get("cliente"), dict):
        cliente_id = cotizacion["cliente"].get("id") or cotizacion["cliente"].get("cliente_id")
    return {
        "cliente_id_sap": _texto(cliente_id, "id", "cliente_id"),
        "destino": _texto(destino, "destino", "nombre"),
        "costo": _num(cotizacion.get("costo")),
        "margen_politica": _num(cotizacion.get("margen_politica")),
        "margen_aplicado": _num(cotizacion.get("margen_aplicado")),
        "precio_final": _num(cotizacion.get("precio_final") or cotizacion.get("precio")),
        "fuente_tarifa": _texto(cotizacion.get("fuente_tarifa"), "fuente"),
        "origen_margen": _texto(cotizacion.get("origen_margen"), "origen"),
    }


def build_server(
    router: TarifaRouter | None = None,
    margen: MargenResolver | None = None,
    correo: ServicioCorreo | None = None,
) -> FastMCP:
    try:
        from fastmcp import FastMCP
    except ImportError as e:  # pragma: no cover - el extra [mcp] se instala en Fase 2
        raise RuntimeError(
            'fastmcp no está instalado. Ejecuta: pip install -e ".[mcp]" (Fase 2).'
        ) from e

    from mcp_server.auditoria import AuditoriaMiddleware
    from mcp_server.correo import (
        ConfirmacionRequerida,
        CorreoPdfNoEncontrado,
        CotizacionNoEncontrada,
        EntradaEnviarCorreo,
        EnvioDuplicado,
        EnvioSmtpError,
        construir_servicio_correo,
        cuerpo_por_defecto,
    )
    from mcp_server.errores import error_estructurado
    from mcp_server.margen import (
        EntradaObtenerMargen,
        FuenteMargenNoDisponible,
        construir_resolver,
    )
    from mcp_server.margen.servicio import MargenLineaDesconocida
    from mcp_server.pdf import PdfRenderNoDisponible
    from mcp_server.pdf import construir_html as _construir_html
    from mcp_server.pdf import render_pdf_bytes as _render_pdf_bytes
    from mcp_server.precio import PrecioInvalido
    from mcp_server.precio import calcular_precio as _calcular_precio
    from mcp_server.tarifas import EntradaBuscarTarifa, FuenteTarifaNoDisponible, construir_router
    from shared.almacen_pdf import construir_almacen
    from shared.db import get_session
    from shared.models import Cotizacion
    from shared.repos import AjusteMargenRepo, CorreoRepo, CotizacionRepo

    # mask_error_details: una excepción no controlada llega al cliente como un
    # error genérico (sin traceback ni detalle interno). Ver contrato en errores.py.
    mcp = FastMCP("bot-cotizador-mcp", auth=_auth_provider(), mask_error_details=True)
    mcp.add_middleware(AuditoriaMiddleware())

    # Router y resolver se inyectan en pruebas (fixtures); en producción de config.
    tarifas = router if router is not None else construir_router()
    margenes = margen if margen is not None else construir_resolver()
    correos = correo if correo is not None else construir_servicio_correo()

    @mcp.tool
    def ping(fallar: bool = False) -> dict[str, Any]:
        """Herramienta de humo: confirma transporte, auth y auditoría.

        Con `fallar=True` devuelve un error estructurado, para ejercitar el
        contrato de error sin necesidad de una herramienta de negocio.
        """
        if fallar:
            return error_estructurado(
                "PING_FORZADO",
                "Fallo simulado para verificar el contrato de error del MCP.",
            )
        return {"status": "ok", "servicio": "mcp", "entorno": settings.entorno}

    @mcp.tool
    @_tool_segura
    def buscar_tarifa(
        linea_negocio: str,
        origen: str,
        destino: str,
        tipo_vehiculo: str | None = None,
        tipo_carga: str | None = None,
        peso_kg: float | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Busca la tarifa de costo para la ruta (§5.1).

        La tarifa depende del **peso** de la carga: pasa `peso_kg` para que filtre el
        rango correcto (pesoInf–pesoSup o pesoLimite). Sin coincidencia exacta
        devuelve `encontrada:false` con hasta 5 candidatas (incluyen el rango de peso
        y el detalle); **nunca** inventa una tarifa. Si la fuente no está disponible
        devuelve un error estructurado. `space_id`/`thread_id` son opcionales y solo
        alimentan la auditoría por hilo (los inyecta el Orquestador).
        """
        entrada = EntradaBuscarTarifa(
            linea_negocio=linea_negocio,
            origen=origen,
            destino=destino,
            tipo_vehiculo=tipo_vehiculo,
            tipo_carga=tipo_carga,
            peso_kg=peso_kg,
        )
        try:
            return tarifas.buscar(entrada).model_dump()
        except FuenteTarifaNoDisponible as e:
            return error_estructurado("TARIFA_FUENTE_NO_DISPONIBLE", str(e))
        except ValueError as e:
            return error_estructurado("TARIFA_CONFIG_INVALIDA", str(e))

    @mcp.tool
    @_tool_segura
    def obtener_margen(
        linea_negocio: str,
        cliente_id: str | None = None,
        sales_org: str | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Margen propuesto desde SAP TM, con fallback a la política por línea (§5.1).

        Normaliza el `Value` a decimal (13.5 → 0.135) y marca `anomalo` si cae
        fuera de [0, 60%] (no lo aplica ciego). Sin cliente o sin registro en SAP
        cae a `margenes_politica` con `origen:"politica_linea"`. `space_id`/
        `thread_id` son opcionales y solo alimentan la auditoría por hilo.
        """
        entrada = EntradaObtenerMargen(
            linea_negocio=linea_negocio,
            cliente_id=cliente_id,
            sales_org=sales_org,
        )
        try:
            with get_session() as s:
                return margenes.resolver(s, entrada).model_dump()
        except FuenteMargenNoDisponible as e:
            return error_estructurado("MARGEN_FUENTE_NO_DISPONIBLE", str(e))
        except MargenLineaDesconocida as e:
            return error_estructurado("MARGEN_LINEA_DESCONOCIDA", str(e))

    @mcp.tool
    @_tool_segura
    def calcular_precio(
        costo: float,
        margen: float,
        metodo: Literal["margen", "markup"] = "margen",
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Cálculo determinista del precio (§5.1). El LLM no hace la aritmética.

        `margen`: precio = costo/(1-m); `markup`: precio = costo*(1+m). Devuelve
        precio, costo, margen, metodo y utilidad. Rechaza entradas inválidas
        (costo ≤ 0, margen negativo, o margen ≥ 1 con método 'margen').
        """
        try:
            return _calcular_precio(costo, margen, metodo).model_dump()
        except PrecioInvalido as e:
            return error_estructurado("PRECIO_INVALIDO", str(e))

    @mcp.tool
    @_tool_segura
    def generar_pdf(
        cotizacion: dict[str, Any],
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Genera el PDF corporativo de la cotización y la registra en BD (§5.1).

        El payload sigue el esquema de la skill (`ejemplo-solicitud.json`). Es
        idempotente por folio: si `referencia` ya existe, versiona a `-v2`, `-v3`…
        Devuelve `cotizacion_id`, `folio`, `pdf_url` y `registrada_en_bd`.
        """
        base_folio = _texto(cotizacion.get("referencia"), "referencia", "folio")
        if not base_folio:
            return error_estructurado(
                "PDF_PAYLOAD_INVALIDO", "Falta 'referencia' (folio PCN-DDMMAAAA)."
            )
        # Normaliza `cliente` a texto ANTES de armar el HTML: si el agente mandó un
        # objeto ({"nombre": ..., "id": ...}), el documento muestra el nombre y el
        # id se conserva como cliente_id_sap para la trazabilidad.
        cliente_raw = cotizacion.get("cliente")
        if isinstance(cliente_raw, dict):
            cid = cliente_raw.get("id") or cliente_raw.get("cliente_id")
            if cid is not None and not cotizacion.get("cliente_id_sap"):
                cotizacion = {**cotizacion, "cliente_id_sap": _texto(cid)}
        cliente_nombre = _texto(cliente_raw, "nombre", "name", "razon_social")
        if cliente_nombre is not None:
            cotizacion = {**cotizacion, "cliente": cliente_nombre}
        try:
            with get_session() as s:
                folio = CotizacionRepo.folio_versionado(s, base_folio)
                html = _construir_html(cotizacion)
                # Render a bytes y guardado agnóstico (volumen o S3/MinIO según config).
                datos_pdf, _paginas = _render_pdf_bytes(html)
                pdf_path = construir_almacen().guardar(f"{folio}.pdf", datos_pdf)
                comercial = cotizacion.get("comercial")
                if not isinstance(comercial, dict):
                    comercial = {}
                tarifa = cotizacion.get("tarifa")
                if not isinstance(tarifa, dict):
                    tarifa = {}
                traza = _datos_trazabilidad(cotizacion)
                cot = CotizacionRepo.crear(
                    s,
                    folio=folio,
                    linea_negocio=_texto(cotizacion.get("linea_negocio"), "linea", "nombre"),
                    cliente_nombre=cliente_nombre,
                    cliente_id_sap=traza["cliente_id_sap"],
                    origen=_texto(tarifa.get("origen"), "origen", "nombre"),
                    destino=traza["destino"],
                    costo=traza["costo"],
                    margen_politica=traza["margen_politica"],
                    margen_aplicado=traza["margen_aplicado"],
                    precio_final=traza["precio_final"],
                    metodo_calculo=_texto(cotizacion.get("metodo_calculo"), "metodo"),
                    moneda="COP",
                    fuente_tarifa=traza["fuente_tarifa"],
                    origen_margen=traza["origen_margen"],
                    payload=cotizacion,
                    pdf_path=pdf_path,
                    estado="generada",
                    comercial_email=_texto(comercial.get("email"), "email", "correo"),
                    space_id=space_id,
                    thread_id=thread_id,
                )
                cotizacion_id = str(cot.id)
            return {
                "cotizacion_id": cotizacion_id,
                "folio": folio,
                "pdf_url": f"{settings.pdf_base_url}/pdfs/{folio}.pdf",
                "registrada_en_bd": True,
            }
        except PdfRenderNoDisponible as e:
            return error_estructurado("PDF_RENDER_NO_DISPONIBLE", str(e))

    @mcp.tool
    @_tool_segura
    def enviar_correo(
        cotizacion_id: str,
        para: list[str],
        asunto: str,
        cuerpo: str | None = None,
        confirmado_por: str = "",
        cc: list[str] | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Envía la cotización al cliente con el PDF adjunto (§5.1).

        **Exige `confirmado_por`** (autorización explícita del comercial): sin él no
        envía. `confirmado_por` es además el correo del comercial que actúa como
        remitente del envío. Rechaza duplicados exactos en < 5 min. Un fallo del
        transporte se traza como `fallido` y se reporta como error estructurado
        (nunca excepción cruda). `space_id`/`thread_id` alimentan la auditoría.
        """
        entrada = EntradaEnviarCorreo(
            cotizacion_id=cotizacion_id,
            para=para,
            asunto=asunto,
            cuerpo=cuerpo or cuerpo_por_defecto(),
            confirmado_por=confirmado_por,
            cc=cc or [],
        )
        try:
            with get_session() as s:
                message_id = correos.enviar(s, entrada)
            return {"enviado": True, "message_id": message_id, "registrado_en_bd": True}
        except ConfirmacionRequerida as e:
            return error_estructurado("CONFIRMACION_REQUERIDA", str(e))
        except CotizacionNoEncontrada as e:
            return error_estructurado("COTIZACION_NO_ENCONTRADA", str(e))
        except EnvioDuplicado as e:
            return error_estructurado("CORREO_DUPLICADO", str(e))
        except CorreoPdfNoEncontrado as e:
            return error_estructurado("CORREO_PDF_NO_ENCONTRADO", str(e))
        except EnvioSmtpError as e:
            # Traza el fallo en su propia transacción (la del envío hizo rollback).
            with get_session() as s:
                CorreoRepo.registrar(
                    s,
                    cotizacion_id=UUID(cotizacion_id),
                    para=para,
                    cc=cc or None,
                    asunto=asunto,
                    confirmado_por=confirmado_por,
                    estado="fallido",
                    error=str(e),
                )
            return error_estructurado("CORREO_ENVIO_FALLIDO", str(e))

    @mcp.tool
    @_tool_segura
    def registrar_ajuste_margen(
        margen_politica: float,
        margen_aplicado: float,
        motivo: str,
        comercial: str,
        cotizacion_id: str | None = None,
        folio: str | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Registra un ajuste cuando el comercial negocia un margen distinto (§5.1).

        Persiste en `ajustes_margen` con `diferencia = aplicado - politica`.
        Devuelve `registrado` y `diferencia`.
        """
        try:
            with get_session() as s:
                cot_uuid = None
                if cotizacion_id:
                    cot_uuid = UUID(cotizacion_id)
                    if s.get(Cotizacion, cot_uuid) is None:
                        return error_estructurado(
                            "AJUSTE_COTIZACION_INVALIDA",
                            f"No existe la cotización {cotizacion_id}.",
                        )
                ajuste = AjusteMargenRepo.registrar(
                    s,
                    cotizacion_id=cot_uuid,
                    folio=folio,
                    margen_politica=margen_politica,
                    margen_aplicado=margen_aplicado,
                    motivo=motivo,
                    comercial_email=comercial,
                )
                diferencia = float(ajuste.diferencia) if ajuste.diferencia is not None else 0.0
            return {"registrado": True, "diferencia": diferencia}
        except ValueError as e:  # UUID mal formado
            return error_estructurado("AJUSTE_ID_INVALIDO", str(e))

    @mcp.tool
    @_tool_segura
    def consultar_cotizacion(
        folio: str | None = None,
        cliente: str | None = None,
        comercial: str | None = None,
        space_id: str | None = None,
        thread_id: str | None = None,
    ) -> dict[str, Any]:
        """Busca cotizaciones históricas por folio, cliente o comercial (§5.1, lectura).

        Permite reutilizar/reenviar una cotización previa. Devuelve `total` y
        `resultados` (folio, cliente, línea, ruta, costo, márgenes, precio, estado,
        PDF, fecha) — la trazabilidad completa de precio/margen (§7.3).
        """
        def _f(valor: Any) -> float | None:
            return float(valor) if valor is not None else None

        with get_session() as s:
            filas = CotizacionRepo.buscar(
                s, folio=folio, cliente=cliente, comercial=comercial
            )
            resultados = [
                {
                    "cotizacion_id": str(c.id),
                    "folio": c.folio,
                    "cliente_nombre": c.cliente_nombre,
                    "linea_negocio": c.linea_negocio,
                    "origen": c.origen,
                    "destino": c.destino,
                    "costo": _f(c.costo),
                    "margen_politica": _f(c.margen_politica),
                    "margen_aplicado": _f(c.margen_aplicado),
                    "precio_final": _f(c.precio_final),
                    "moneda": c.moneda,
                    "fuente_tarifa": c.fuente_tarifa,
                    "origen_margen": c.origen_margen,
                    "estado": c.estado,
                    "comercial_email": c.comercial_email,
                    "pdf_url": (
                        f"{settings.pdf_base_url}/pdfs/{c.folio}.pdf" if c.pdf_path else None
                    ),
                    "created_at": c.created_at.isoformat() if c.created_at else None,
                }
                for c in filas
            ]
        return {"total": len(resultados), "resultados": resultados}

    return mcp


if __name__ == "__main__":  # pragma: no cover
    server = build_server()
    server.run(transport="streamable-http", host=settings.mcp_host, port=settings.mcp_port)
