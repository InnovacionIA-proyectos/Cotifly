using System.Data;
using System.Globalization;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// <see cref="IClsCupoCubicador"/> con <c>sp_getapplock</c> (HU-023.5, #160, petición P6): el cupo <c>i</c> es el candado
/// <c>cotifly:cubicador:cupo:{i}</c> (<c>Exclusive</c>, dueño <c>Session</c>) sobre una conexión propia, sin pool, que queda abierta
/// mientras dura la llamada al cubicador. En una sola conexión se prueban los cupos 0 a <c>máximo − 1</c> sin esperar
/// (<c>@LockTimeout = 0</c>): el primero libre es el de esta llamada. Así el límite vale para todas las réplicas juntas, y si una réplica
/// muere con un cupo tomado SQL Server lo suelta al cortarse la conexión. Mismos límites del cliente que el candado de la sesión (#125).
/// </summary>
public sealed class clsCupoCubicadorSql(IOptions<clsOpcionesConexiones> conexiones, TimeProvider tiempo) : IClsCupoCubicador
{
    public const string Prefijo = "cotifly:cubicador:cupo:";

    private static readonly TimeSpan _margenRespuesta = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _gracia = TimeSpan.FromSeconds(5);
    private const int ComandoSegundos = 60;

    /// <summary>Prefijo del candado del envío a Nexo de una licitación cubicada.</summary>
    public const string PrefijoEnvio = "cotifly:licitacion:envio:";

    public Task<IAsyncDisposable?> IntentarTomarAsync(int maximo, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximo, 1);
        return TomarPrimeroLibreAsync([.. Enumerable.Range(0, maximo).Select(c => Prefijo + c.ToString(CultureInfo.InvariantCulture))], cancellationToken);
    }

    public Task<IAsyncDisposable?> IntentarTomarEnvioAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        TomarPrimeroLibreAsync([PrefijoEnvio + cotizacionId.ToString("N")], cancellationToken);

    /// <summary>El primero libre de <paramref name="recursos"/> (sin esperar), en una sola conexión, o <c>null</c>.</summary>
    private async Task<IAsyncDisposable?> TomarPrimeroLibreAsync(IReadOnlyList<string> recursos, CancellationToken cancellationToken)
    {
        var conexion = new SqlConnection(new SqlConnectionStringBuilder(conexiones.Value.Cotifly ?? string.Empty) { Pooling = false }.ConnectionString);
        try
        {
            await conexion.OpenAsync(cancellationToken);
            var limites = new clsLimitesAppLock(TimeSpan.Zero, _margenRespuesta, _gracia, ComandoSegundos);
            foreach (var recurso in recursos)
            {
                var toma = await clsAppLockSql.TomarAsync(conexion, transaccion: null, recurso, "Session", limites, tiempo, cancellationToken);
                if (toma.Codigo >= clsAppLockSql.Concedido)
                {
                    return new clsCupoTomado(conexion, recurso, tiempo);
                }

                if (toma.ConexionCerrada)
                {
                    // Ya se cerró (o se está cerrando) a la fuerza: desecharla aquí podría quedarse esperando a SqlClient.
                    return null;
                }

                if (toma.Codigo is null)
                {
                    // El servidor no respondió a tiempo: no se sigue probando en esta conexión.
                    break;
                }
            }
        }
        catch
        {
            await conexion.DisposeAsync();
            throw;
        }

        await conexion.DisposeAsync();
        return null;
    }

    private sealed class clsCupoTomado(SqlConnection conexion, string recurso, TimeProvider tiempo) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Si no se confirma la liberación, cerrar la conexión también suelta el candado de sesión.
            if (conexion.State == ConnectionState.Open
                && !await clsAppLockSql.LiberarAsync(
                    conexion, recurso, "Session", new clsLimitesAppLock(TimeSpan.Zero, _margenRespuesta, _gracia, ComandoSegundos), tiempo))
            {
                await clsAppLockSql.CerrarALaFuerzaAsync(conexion, _gracia, tiempo);
                return;
            }

            await conexion.DisposeAsync();
        }
    }
}
