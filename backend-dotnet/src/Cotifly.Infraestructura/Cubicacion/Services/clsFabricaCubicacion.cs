using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Elige el proveedor de cubicación según <c>Nexo:UrlBase</c>: configurada → <see cref="clsCubicacionNexo"/> (cliente
/// tipado, transitorio); vacía → <see cref="clsCubicacionNoDisponible"/>. Mismo patrón que
/// <c>clsFabricaCatalogosFlete</c>/<c>clsFabricaMunicipios</c>.
/// </summary>
internal static class clsFabricaCubicacion
{
    public static IClsCubicacionProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsCubicacionNexo>()
            : servicios.GetRequiredService<clsCubicacionNoDisponible>();
    }
}
