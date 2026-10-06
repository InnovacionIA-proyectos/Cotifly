using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Elige el proveedor de catálogos de flete según <c>Nexo:UrlBase</c>: configurada → <see cref="clsCatalogosFleteNexo"/> (sobre los
/// catálogos maestros); vacía → <see cref="clsCatalogosFleteNoDisponible"/>. Mismo patrón que <c>clsFabricaBuscadorClientes</c>.
/// </summary>
internal static class clsFabricaCatalogosFlete
{
    public static IClsCatalogosFleteProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsCatalogosFleteNexo>()
            : servicios.GetRequiredService<clsCatalogosFleteNoDisponible>();
    }
}
