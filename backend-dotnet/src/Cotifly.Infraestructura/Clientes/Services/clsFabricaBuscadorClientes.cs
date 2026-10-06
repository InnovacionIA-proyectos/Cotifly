using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Clientes.Services;

/// <summary>
/// Elige el buscador de clientes según <c>Nexo:UrlBase</c> (HU-005.2): configurada → <see cref="clsBuscadorClientesNexo"/>
/// (cliente tipado, transitorio); vacía → <see cref="clsBuscadorClientesNoDisponible"/>. Mismo patrón que
/// <c>clsFabricaFuenteMargen</c> para la fuente de margen SAP TM.
/// </summary>
internal static class clsFabricaBuscadorClientes
{
    public static IClsBuscadorClientes Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase);
        return configurada
            ? servicios.GetRequiredService<clsBuscadorClientesNexo>()
            : servicios.GetRequiredService<clsBuscadorClientesNoDisponible>();
    }
}
