using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Precio.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aplicacion;

/// <summary>
/// Registro de los servicios de la capa de aplicación (casos de uso y validadores).
/// Cada módulo agrega aquí sus servicios a medida que se implementa. Los puertos que usan
/// (repositorios, fuente de margen, proveedor de tarifas) y las opciones los registra Infraestructura.
/// </summary>
public static class clsDependenciasAplicacion
{
    public static IServiceCollection AddAplicacion(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        // Precio y Margen (HU-003.4, #10).
        servicios.AddSingleton<IClsPrecioService, clsPrecioService>();
        servicios.AddScoped<IClsMargenResolverService, clsMargenResolverService>();
        servicios.AddScoped<IClsAjusteMargenService, clsAjusteMargenService>();

        return servicios;
    }
}
