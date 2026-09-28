using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Elige el proveedor de municipios según <c>Nexo:UrlBase</c>: configurada → <see cref="clsMunicipiosNexo"/> (cliente
/// tipado, transitorio), envuelto en <see cref="clsMunicipiosEnCache"/> si <c>Nexo:MinutosCacheMunicipios</c> es mayor que cero
/// (HU-005.13); vacía → <see cref="clsMunicipiosNoDisponible"/>. Mismo patrón que <c>clsFabricaBuscadorClientes</c>.
/// </summary>
internal static class clsFabricaMunicipios
{
    public static IClsMunicipiosProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var opciones = servicios.GetRequiredService<IOptions<clsOpcionesNexo>>();
        if (string.IsNullOrWhiteSpace(opciones.Value.UrlBase))
        {
            return servicios.GetRequiredService<clsMunicipiosNoDisponible>();
        }

        var nexo = servicios.GetRequiredService<clsMunicipiosNexo>();
        return opciones.Value.MinutosCacheMunicipios > 0
            ? new clsMunicipiosEnCache(nexo, servicios.GetRequiredService<clsCacheCatalogoMunicipios>(), servicios.GetRequiredService<IClsReloj>(), opciones)
            : nexo;
    }
}
