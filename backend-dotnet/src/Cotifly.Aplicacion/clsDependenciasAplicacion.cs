using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Correo.Services;
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
        servicios.AddScoped<IClsMargenResolverService, clsMargenResolverService>();
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
        servicios.AddScoped<IClsDestinatariosPreviosService, clsDestinatariosPreviosService>();
        servicios.AddScoped<IClsTelefonoComercialService, clsTelefonoComercialService>();
        servicios.AddScoped<IClsSeguimientoService, clsSeguimientoService>();

        // Login SSO ClientePro con verificación doble, refresh y logout (HU-003.6, #12). Cada intento se audita.
        servicios.AddScoped<clsVerificadorIdentidad>();
        servicios.AddScoped<IClsVerificadorIdentidad>(sp => sp.GetRequiredService<clsVerificadorIdentidad>());
        servicios.AddScoped<IClsLoginClienteProService, clsLoginClienteProService>();
        servicios.AddScoped<IClsRefreshTokenService, clsRefreshTokenService>();
        servicios.AddScoped<IClsLogoutService, clsLogoutService>();
        servicios.AddSingleton<IClsRevocacionAccesos, clsCacheTokensRevocados>();

        // API web /web/* (HU-003.6, #12 parte B): mis cotizaciones, nueva versión, folio del servidor (ADR-011), descarga
        // autenticada del PDF, administración de líneas (HU-004.9) y métricas en borrador (HU-010.1).
        servicios.AddScoped<IClsMisCotizacionesService, clsMisCotizacionesService>();
        servicios.AddScoped<IClsNuevaVersionService, clsNuevaVersionService>();
        servicios.AddScoped<IClsGeneradorFolioService, clsGeneradorFolioService>();
        servicios.AddScoped<IClsDescargaPdfService, clsDescargaPdfService>();
        servicios.AddScoped<IClsLineasNegocioAdminService, clsLineasNegocioAdminService>();
        servicios.AddScoped<IClsMetricasService, clsMetricasService>();

        // Costeo en Nexo (HU-005.5 a HU-005.14, ADR-015): envío de la solicitud. El adaptador de Nexo lo registra Infraestructura.
        servicios.AddScoped<IClsEnvioCosteoService, clsEnvioCosteoService>();

        // Agente (HU-003.6, #12 parte C, ADR-003/ADR-004): runner con loop manual sobre IChatClient + Kernel de Semantic Kernel,
        // herramientas del plugin "cotizador", estado de la conversación, procesamiento de Google Chat y URL firmada del PDF.
        // El IChatClient (proveedor por IA:Proveedor) lo registra Infraestructura. La respuesta final pasa por el validador con
        // JSON Schema, validación manual, un reintento y respaldo (HU-003.10, #16; TryAdd: las pruebas pueden cambiarlo).
        servicios.AddScoped<clsContextoTurnoAgente>();
        servicios.AddScoped<clsHerramientasCotizador>();
        servicios.AddSingleton<IClsEstadoConversacionService, clsEstadoConversacionService>();
        servicios.AddMetrics(); // IMeterFactory del medidor "Cotifly.Agente" (idempotente; el host web ya lo registra).
        servicios.TryAddSingleton<IClsValidadorRespuestaAgente, clsValidadorRespuestaAgente>();
        servicios.AddScoped<IClsAgenteRunner, clsAgenteRunner>();
        servicios.AddScoped<IClsProcesadorEventoChat, clsProcesadorEventoChat>();
        servicios.AddScoped<IClsDescargaPdfFirmadaService, clsDescargaPdfFirmadaService>();

        return servicios;
    }
}
