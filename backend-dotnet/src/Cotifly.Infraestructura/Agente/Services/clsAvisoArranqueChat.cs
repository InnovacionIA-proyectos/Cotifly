using Cotifly.Aplicacion.Agente.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// Aviso al arrancar (HU-013.6, #102, ADR-018 §1): <c>Chat:CreacionHabilitada=true</c> en Production reactiva la creación de
/// cotizaciones desde Google Chat y desde el chat libre (sin <c>folio</c>). Solo es válido durante la transición del frontend: no
/// impide arrancar, pero operación debe verlo.
/// </summary>
public sealed class clsAvisoArranqueChat(IOptions<clsOpcionesChat> opciones, IHostEnvironment entorno, ILogger<clsAvisoArranqueChat> logger) : IHostedService
{
    public const string AvisoCreacionHabilitada =
        "Chat:CreacionHabilitada=true en Production: se reactiva la creación de cotizaciones desde Google Chat y desde el chat sin folio (agente legado). Úsalo solo durante la transición del frontend y vuelve a false.";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (entorno.IsProduction() && opciones.Value.CreacionHabilitada)
        {
            logger.LogWarning(AvisoCreacionHabilitada);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
