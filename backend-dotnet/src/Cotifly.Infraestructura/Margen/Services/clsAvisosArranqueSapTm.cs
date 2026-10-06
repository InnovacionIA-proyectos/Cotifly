using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Avisos al arrancar sobre la fuente SAP TM (#13). No impiden arrancar: son situaciones válidas pero que operación debe ver.
/// <list type="bullet">
/// <item>Production con <c>SapTm:Modo</c> vacío: todos los márgenes salen de la política de la línea.</item>
/// <item><c>export_json</c> con un archivo que todavía no existe: se admite porque el adaptador lo recoge en caliente
/// cuando aparece, pero mientras tanto cada cotización con BP cae a la política ("SAP TM no disponible").</item>
/// </list>
/// </summary>
public sealed class clsAvisosArranqueSapTm(
    IOptions<clsOpcionesSapTm> opciones, IHostEnvironment entorno, ILogger<clsAvisosArranqueSapTm> logger) : IHostedService
{
    public const string AvisoNoConfigurado =
        "SAP TM no configurado (SapTm:Modo vacío): todos los márgenes salen de la política de la línea.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var sapTm = opciones.Value;
        switch (sapTm.ModoNormalizado)
        {
            case "" when entorno.IsProduction():
                logger.LogWarning(AvisoNoConfigurado);
                break;
            case clsOpcionesSapTm.ModoExportJson when !File.Exists(sapTm.ExportPath!.Trim()):
                logger.LogWarning(
                    "El export de SAP TM no existe en {Ruta} (SapTm:ExportPath): hasta que aparezca, los márgenes salen de la política de la línea.",
                    sapTm.ExportPath.Trim());
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
