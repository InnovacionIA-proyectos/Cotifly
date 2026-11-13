using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>Límites de una llamada a <c>sp_getapplock</c> o <c>sp_releaseapplock</c>.</summary>
/// <param name="Espera"><c>@LockTimeout</c>: cuánto espera el servidor por el candado.</param>
/// <param name="Margen">Margen para la respuesta del servidor; <paramref name="Espera"/> + margen es el límite del cliente.</param>
/// <param name="Gracia">Tiempo que se espera el acuse de la cancelación antes de cerrar la conexión.</param>
/// <param name="ComandoSegundos"><c>CommandTimeout</c> de respaldo (alto: SqlClient lo mide con el reloj de pared).</param>
internal sealed record clsLimitesAppLock(TimeSpan Espera, TimeSpan Margen, TimeSpan Gracia, int ComandoSegundos);

/// <summary>Resultado de <see cref="clsAppLockSql.TomarAsync"/>.</summary>
/// <param name="Codigo">Código de <c>sp_getapplock</c>, o <c>null</c> si venció el límite del cliente sin respuesta.</param>
/// <param name="ConexionCerrada">La cancelación no se confirmó dentro de la gracia y la conexión se cerró a la fuerza.</param>
internal sealed record clsResultadoAppLock(int? Codigo, bool ConexionCerrada);

/// <summary>
/// Llamadas a <c>sp_getapplock</c> / <c>sp_releaseapplock</c> compartidas por los candados de SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// El límite principal del cliente no es <c>CommandTimeout</c>: SqlClient calcula ese plazo con el reloj de pared
/// (<c>DateTime.UtcNow</c>), y si el reloj salta hacia adelante más que el plazo mientras el comando está en curso, la siguiente
/// lectura de red ya lo ve vencido y lanza <c>Execution Timeout Expired</c> (258) al instante, sin haber esperado: con un reloj de
/// contenedor que salta unos 63–65 s (hacia atrás y luego hacia adelante), un <c>sp_getapplock</c> con <c>CommandTimeout = 30</c>
/// falla en 0,1 s. Aquí el límite lo pone un
/// <see cref="CancellationTokenSource"/> sobre el <see cref="TimeProvider"/> (temporizadores monotónicos) y la espera por el
/// candado la acota el servidor con <c>@LockTimeout</c>.
/// </para>
/// <para>
/// Respaldo doble para un servidor que deja de responder con la conexión TCP viva: <c>CommandTimeout</c> alto
/// (<see cref="clsLimitesAppLock.ComandoSegundos"/>) y, si tras cancelar el comando no termina dentro de
/// <see cref="clsLimitesAppLock.Gracia"/> (SqlClient espera sin límite el acuse de la cancelación), la conexión se cierra a la
/// fuerza: así el servidor deshace la transacción y suelta el candado, y el llamador no queda colgado.
/// </para>
/// </remarks>
internal static class clsAppLockSql
{
    /// <summary>Largo máximo del nombre del recurso en <c>sp_getapplock</c>.</summary>
    public const int LargoMaximoRecurso = 255;

    /// <summary>Candado concedido sin esperar.</summary>
    public const int Concedido = 0;

    /// <summary>Se agotó <c>@LockTimeout</c>.</summary>
    public const int TiempoAgotado = -1;

    /// <summary>La solicitud del candado se canceló.</summary>
    public const int Cancelado = -2;

    /// <summary>La solicitud del candado fue elegida como víctima de un interbloqueo.</summary>
    public const int Interbloqueo = -3;

    /// <summary>
    /// Pide el candado <paramref name="recurso"/>. <see cref="clsResultadoAppLock.Codigo"/>: 0 o 1 = concedido; -1 tiempo agotado;
    /// -2 cancelado; -3 interbloqueo; -999 error de parámetros o del servidor; <c>null</c> si venció el límite del cliente sin
    /// respuesta. Si se cancela <paramref name="cancellationToken"/>, la cancelación sale como excepción (con la conexión ya
    /// cerrada si no se confirmó a tiempo).
    /// </summary>
    public static async Task<clsResultadoAppLock> TomarAsync(
        DbConnection conexion,
        DbTransaction? transaccion,
        string recurso,
        string dueno,
        clsLimitesAppLock limites,
        TimeProvider tiempo,
        CancellationToken cancellationToken)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = "sp_getapplock";
        comando.CommandTimeout = limites.ComandoSegundos;
        comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
        comando.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = dueno });
        comando.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)limites.Espera.TotalMilliseconds });
        var retorno = new SqlParameter("@Retorno", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        comando.Parameters.Add(retorno);

        var salida = await EjecutarAcotadoAsync(conexion, comando, limites.Espera + limites.Margen, limites.Gracia, tiempo, cancellationToken);
        return salida switch
        {
            Salida.Respondio => new clsResultadoAppLock(retorno.Value is int codigo ? codigo : -999, ConexionCerrada: false),
            Salida.SinRespuesta => new clsResultadoAppLock(null, ConexionCerrada: false),
            _ => new clsResultadoAppLock(null, ConexionCerrada: true),
        };
    }

    /// <summary>
    /// <c>sp_releaseapplock</c> con los mismos límites que la toma. Devuelve <c>false</c> si no se confirmó (sin respuesta o con
    /// error): el llamador cierra la conexión, que también suelta un candado de sesión.
    /// </summary>
    public static async Task<bool> LiberarAsync(
        DbConnection conexion, string recurso, string dueno, clsLimitesAppLock limites, TimeProvider tiempo)
    {
        try
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandType = CommandType.StoredProcedure;
            comando.CommandText = "sp_releaseapplock";
            comando.CommandTimeout = limites.ComandoSegundos;
            comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
            comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = dueno });
            return await EjecutarAcotadoAsync(conexion, comando, limites.Margen, limites.Gracia, tiempo, CancellationToken.None) == Salida.Respondio;
        }
        catch (SqlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Cierra la conexión sin bloquear al llamador más de <paramref name="espera"/> (SqlClient puede quedarse drenando una conexión
    /// congelada). <c>false</c> si el cierre sigue en curso al vencer la espera.
    /// </summary>
    public static async Task<bool> CerrarALaFuerzaAsync(DbConnection conexion, TimeSpan espera, TimeProvider tiempo)
    {
        if (conexion is SqlConnection sql)
        {
            // La conexión física no debe volver al pool: puede tener una transacción y un candado a medias.
            SqlConnection.ClearPool(sql);
        }

        var cierre = Task.Run(() =>
        {
            try
            {
                conexion.Close();
            }
#pragma warning disable CA1031 // Cierre de mejor esfuerzo: no hay a quién informar.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Ya estaba rota o cerrada.
            }
        });
        try
        {
            await cierre.WaitAsync(espera, tiempo);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private enum Salida
    {
        Respondio,
        SinRespuesta,
        ConexionCerrada,
    }

    private static async Task<Salida> EjecutarAcotadoAsync(
        DbConnection conexion, DbCommand comando, TimeSpan limite, TimeSpan gracia, TimeProvider tiempo, CancellationToken cancellationToken)
    {
        using var plazo = new CancellationTokenSource(limite, tiempo);
        using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plazo.Token);
        using var finGracia = new CancellationTokenSource(Timeout.InfiniteTimeSpan, tiempo);
        await using var alCancelar = enlazado.Token.Register(() => finGracia.CancelAfter(gracia));

        var ejecucion = comando.ExecuteNonQueryAsync(enlazado.Token);
        try
        {
            await ejecucion.WaitAsync(finGracia.Token);
            return Salida.Respondio;
        }
        catch (OperationCanceledException) when (finGracia.IsCancellationRequested && !ejecucion.IsCompleted)
        {
            // Se canceló y SqlClient sigue esperando el acuse del servidor: se abandona el comando y se cierra la conexión.
            _ = ejecucion.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await CerrarALaFuerzaAsync(conexion, gracia, tiempo);
            cancellationToken.ThrowIfCancellationRequested();
            return Salida.ConexionCerrada;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ((ex is SqlException or OperationCanceledException && plazo.IsCancellationRequested)
                                       || ex is SqlException { Number: -2 }))
        {
            // SqlClient informa la cancelación como SqlException ("Operation cancelled by user") u OperationCanceledException;
            // -2 es el CommandTimeout de respaldo (SqlClient ya rompió la conexión si el servidor no confirmó).
            return Salida.SinRespuesta;
        }
    }
}
