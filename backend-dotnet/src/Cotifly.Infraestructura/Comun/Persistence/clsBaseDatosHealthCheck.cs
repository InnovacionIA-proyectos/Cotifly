using System.Text;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Readiness de la base <c>dbo_cotifly</c> para <c>GET /health/ready</c> (rehecha para HU-021, ADR-019): la cadena debe estar
/// configurada, la base debe responder y el esquema debe tener TODAS las tablas y columnas que mapea el modelo EF (se leen del propio
/// modelo, así la sonda no se queda atrás cuando cambia el esquema). Si falta algo, la descripción dice qué script falta:
/// <list type="bullet">
/// <item><see cref="DescripcionEsquemaIncompleto"/>: falta el 0002 (<c>Sesiones.estadoAgente</c> o <c>TokensRefresco</c>);</item>
/// <item><see cref="DescripcionEsquemaSin0009"/>: la base sigue con <c>Cotizaciones</c> (esquema anterior a la reestructuración);</item>
/// <item><see cref="DescripcionEsquemaSin0010"/>: solo faltan las columnas del 0010 (nombres de municipio, id de la solicitud de Nexo,
/// nombre del conexo);</item>
/// <item>cualquier otra ausencia: <c>esquema incompleto: falta Tabla.columna</c> (las primeras, sin datos de conexión).</item>
/// </list>
/// La sonda tiene un tiempo máximo propio (<see cref="TiempoMaximo"/>). Las descripciones no incluyen la cadena ni el servidor.
/// </summary>
public sealed class clsBaseDatosHealthCheck(clsCotiflyDbContext contexto, IOptions<clsOpcionesConexiones> opciones) : IHealthCheck
{
    public const string Nombre = "base-datos-cotifly";
    public const string DescripcionSinCadena = "La cadena de conexión 'ConnectionStrings:Cotifly' no está configurada.";
    public const string DescripcionNoDisponible = "La base de datos de Cotifly no está disponible.";
    public const string DescripcionEsquemaIncompleto = "esquema incompleto: falta 0002";

    /// <summary>La base tiene el esquema anterior (<c>Cotizaciones</c>): falta la reestructuración de HU-021.</summary>
    public const string DescripcionEsquemaSin0009 = "esquema incompleto: falta 0009";

    /// <summary>Está el 0009, pero no el 0010 (nombres de municipio, id de la solicitud de Nexo y nombre del conexo).</summary>
    public const string DescripcionEsquemaSin0010 = "esquema incompleto: falta 0010";

    /// <summary>Prefijo de la descripción cuando falta otra tabla o columna del modelo.</summary>
    public const string PrefijoEsquemaIncompleto = "esquema incompleto: falta ";

    public const string DescripcionDisponible = "La base de datos de Cotifly responde.";

    /// <summary>Tiempo máximo de la sonda completa (conexión + verificación de esquema).</summary>
    public static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(5);

    /// <summary>Columnas que agrega el 0010 (borrador pendiente de aprobación).</summary>
    public static readonly IReadOnlySet<string> Columnas0010 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionesSolicitud.solicitudNexoId",
        "CotizacionLineasSolicitud.origenNombre",
        "CotizacionLineasSolicitud.destinoNombre",
        "CotizacionLineasSolicitud.ciudadDevolucionNombre",
        "CotizacionLineasSolicitud.ciudadLevanteNombre",
        "CotizacionServiciosConexos.nombreServicioConexo",
    };

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

            var faltantes = await contexto.Database.SqlQueryRaw<string>(ConsultaFaltantes(contexto.Model)).ToListAsync(limite.Token);
            var cotizacionesViejas = faltantes.Count > 0
                && await contexto.Database.SqlQueryRaw<int>(
                    "SELECT CASE WHEN OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync(limite.Token) == 1;
            return Clasificar(faltantes, cotizacionesViejas);
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

    /// <summary>Resultado de la sonda según las columnas que faltan (<c>Tabla.columna</c>) y si la base aún tiene <c>Cotizaciones</c>.</summary>
    public static HealthCheckResult Clasificar(IReadOnlyCollection<string> faltantes, bool cotizacionesViejas)
    {
        ArgumentNullException.ThrowIfNull(faltantes);
        if (faltantes.Count == 0)
        {
            return HealthCheckResult.Healthy(DescripcionDisponible);
        }

        if (faltantes.Any(f => f.StartsWith("TokensRefresco.", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(f, "Sesiones.estadoAgente", StringComparison.OrdinalIgnoreCase)))
        {
            return HealthCheckResult.Unhealthy(DescripcionEsquemaIncompleto);
        }

        if (cotizacionesViejas && faltantes.Any(f => f.StartsWith("CotizacionesSolicitud.", StringComparison.OrdinalIgnoreCase)))
        {
            return HealthCheckResult.Unhealthy(DescripcionEsquemaSin0009);
        }

        if (faltantes.All(Columnas0010.Contains))
        {
            return HealthCheckResult.Unhealthy(DescripcionEsquemaSin0010);
        }

        return HealthCheckResult.Unhealthy(PrefijoEsquemaIncompleto + string.Join(", ", faltantes.Order(StringComparer.Ordinal).Take(5)));
    }

    /// <summary>
    /// <c>Tabla.columna</c> de cada columna del modelo EF que no existe en <c>dbo_cotifly</c> (<c>COL_LENGTH</c> es NULL si no existe la
    /// columna o la tabla). Los nombres salen del modelo (código), nunca de datos del usuario.
    /// </summary>
    public static string ConsultaFaltantes(IModel modelo)
    {
        ArgumentNullException.ThrowIfNull(modelo);
        var filas = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entidad in modelo.GetEntityTypes())
        {
            if (entidad.GetTableName() is not { } tabla)
            {
                continue;
            }

            var objeto = StoreObjectIdentifier.Table(tabla, entidad.GetSchema());
            foreach (var propiedad in entidad.GetProperties())
            {
                if (propiedad.GetColumnName(objeto) is { } columna)
                {
                    filas.Add($"(N'{tabla}', N'{columna}')");
                }
            }
        }

        var sql = new StringBuilder("SELECT CONCAT(t.tabla, N'.', t.columna) AS [Value] FROM (VALUES ");
        sql.AppendJoin(", ", filas);
        sql.Append(") AS t(tabla, columna) WHERE COL_LENGTH(N'dbo_cotifly.' + t.tabla, t.columna) IS NULL");
        return sql.ToString();
    }
}
