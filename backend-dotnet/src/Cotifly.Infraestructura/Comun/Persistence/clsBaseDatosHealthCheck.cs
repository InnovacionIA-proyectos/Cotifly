using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Readiness de la base <c>dbo_cotifly</c> para <c>GET /health/ready</c>: la cadena debe estar
/// configurada, la base debe responder y el esquema debe tener aplicado el script 0002
/// (<c>Sesiones.estadoAgente</c> y <c>TokensRefresco</c>), del que depende el modelo EF.
/// La sonda tiene un tiempo máximo propio (<see cref="TiempoMaximo"/>) para no colgar el endpoint
/// los 15 s del timeout de conexión por defecto. Las descripciones son fijas: no incluyen la cadena
/// ni el servidor; el detalle técnico queda en la excepción que registra <c>HealthCheckService</c>.
/// </summary>
public sealed class clsBaseDatosHealthCheck(clsCotiflyDbContext contexto, IOptions<clsOpcionesConexiones> opciones) : IHealthCheck
{
    public const string Nombre = "base-datos-cotifly";
    public const string DescripcionSinCadena = "La cadena de conexión 'ConnectionStrings:Cotifly' no está configurada.";
    public const string DescripcionNoDisponible = "La base de datos de Cotifly no está disponible.";
    public const string DescripcionEsquemaIncompleto = "esquema incompleto: falta 0002";

    /// <summary>El 0002 está, pero no el 0004 (costeo en Nexo, HU-005.8): el modelo EF de <c>Cotizaciones</c> ya lo exige.</summary>
    public const string DescripcionEsquemaSin0004 = "esquema incompleto: falta 0004";

    /// <summary>Está el 0004, pero no el 0005 (líneas de la solicitud y Excel de licitaciones, ADR-017): el modelo EF ya lo exige.</summary>
    public const string DescripcionEsquemaSin0005 = "esquema incompleto: falta 0005";

    /// <summary>Está el 0005, pero no el 0006 (concurrencia, textos y regla del PDF, ADR-018): el modelo EF ya lo exige.</summary>
    public const string DescripcionEsquemaSin0006 = "esquema incompleto: falta 0006";

    /// <summary>Está el 0006, pero no el 0007 (notas y municipios de la captura, HU-013.10): el modelo EF ya lo exige.</summary>
    public const string DescripcionEsquemaSin0007 = "esquema incompleto: falta 0007";

    /// <summary>Está el 0007, pero no el 0008 (horas por línea de las licitaciones, HU-013.11): el modelo EF ya lo exige.</summary>
    public const string DescripcionEsquemaSin0008 = "esquema incompleto: falta 0008";
    public const string DescripcionDisponible = "La base de datos de Cotifly responde.";

    /// <summary>Tiempo máximo de la sonda completa (conexión + verificación de esquema).</summary>
    public static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(5);

    /// <summary>0 = falta el 0002; 1 = falta el 0004; 2 = falta el 0005; 3 = falta el 0006; 4 = falta el 0007; 5 = falta el 0008; 6 = esquema completo.</summary>
    private const string ConsultaEsquema =
        "SELECT CASE WHEN COL_LENGTH(N'dbo_cotifly.Sesiones', N'estadoAgente') IS NULL " +
        "OR OBJECT_ID(N'dbo_cotifly.TokensRefresco', N'U') IS NULL THEN 0 " +
        "WHEN COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'serviciosConexos') IS NULL " +
        "OR OBJECT_ID(N'dbo_cotifly.CotizacionPosiciones', N'U') IS NULL THEN 1 " +
        "WHEN OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL " +
        "OR OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion', N'U') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.CotizacionPosiciones', N'lineaSolicitudId') IS NULL THEN 2 " +
        "WHEN COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'versionNegocio') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'margenSugerido') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'pdfDesactualizado') IS NULL " +
        "OR OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial', N'U') IS NULL THEN 3 " +
        "WHEN COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'notas') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenMunicipioId') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'destinoMunicipioId') IS NULL THEN 4 " +
        "WHEN COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasCargue') IS NULL " +
        "OR COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasDescargue') IS NULL THEN 5 ELSE 6 END AS [Value]";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(opciones.Value.Cotifly))
        {
            return HealthCheckResult.Unhealthy(DescripcionSinCadena);
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TiempoMaximo);

        try
        {
            if (!await contexto.Database.CanConnectAsync(limite.Token))
            {
                return HealthCheckResult.Unhealthy(DescripcionNoDisponible);
            }

            var esquema = await contexto.Database
                .SqlQueryRaw<int>(ConsultaEsquema)
                .SingleAsync(limite.Token);

            return esquema switch
            {
                6 => HealthCheckResult.Healthy(DescripcionDisponible),
                5 => HealthCheckResult.Unhealthy(DescripcionEsquemaSin0008),
                4 => HealthCheckResult.Unhealthy(DescripcionEsquemaSin0007),
                3 => HealthCheckResult.Unhealthy(DescripcionEsquemaSin0006),
                2 => HealthCheckResult.Unhealthy(DescripcionEsquemaSin0005),
                1 => HealthCheckResult.Unhealthy(DescripcionEsquemaSin0004),
                _ => HealthCheckResult.Unhealthy(DescripcionEsquemaIncompleto),
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(DescripcionNoDisponible, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(DescripcionNoDisponible, ex);
        }
    }
}
