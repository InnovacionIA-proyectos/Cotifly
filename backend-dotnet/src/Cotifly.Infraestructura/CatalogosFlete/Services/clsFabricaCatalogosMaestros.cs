using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Elige el proveedor de catálogos maestros según <c>Nexo:UrlBase</c>: configurada → <see cref="clsCatalogosMaestrosNexo"/>
/// (cliente tipado, transitorio) envuelto en <see cref="clsCatalogosMaestrosEnCache"/> si <c>Nexo:MinutosCacheCatalogos</c> es mayor que
/// cero; vacía → <see cref="clsCatalogosMaestrosNoDisponible"/>.
/// </summary>
internal static class clsFabricaCatalogosMaestros
{
    public static IClsCatalogosMaestrosProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var opciones = servicios.GetRequiredService<IOptions<clsOpcionesNexo>>();
        if (string.IsNullOrWhiteSpace(opciones.Value.UrlBase))
        {
            return servicios.GetRequiredService<clsCatalogosMaestrosNoDisponible>();
        }

        var nexo = servicios.GetRequiredService<clsCatalogosMaestrosNexo>();
        return opciones.Value.MinutosCacheCatalogos > 0
            ? new clsCatalogosMaestrosEnCache(
                nexo,
                servicios.GetRequiredService<clsCacheCatalogosMaestros>(),
                servicios.GetRequiredService<IClsReloj>(),
                opciones,
                servicios.GetRequiredService<ILogger<clsCatalogosMaestrosEnCache>>())
            : nexo;
    }
}
