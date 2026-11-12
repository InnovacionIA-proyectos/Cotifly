using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Elige el adaptador del cubicador según <c>Cubicador:UrlBase</c>: configurada → <see cref="clsCubicadorHttp"/> (cliente
/// tipado <see cref="clsCubicadorHttp.NombreCliente"/>); vacía → <see cref="clsCubicadorNoDisponible"/>. Mismo patrón que
/// <c>clsFabricaCosteoNexo</c>.
/// </summary>
internal static class clsFabricaCubicador
{
    public static IClsCubicador Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsCubicadorHttp>()
            : servicios.GetRequiredService<clsCubicadorNoDisponible>();
    }
}
