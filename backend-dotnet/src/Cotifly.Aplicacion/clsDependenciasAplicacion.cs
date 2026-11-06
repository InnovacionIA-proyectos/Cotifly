using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Metricas.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cotifly.Aplicacion;

/// <summary>
/// Registro de los servicios de la capa de aplicación (casos de uso y validadores).
/// Cada módulo agrega aquí sus servicios a medida que se implementa. Los puertos que usan
/// (repositorios, fuente de margen, proveedor de tarifas, generador y almacén de PDF, transporte de correo) y las
/// opciones los registra Infraestructura.
/// </summary>
public static class clsDependenciasAplicacion
{
    public static IServiceCollection AddAplicacion(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        // Auditoría transversal (HU-003.5, #11): singleton que abre su propio ámbito por evento.
        servicios.AddSingleton<IClsAuditoriaService, clsAuditoriaService>();

        // Precio y Margen (HU-003.4, #10). El ajuste negociado se audita (#11).
        servicios.AddSingleton<IClsPrecioService, clsPrecioService>();
        servicios.AddSingleton<Cotizaciones.Services.IClsFijacionMonedaService, Cotizaciones.Services.clsFijacionMonedaService>();
        servicios.AddScoped<IClsMargenResolverService, clsMargenResolverService>();
        // HU-042.2 (#243): aviso de la copia de SAP TM vieja en aviso_margen del costeo.
        servicios.AddScoped<IClsAvisoSincronizacionMargenSap, clsAvisoSincronizacionMargenSap>();
        servicios.AddScoped<clsAjusteMargenService>();
        servicios.AddScoped<IClsAjusteMargenService>(sp => new clsAjusteMargenServiceAuditado(
            sp.GetRequiredService<clsAjusteMargenService>(), sp.GetRequiredService<IClsAuditoriaService>()));

        // PDF, correo, teléfono y seguimiento (HU-003.5, #11). Generar PDF y enviar correo se auditan con decoradores.
        servicios.AddScoped<clsGenerarPdfService>();
        servicios.AddScoped<IClsGenerarPdfService>(sp => new clsGenerarPdfServiceAuditado(
            sp.GetRequiredService<clsGenerarPdfService>(), sp.GetRequiredService<IClsAuditoriaService>()));
        servicios.AddSingleton<clsCandadoEnvios>();
        servicios.AddScoped<clsCorreoService>();
        servicios.AddScoped<IClsCorreoService>(sp => new clsCorreoServiceAuditado(
            sp.GetRequiredService<clsCorreoService>(), sp.GetRequiredService<IClsAuditoriaService>()));
        servicios.AddScoped<IClsVistaPreviaCorreoService, clsVistaPreviaCorreoService>();
        // HU-033 (#199): historial y reintento de los envíos, y la consulta del estado en MailSender (worker de correos).
        servicios.AddScoped<IClsHistorialCorreosService, clsHistorialCorreosService>();
        servicios.AddScoped<IClsSeguimientoCorreosService, clsSeguimientoCorreosService>();
        servicios.AddScoped<IClsDestinatariosPreviosService, clsDestinatariosPreviosService>();
        servicios.AddScoped<IClsTelefonoComercialService, clsTelefonoComercialService>();
        // HU-018.1 (#155): lista de motivos de pérdida (Resultado:MotivosPerdida) para /resultado, /seguimiento y la sincronización.
        servicios.AddSingleton<IClsCatalogoMotivosPerdida, clsCatalogoMotivosPerdida>();
        servicios.AddScoped<IClsSeguimientoService, clsSeguimientoService>();

        // Login SSO ClientePro con verificación doble, refresh y logout (HU-003.6, #12). Cada intento se audita.
        servicios.AddScoped<clsVerificadorIdentidad>();
        servicios.AddScoped<IClsVerificadorIdentidad>(sp => sp.GetRequiredService<clsVerificadorIdentidad>());
        servicios.AddScoped<IClsLoginClienteProService, clsLoginClienteProService>();
        // HU-040.2 (#230, ADR-025): elegir el rol al entrar (multi-rol), con el ticket de un solo uso del login.
        servicios.AddScoped<IClsEleccionRolService, clsEleccionRolService>();
        servicios.AddScoped<IClsRefreshTokenService, clsRefreshTokenService>();
        servicios.AddScoped<IClsLogoutService, clsLogoutService>();
        servicios.AddSingleton<IClsRevocacionAccesos, clsCacheTokensRevocados>();

        // API web /web/* (HU-003.6, #12 parte B): mis cotizaciones, nueva versión, folio del servidor (ADR-011), descarga
        // autenticada del PDF, y administración de líneas (HU-004.9).
        servicios.AddScoped<IClsMisCotizacionesService, clsMisCotizacionesService>();
        servicios.AddScoped<IClsCotizacionesRegionalService, clsCotizacionesRegionalService>();
        // HU-027.1 (#179): detalle por folio (dueño, o gerente de su regional en solo consulta).
        servicios.AddScoped<IClsDetalleCotizacionService, clsDetalleCotizacionService>();
        // HU-034.1 (#201): la licitación ajena en solo lectura (gerente de la regional o director), con la regla del detalle.
        servicios.AddScoped<IClsAccesoCotizacionAjena, clsAccesoCotizacionAjenaService>();
        // HU-018.1 (#155, parte C): Excel de la cotización (dueño: versión interna; gerente y director: sin costos ni márgenes).
        servicios.AddScoped<IClsExportacionCotizacionService, clsExportacionCotizacionService>();
        servicios.AddScoped<IClsNuevaVersionService, clsNuevaVersionService>();
        servicios.AddScoped<IClsGeneradorFolioService, clsGeneradorFolioService>();
        servicios.AddScoped<IClsDescargaPdfService, clsDescargaPdfService>();
        servicios.AddScoped<IClsLineasNegocioAdminService, clsLineasNegocioAdminService>();
        // HU-016.1 (#154): catálogo de líneas activas para el desplegable de la captura (sin margen).
        servicios.AddScoped<IClsLineasNegocioCatalogoService, clsLineasNegocioCatalogoService>();
        // HU-042.2 (#243, ADR-027): estado y corrida forzada de la sincronización del margen de SAP TM (el adaptador lo registra Infraestructura).
        servicios.AddScoped<Margen.Services.IClsSapMargenAdminService, Margen.Services.clsSapMargenAdminService>();

        // Costeo en Nexo (HU-005.5 a HU-005.14, ADR-015): envío de la solicitud y polling (el worker lo registra Infraestructura). El adaptador de Nexo lo registra Infraestructura.
        servicios.AddScoped<IClsValidadorCatalogosCaptura, clsValidadorCatalogosCaptura>();
        servicios.AddScoped<CatalogosFlete.Services.IClsResolucionAliasService, CatalogosFlete.Services.clsResolucionAliasService>();
        servicios.AddScoped<IClsEnvioCosteoService, clsEnvioCosteoService>();
        servicios.AddScoped<IClsPollingCosteoService, clsPollingCosteoService>();
        // HU-033 (#199): envío a Nexo del resultado que quedó pendiente (lo llama el polling cuando ve la entrega).
        servicios.AddScoped<IClsSincronizacionResultadoNexoService, clsSincronizacionResultadoNexoService>();
        servicios.AddSingleton<IClsPrecioCosteoService, clsPrecioCosteoService>();
        // #181: estado y motivo pendiente de la línea de Nexo de cada camión, en memoria (los llena el polling; no van a la base).
        servicios.AddSingleton<IClsSituacionLineasNexo, clsSituacionLineasNexoEnMemoria>();
        servicios.AddScoped<IClsCosteoCotizacionService, clsCosteoCotizacionService>();
        servicios.AddScoped<IClsAjusteMargenCosteoService, clsAjusteMargenCosteoService>();
        // HU-023.3 (#158): recalcular los camiones de una ruta con el cubicador antes de enviarla a Nexo.
        servicios.AddScoped<IClsRecalculoVehiculosService, clsRecalculoVehiculosService>();
        servicios.AddScoped<IClsServiciosConexosCosteoService, clsServiciosConexosCosteoService>();
        // HU-041.1 (#231, ADR-026): margen y conexos para varias rutas en una operación.
        servicios.AddScoped<IClsCosteoLineasService, clsCosteoLineasService>();
        servicios.AddScoped<IClsReSolicitudCosteoService, clsReSolicitudCosteoService>();
        // HU-028 (#186): editar la solicitud (GET y PUT /web/cotizaciones/{folio}/solicitud).
        servicios.AddScoped<IClsEdicionSolicitudService, clsEdicionSolicitudService>();

        // Decisión 10 (HU-021.9): envío diferido de los conexos de las rutas a la cotización de Nexo, desde el polling.
        servicios.AddScoped<IClsEnvioConexosNexoService, clsEnvioConexosNexoService>();
        servicios.AddScoped<IClsResolutorConexosCatalogo, clsResolutorConexosCatalogo>();

        // #173: categoría de los conexos del costeo según el catálogo de Nexo (índice en memoria de 30 min).
        servicios.AddSingleton<clsAlmacenCategoriasConexos>();
        servicios.AddScoped<IClsCategoriasConexos, clsCategoriasConexosService>();

        // #173: capturas con Idempotency-Key registradas antes de llamar a Nexo (el reintento nunca duplica la solicitud). #191: el
        // registro (IClsRegistroCapturasIdempotentes) lo registra Infraestructura sobre la tabla del 06, junto con el almacén de la clave.

        // Captura por rutas (HU-021.6): validación común al formulario y a la licitación y el punto de extensión de los camiones. El
        // planificador (HU-021.5, #133) crea un camión por fila de la recomendación del cubicador (IClsCubicador, lo registra
        // Infraestructura) y, sin cubicador, si falla, si no selecciona vehículo o si la ruta no tiene mercancías cubicables, un camión por
        // ruta con el vehículo elegido por el comercial o, sin él, sin vehículo (respaldo, HU-032 #197). Scoped: recuerda en la petición
        // que el cubicador falló. TryAdd: un planificador registrado antes (pruebas) se respeta.
        servicios.AddScoped<IClsCapturaRutasService, clsCapturaRutasService>();
        servicios.TryAddScoped<Costeo.Interfaces.IClsPlanificadorCamiones, clsPlanificadorCamionesCubicador>();

        // "Calcular cubicaje" (HU-023.2, #157): vista previa contra el cubicador (POST /recomendaciones con PRUEBA-<guid>), sin persistencia.
        servicios.AddScoped<Cubicacion.Interfaces.IClsCalculoCubicajeService, Cubicacion.Services.clsCalculoCubicajeService>();

        // Asunto e introducción del PDF (HU-013.5, ADR-018 §2a): la IA redacta con el texto estándar de respaldo; el redactor
        // (IClsRedactorCotizacion, sobre IChatClient) lo registra Infraestructura.
        servicios.AddScoped<Redaccion.Services.IClsRedaccionCotizacionService, Redaccion.Services.clsRedaccionCotizacionService>();
        servicios.AddScoped<IClsTextosCosteoService, clsTextosCosteoService>();
        servicios.AddScoped<IClsResultadoCotizacionService, clsResultadoCotizacionService>();
        // HU-036.2 (#206): finalizar la cotización (POST /web/cotizaciones/{folio}/finalizar).
        servicios.AddScoped<IClsFinalizacionCotizacionService, clsFinalizacionCotizacionService>();

        // Licitaciones desde Excel (HU-011, ADR-017).
        servicios.AddScoped<Licitaciones.Services.IClsLicitacionService, Licitaciones.Services.clsLicitacionService>();
        // HU-044.5 (#256): plantillas de mapeo, formatos de lectura y diccionario del cliente, puente hacia NEXO (el adaptador lo registra
        // Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsFormatosClienteService, Licitaciones.Services.clsFormatosClienteService>();
        // HU-023.5 (#160): cubicación de la licitación en segundo plano (el procesador lo registra Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsCubicacionLicitacionService, Licitaciones.Services.clsCubicacionLicitacionService>();
        servicios.AddScoped<Licitaciones.Services.IClsValidadorFilasLicitacion, Licitaciones.Services.clsValidadorFilasLicitacion>();

        // HU-035.1 (#202, ADR-024): Excel de mercancías de una ruta, leído localmente sin Nexo (el lector lo registra Infraestructura).
        servicios.AddScoped<Mercancias.Services.IClsAnalisisExcelMercanciasService, Mercancias.Services.clsAnalisisExcelMercanciasService>();

        // Tablero de métricas agregadas (HU-010.1, ADR-016) y regional del usuario (DimEjecutivo o UsuariosWeb.regional).
        servicios.AddScoped<IClsRegionalUsuario, clsRegionalUsuarioService>();
        // HU-037.3 (#211): GET /web/regionales (selector del director y del gerente).
        servicios.AddScoped<IClsRegionalesService, clsRegionalesService>();
        servicios.AddScoped<IClsTableroMetricasService, clsTableroMetricasService>();

        // Asistente por folio (HU-013.6, #102, ADR-018 §2b; único agente desde HU-013.9, #105): loop manual sobre IChatClient + Kernel de
        // Semantic Kernel con el plugin "asistente" (solo lectura y propuesta sobre la cotización de la conversación). El IChatClient
        // (proveedor por IA:Proveedor) lo registra Infraestructura. La respuesta final pasa por el validador con JSON Schema, validación
        // manual, un reintento y respaldo (HU-003.10, #16; TryAdd: las pruebas pueden cambiarlo).
        servicios.AddMetrics(); // IMeterFactory del medidor "Cotifly.Agente" (idempotente; el host web ya lo registra).
        servicios.TryAddSingleton<clsInterruptorJsonSchema>();
        servicios.TryAddSingleton<IClsValidadorRespuestaAgente, clsValidadorRespuestaAgente>();
        servicios.AddScoped<clsContextoAsistenteFolio>();
        servicios.AddScoped<clsHerramientasAsistenteFolio>();
        servicios.AddScoped<IClsAsistenteFolioRunner, clsAsistenteFolioRunner>();

        return servicios;
    }
}
