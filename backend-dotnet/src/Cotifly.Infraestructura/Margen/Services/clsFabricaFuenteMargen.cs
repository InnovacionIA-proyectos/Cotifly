using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Elige la fuente SAP TM según <c>SapTm:Modo</c> (port de <c>construir_resolver</c>, <c>mcp_server/margen/servicio.py</c>):
/// <c>odata</c> → <see cref="clsFuenteMargenOData"/> (cliente tipado, transitorio); <c>export_json</c> →
/// <see cref="clsFuenteMargenExportJson"/> (singleton, conserva su caché); vacío → <see cref="clsFuenteMargenNoConfigurada"/>.
/// El Python caía a <c>export_json</c> con cualquier modo distinto de <c>odata</c>; aquí un modo desconocido no pasa la
/// validación al arrancar (<see cref="clsValidadorOpcionesSapTm"/>).
/// </summary>
internal static class clsFabricaFuenteMargen
{
    public static IClsFuenteMargen Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        return servicios.GetRequiredService<IOptions<clsOpcionesSapTm>>().Value.ModoNormalizado switch
        {
            clsOpcionesSapTm.ModoOData => servicios.GetRequiredService<clsFuenteMargenOData>(),
            clsOpcionesSapTm.ModoExportJson => servicios.GetRequiredService<clsFuenteMargenExportJson>(),
            _ => servicios.GetRequiredService<clsFuenteMargenNoConfigurada>(),
        };
    }
}
