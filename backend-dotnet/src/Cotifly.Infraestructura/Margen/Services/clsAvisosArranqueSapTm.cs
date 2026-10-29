using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Avisos al arrancar sobre SAP TM (HU-042.2, #243, ADR-027). Desde que el margen sale de la copia local que sincroniza Datos, las
/// variables del modo en vivo de #13 (<c>SapTm__Modo</c>, <c>Url</c>, <c>User</c>, <c>Pass</c>, <c>SapClient</c>, <c>ExportPath</c>) ya no
/// se leen. Un <c>.env</c> viejo que todavía las traiga NO impide arrancar: se registra un warning por variable ("variable obsoleta, se
/// ignora") con su nombre, nunca su valor (puede ser la clave del usuario de servicio de SAP).
/// </summary>
public sealed class clsAvisosArranqueSapTm(IReadOnlyList<string> obsoletas, ILogger<clsAvisosArranqueSapTm> logger) : IHostedService
{
    /// <summary>Claves retiradas de la sección <c>SapTm</c> (las del modo <c>export_json</c>/<c>odata</c> de #13).</summary>
    public static readonly IReadOnlyList<string> ClavesRetiradas = ["Modo", "Url", "User", "Pass", "SapClient", "ExportPath"];

    /// <summary>
    /// Variables retiradas presentes en la configuración con algún valor, como variable de entorno (<c>SapTm__Pass</c>). Una variable
    /// declarada vacía (<c>SapTm__Modo=</c>, como dejaban los manifiestos anteriores) no se reporta: no cambia nada.
    /// </summary>
    public static IReadOnlyList<string> VariablesObsoletas(IConfiguration configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        var seccion = configuracion.GetSection(Aplicacion.Margen.Services.clsOpcionesMargen.Seccion);
        return [.. ClavesRetiradas.Where(clave => !string.IsNullOrWhiteSpace(seccion[clave])).Select(clave => $"{seccion.Key}__{clave}")];
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var variable in obsoletas)
        {
            logger.LogWarning(
                "{Variable}: variable obsoleta, se ignora. El margen de SAP TM sale de la copia local que sincroniza Datos (HU-042.2); "
                + "Cotifly ya no se conecta a SAP. Quítela de la configuración.",
                variable);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
