using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Avisos al arrancar sobre <c>Captura:ClasesSinVehiculoObligatorio</c>. La lista configurada
/// REEMPLAZA a la de por defecto (solo Contenedor), así que un ambiente que configura solo <c>__0=Carga Suelta</c> pasa a exigir el
/// vehículo elegido en Contenedor. No impide arrancar (puede ser intencional), pero deja un warning con código estable
/// (<see cref="CodigoContenedorExigeVehiculo"/>, EventId <see cref="EventoContenedorExigeVehiculo"/>) para que operación lo vea.
/// </summary>
public sealed partial class clsAvisosArranqueCaptura(IOptions<clsOpcionesCaptura> opciones, ILogger<clsAvisosArranqueCaptura> logger)
    : IHostedService
{
    /// <summary>Código estable del aviso (va al inicio del mensaje y como <c>EventName</c>).</summary>
    public const string CodigoContenedorExigeVehiculo = "CAPTURA_CONTENEDOR_EXIGE_VEHICULO";

    /// <summary>EventId estable del aviso.</summary>
    public const int EventoContenedorExigeVehiculo = 2120;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configuradas = opciones.Value.ClasesSinVehiculoConfiguradas();
        if (configuradas.Count > 0 && !configuradas.Contains(clsCatalogosCaptura.ClaseViajeContenedor, StringComparer.Ordinal))
        {
            LogContenedorExigeVehiculo(logger, string.Join(", ", configuradas));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = EventoContenedorExigeVehiculo, EventName = CodigoContenedorExigeVehiculo, Level = LogLevel.Warning,
        Message = "CAPTURA_CONTENEDOR_EXIGE_VEHICULO: Captura:ClasesSinVehiculoObligatorio = [{Clases}] no incluye Contenedor: la lista "
            + "reemplaza a la de por defecto y las rutas de Contenedor sin mercancías cubicables exigirán el vehículo elegido "
            + "(VEHICULO_REQUERIDO). Si no es intencional, agregue Contenedor a la lista.")]
    private static partial void LogContenedorExigeVehiculo(ILogger logger, string clases);
}
