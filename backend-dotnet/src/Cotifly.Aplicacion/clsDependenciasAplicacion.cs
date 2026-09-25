using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Microsoft.Extensions.DependencyInjection;

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

        return servicios;
    }
}
