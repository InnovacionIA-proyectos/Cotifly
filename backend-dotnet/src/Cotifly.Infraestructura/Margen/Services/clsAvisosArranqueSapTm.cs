using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Avisos al arrancar sobre SAP TM (ADR-027). Las variables retiradas de la sección <c>SapTm</c>
/// (<c>SapTm__Modo</c> y <c>SapTm__ExportPath</c>) no se leen: un <c>.env</c> que todavía las traiga NO impide arrancar, se registra un
/// warning por variable ("variable obsoleta, se ignora") con su nombre, nunca su valor. <c>SapTm__Url</c>, <c>User</c>, <c>Pass</c> y
/// <c>SapClient</c> los usa la sincronización que hace el backend (<see cref="clsWorkerSincronizacionMargenSap"/>).
/// </summary>
public sealed class clsAvisosArranqueSapTm(IReadOnlyList<string> obsoletas, ILogger<clsAvisosArranqueSapTm> logger) : IHostedService
{
    /// <summary>Claves retiradas de la sección <c>SapTm</c> (las de los modos <c>export_json</c>/<c>odata</c>).</summary>
    public static readonly IReadOnlyList<string> ClavesRetiradas = ["Modo", "ExportPath"];

    /// <summary>
    /// Variables retiradas presentes en la configuración con algún valor, como variable de entorno (<c>SapTm__Modo</c>). Una variable
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
                "{Variable}: variable obsoleta, se ignora. El margen de SAP TM sale de la copia local que sincroniza el backend con SapTm__Url, "
                + "SapTm__User y SapTm__Pass (HU-042.2); ya no hay modos. Quítela de la configuración.",
                variable);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
