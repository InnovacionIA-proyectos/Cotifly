using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Elige el adaptador del costeo según <c>Nexo:UrlBase</c> (ADR-015): configurada → <see cref="clsCosteoNexo"/> (cliente tipado
/// <see cref="clsCosteoNexo.NombreCliente"/>); vacía → <see cref="clsCosteoNexoNoDisponible"/>. Mismo patrón que
/// <c>clsFabricaBuscadorClientes</c>.
/// </summary>
internal static class clsFabricaCosteoNexo
{
    public static IClsCosteoNexo Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsCosteoNexo>()
            : servicios.GetRequiredService<clsCosteoNexoNoDisponible>();
    }
}
