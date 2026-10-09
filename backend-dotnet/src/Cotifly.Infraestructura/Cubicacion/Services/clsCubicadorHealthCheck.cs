using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Sonda <c>cubicador</c> de <c>GET /health/ready</c> (HU-023.1, #156; petición P7). Sin <c>Cubicador:UrlBase</c> o sin
/// <c>Cubicador:RutaSalud</c> no consulta nada (sano). Con las dos, hace un GET liviano a la ruta de salud del cubicador:
/// <list type="bullet">
/// <item>responde → sano;</item>
/// <item>no responde y <c>Cubicador:RespaldoSiFalla = false</c> → no sano (sin cubicador la captura falla: Cotifly no está listo);</item>
/// <item>no responde con el respaldo encendido → sano con un warning (la captura sigue con camiones sin vehículo).</item>
/// </list>
/// La descripción nunca incluye la URL ni la llave.
/// </summary>
public sealed class clsCubicadorHealthCheck(
    IServiceProvider servicios, IOptions<clsOpcionesCubicador> opciones, ILogger<clsCubicadorHealthCheck> logger) : IHealthCheck
{
    public const string Nombre = "cubicador";
    public const string DescripcionSinConsultar = "El cubicador no se consulta (sin Cubicador:UrlBase o sin Cubicador:RutaSalud).";
    public const string DescripcionDisponible = "El cubicador responde.";
    public const string DescripcionNoDisponible = "El cubicador no responde y Cubicador:RespaldoSiFalla = false.";
    public const string DescripcionRespaldo = "El cubicador no responde: la captura usa el respaldo (camiones sin vehículo).";

    /// <summary>Tiempo máximo de la sonda.</summary>
    public static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var valor = opciones.Value;
        if (string.IsNullOrWhiteSpace(valor.UrlBase) || string.IsNullOrWhiteSpace(valor.RutaSalud))
        {
            return HealthCheckResult.Healthy(DescripcionSinConsultar);
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TiempoMaximo);
        string? motivo;
        try
        {
            motivo = await servicios.GetRequiredService<clsCubicadorHttp>().VerificarSaludAsync(limite.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            motivo = "tiempo agotado";
        }

        if (motivo is null)
        {
            return HealthCheckResult.Healthy(DescripcionDisponible);
        }

        logger.LogWarning("La sonda del cubicador falló ({Motivo}).", motivo);
        return valor.RespaldoSiFalla ? HealthCheckResult.Healthy(DescripcionRespaldo) : HealthCheckResult.Unhealthy(DescripcionNoDisponible);
    }
}
