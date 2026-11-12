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
/// Cada módulo registra aquí sus servicios. Los puertos que usan
/// (repositorios, fuente de margen, proveedor de tarifas, generador y almacén de PDF, transporte de correo) y las
/// opciones los registra Infraestructura.
/// </summary>
public static class clsDependenciasAplicacion
{
    public static IServiceCollection AddAplicacion(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        // Auditoría transversal: singleton que abre su propio ámbito por evento.
        servicios.AddSingleton<IClsAuditoriaService, clsAuditoriaService>();

        // Precio y margen. El ajuste negociado se audita.
        servicios.AddSingleton<IClsPrecioService, clsPrecioService>();
        servicios.AddSingleton<Cotizaciones.Services.IClsFijacionMonedaService, Cotizaciones.Services.clsFijacionMonedaService>();
        servicios.AddScoped<IClsMargenResolverService, clsMargenResolverService>();
        // Aviso de la copia de SAP TM vieja en aviso_margen del costeo.
        servicios.AddScoped<IClsAvisoSincronizacionMargenSap, clsAvisoSincronizacionMargenSap>();
        servicios.AddScoped<clsAjusteMargenService>();
        servicios.AddScoped<IClsAjusteMargenService>(sp => new clsAjusteMargenServiceAuditado(
            sp.GetRequiredService<clsAjusteMargenService>(), sp.GetRequiredService<IClsAuditoriaService>()));

        // PDF, correo, teléfono y seguimiento. Generar PDF y enviar correo se auditan con decoradores.
        servicios.AddScoped<clsGenerarPdfService>();
        servicios.AddScoped<IClsGenerarPdfService>(sp => new clsGenerarPdfServiceAuditado(
            sp.GetRequiredService<clsGenerarPdfService>(), sp.GetRequiredService<IClsAuditoriaService>()));
        servicios.AddSingleton<clsCandadoEnvios>();
        servicios.AddScoped<clsCorreoService>();
        servicios.AddScoped<IClsCorreoService>(sp => new clsCorreoServiceAuditado(
            sp.GetRequiredService<clsCorreoService>(), sp.GetRequiredService<IClsAuditoriaService>()));
        servicios.AddScoped<IClsVistaPreviaCorreoService, clsVistaPreviaCorreoService>();
        // Historial y reintento de los envíos, y la consulta del estado en MailSender (worker de correos).
        servicios.AddScoped<IClsHistorialCorreosService, clsHistorialCorreosService>();
        servicios.AddScoped<IClsSeguimientoCorreosService, clsSeguimientoCorreosService>();
        servicios.AddScoped<IClsDestinatariosPreviosService, clsDestinatariosPreviosService>();
        servicios.AddScoped<IClsTelefonoComercialService, clsTelefonoComercialService>();
        // Lista de motivos de pérdida (Resultado:MotivosPerdida) para /resultado, /seguimiento y la sincronización.
        servicios.AddSingleton<IClsCatalogoMotivosPerdida, clsCatalogoMotivosPerdida>();
        servicios.AddScoped<IClsSeguimientoService, clsSeguimientoService>();

        // Login SSO ClientePro con verificación doble, refresh y logout. Cada intento se audita.
        servicios.AddScoped<clsVerificadorIdentidad>();
        servicios.AddScoped<IClsVerificadorIdentidad>(sp => sp.GetRequiredService<clsVerificadorIdentidad>());
        servicios.AddScoped<IClsLoginClienteProService, clsLoginClienteProService>();
        // Elegir el rol al entrar (multi-rol), con el ticket de un solo uso del login.
        servicios.AddScoped<IClsEleccionRolService, clsEleccionRolService>();
        servicios.AddScoped<IClsRefreshTokenService, clsRefreshTokenService>();
        servicios.AddScoped<IClsLogoutService, clsLogoutService>();
        servicios.AddSingleton<IClsRevocacionAccesos, clsCacheTokensRevocados>();

        // API web /web/*: mis cotizaciones, nueva versión, folio del servidor (ADR-011), descarga
        // autenticada del PDF y administración de líneas.
        servicios.AddScoped<IClsMisCotizacionesService, clsMisCotizacionesService>();
        servicios.AddScoped<IClsCotizacionesRegionalService, clsCotizacionesRegionalService>();
        // Detalle por folio (dueño, o gerente de su regional en solo consulta).
        servicios.AddScoped<IClsDetalleCotizacionService, clsDetalleCotizacionService>();
        // La licitación ajena en solo lectura (gerente de la regional o director), con la regla del detalle.
        servicios.AddScoped<IClsAccesoCotizacionAjena, clsAccesoCotizacionAjenaService>();
        // Excel de la cotización (dueño: versión interna; gerente y director: sin costos ni márgenes).
        servicios.AddScoped<IClsExportacionCotizacionService, clsExportacionCotizacionService>();
        servicios.AddScoped<IClsNuevaVersionService, clsNuevaVersionService>();
        servicios.AddScoped<IClsGeneradorFolioService, clsGeneradorFolioService>();
        servicios.AddScoped<IClsDescargaPdfService, clsDescargaPdfService>();
        servicios.AddScoped<IClsLineasNegocioAdminService, clsLineasNegocioAdminService>();
        // Catálogo de líneas activas para el desplegable de la captura (sin margen).
        servicios.AddScoped<IClsLineasNegocioCatalogoService, clsLineasNegocioCatalogoService>();
        // Estado y corrida forzada de la sincronización del margen de SAP TM (el adaptador lo registra Infraestructura).
        servicios.AddScoped<Margen.Services.IClsSapMargenAdminService, Margen.Services.clsSapMargenAdminService>();

        // Costeo en Nexo (ADR-015): envío de la solicitud y polling. El worker y el adaptador de Nexo los registra Infraestructura.
        servicios.AddScoped<IClsValidadorCatalogosCaptura, clsValidadorCatalogosCaptura>();
        servicios.AddScoped<CatalogosFlete.Services.IClsResolucionAliasService, CatalogosFlete.Services.clsResolucionAliasService>();
        servicios.AddScoped<IClsEnvioCosteoService, clsEnvioCosteoService>();
        servicios.AddScoped<IClsPollingCosteoService, clsPollingCosteoService>();
        // Envío a Nexo del resultado que quedó pendiente (lo llama el polling cuando ve la entrega).
        servicios.AddScoped<IClsSincronizacionResultadoNexoService, clsSincronizacionResultadoNexoService>();
        servicios.AddSingleton<IClsPrecioCosteoService, clsPrecioCosteoService>();
        // Estado y motivo pendiente de la línea de Nexo de cada camión, en memoria (los llena el polling; no van a la base).
        servicios.AddSingleton<IClsSituacionLineasNexo, clsSituacionLineasNexoEnMemoria>();
        servicios.AddScoped<IClsCosteoCotizacionService, clsCosteoCotizacionService>();
        servicios.AddScoped<IClsAjusteMargenCosteoService, clsAjusteMargenCosteoService>();
        // El seguro de la mercancía por ruta (PUT .../costeo/seguro).
        servicios.AddScoped<IClsSeguroCosteoService, clsSeguroCosteoService>();
        // Recalcular los camiones de una ruta con el cubicador antes de enviarla a Nexo.
        servicios.AddScoped<IClsRecalculoVehiculosService, clsRecalculoVehiculosService>();
        servicios.AddScoped<IClsServiciosConexosCosteoService, clsServiciosConexosCosteoService>();
        // Margen y conexos (ADR-026) para varias rutas en una operación.
        servicios.AddScoped<IClsCosteoLineasService, clsCosteoLineasService>();
        servicios.AddScoped<IClsReSolicitudCosteoService, clsReSolicitudCosteoService>();
        // Editar la solicitud (GET y PUT /web/cotizaciones/{folio}/solicitud).
        servicios.AddScoped<IClsEdicionSolicitudService, clsEdicionSolicitudService>();

        // Envío diferido de los conexos de las rutas a la cotización de Nexo, desde el polling.
        servicios.AddScoped<IClsEnvioConexosNexoService, clsEnvioConexosNexoService>();
        servicios.AddScoped<IClsResolutorConexosCatalogo, clsResolutorConexosCatalogo>();

        // Categoría de los conexos del costeo según el catálogo de Nexo (índice en memoria de 30 min).
        servicios.AddSingleton<clsAlmacenCategoriasConexos>();
        servicios.AddScoped<IClsCategoriasConexos, clsCategoriasConexosService>();

        // Capturas con Idempotency-Key registradas antes de llamar a Nexo (el reintento nunca duplica la solicitud). El
        // registro (IClsRegistroCapturasIdempotentes) lo registra Infraestructura sobre la tabla del script 06, junto con el almacén de la clave.

        // Captura por rutas: validación común al formulario y a la licitación y el punto de extensión de los camiones. El
        // planificador crea un camión por fila de la recomendación del cubicador (IClsCubicador, lo registra
        // Infraestructura) y, sin cubicador, si falla, si no selecciona vehículo o si la ruta no tiene mercancías cubicables, un camión por
        // ruta con el vehículo elegido por el comercial o, sin él, sin vehículo (respaldo). Scoped: recuerda en la petición
        // que el cubicador falló. TryAdd: un planificador registrado antes (pruebas) se respeta.
        servicios.AddScoped<IClsCapturaRutasService, clsCapturaRutasService>();
        servicios.TryAddScoped<Costeo.Interfaces.IClsPlanificadorCamiones, clsPlanificadorCamionesCubicador>();

        // "Calcular cubicaje": vista previa contra el cubicador (POST /recomendaciones con PRUEBA-<guid>), sin persistencia.
        servicios.AddScoped<Cubicacion.Interfaces.IClsCalculoCubicajeService, Cubicacion.Services.clsCalculoCubicajeService>();

        // Asunto e introducción del PDF (ADR-018 §2a): la IA redacta con el texto estándar de respaldo; el redactor
        // (IClsRedactorCotizacion, sobre IChatClient) lo registra Infraestructura.
        servicios.AddScoped<Redaccion.Services.IClsRedaccionCotizacionService, Redaccion.Services.clsRedaccionCotizacionService>();
        servicios.AddScoped<IClsTextosCosteoService, clsTextosCosteoService>();
        servicios.AddScoped<IClsResultadoCotizacionService, clsResultadoCotizacionService>();
        // Finalizar la cotización (POST /web/cotizaciones/{folio}/finalizar).
        servicios.AddScoped<IClsFinalizacionCotizacionService, clsFinalizacionCotizacionService>();

        // Licitaciones desde Excel (ADR-017).
        servicios.AddScoped<Licitaciones.Services.IClsLicitacionService, Licitaciones.Services.clsLicitacionService>();
        // Plantillas de mapeo, formatos de lectura y diccionario del cliente, puente hacia NEXO (el adaptador lo registra
        // Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsFormatosClienteService, Licitaciones.Services.clsFormatosClienteService>();
        // Cubicación de la licitación en segundo plano (el procesador lo registra Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsCubicacionLicitacionService, Licitaciones.Services.clsCubicacionLicitacionService>();
        servicios.AddScoped<Licitaciones.Services.IClsValidadorFilasLicitacion, Licitaciones.Services.clsValidadorFilasLicitacion>();
        // Columnas derivadas (vehículo dentro del texto, contenedor con peso o rango) que se agregan antes de sugerir-mapeo y
        // aplicar-mapeo, con el diccionario de alias de NEXO (el mismo de /web/alias/resolver).
        servicios.AddScoped<Licitaciones.Services.IClsColumnasDerivadasService, Licitaciones.Services.clsColumnasDerivadasService>();
        // Sugerir-mapeo y leer-estructura, puente hacia la importación inteligente de NEXO (el adaptador lo registra Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsSugerenciaMapeoService, Licitaciones.Services.clsSugerenciaMapeoService>();

        // Excel de mercancías (ADR-024) de una ruta, leído localmente sin Nexo (el lector lo registra Infraestructura).
        servicios.AddScoped<Mercancias.Services.IClsAnalisisExcelMercanciasService, Mercancias.Services.clsAnalisisExcelMercanciasService>();

        // POST /web/licitaciones/normalizar-valores con NEXO (ADR-029; el puerto de la importación lo registra Infraestructura).
        servicios.AddScoped<Licitaciones.Services.IClsNormalizacionValoresService, Licitaciones.Services.clsNormalizacionValoresService>();

        // POST /web/licitaciones/aplicar-mapeo (ADR-028 y ADR-029; el puerto de la importación y el cubicador los registra
        // Infraestructura; el cubicador es opcional: sin él las mercancías sin código quedan sin sugerencia).
        servicios.AddScoped<Licitaciones.Services.IClsAplicacionMapeoService, Licitaciones.Services.clsAplicacionMapeoService>();

        // Tablero de métricas agregadas (ADR-016) y regional del usuario (DimEjecutivo o UsuariosWeb.regional).
        servicios.AddScoped<IClsRegionalUsuario, clsRegionalUsuarioService>();
        // GET /web/regionales (selector del director y del gerente).
        servicios.AddScoped<IClsRegionalesService, clsRegionalesService>();
        servicios.AddScoped<IClsTableroMetricasService, clsTableroMetricasService>();

        // Asistente por folio (ADR-018 §2b; único agente): loop manual sobre IChatClient + Kernel de
        // Semantic Kernel con el plugin "asistente" (solo lectura y propuesta sobre la cotización de la conversación). El IChatClient
        // (proveedor por IA:Proveedor) lo registra Infraestructura. La respuesta final pasa por el validador con JSON Schema, validación
        // manual, un reintento y respaldo (TryAdd: las pruebas pueden cambiarlo).
        servicios.AddMetrics(); // IMeterFactory del medidor "Cotifly.Agente" (idempotente; el host web ya lo registra).
        servicios.TryAddSingleton<clsInterruptorJsonSchema>();
        servicios.TryAddSingleton<IClsValidadorRespuestaAgente, clsValidadorRespuestaAgente>();
        servicios.AddScoped<clsContextoAsistenteFolio>();
        servicios.AddScoped<clsHerramientasAsistenteFolio>();
        servicios.AddScoped<IClsAsistenteFolioRunner, clsAsistenteFolioRunner>();

        return servicios;
    }
}
