using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Elige el proveedor de municipios según <c>Nexo:UrlBase</c>: configurada → <see cref="clsMunicipiosNexo"/> (cliente
/// tipado, transitorio); vacía → <see cref="clsMunicipiosNoDisponible"/>. Mismo patrón que
/// <c>clsFabricaBuscadorClientes</c>.
/// </summary>
internal static class clsFabricaMunicipios
{
    public static IClsMunicipiosProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsMunicipiosNexo>()
            : servicios.GetRequiredService<clsMunicipiosNoDisponible>();
    }
}
