using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Llamada a <c>sp_getapplock</c> (modo <c>Exclusive</c>) compartida por los candados de SQL Server (#125).
/// </summary>
/// <remarks>
/// El límite del lado del cliente no usa <c>CommandTimeout</c>: SqlClient calcula ese plazo con el reloj de pared
/// (<c>DateTime.UtcNow</c>), y si el reloj salta hacia adelante más que el plazo mientras el comando está en curso, la
/// siguiente lectura de red ya lo ve vencido y lanza <c>Execution Timeout Expired</c> (258) al instante, sin haber esperado.
/// Eso pasó en las suites del lote 2: el reloj del contenedor de pruebas saltó unos 63 s hacia atrás y 66 s hacia adelante,
/// y los <c>sp_getapplock</c> con <c>CommandTimeout = 30</c> fallaron en 0,1 s. Aquí el comando va sin límite propio
/// (<c>CommandTimeout = 0</c>) y el límite lo pone un <see cref="CancellationTokenSource"/> sobre el <see cref="TimeProvider"/>,
/// cuyos temporizadores son monotónicos. La espera por el candado la acota el servidor con <c>@LockTimeout</c>.
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
    /// Pide el candado <paramref name="recurso"/>. Devuelve el código de <c>sp_getapplock</c> (0 o 1 = concedido; -1 tiempo agotado;
    /// -2 cancelado; -3 interbloqueo; -999 error de parámetros o del servidor) o <c>null</c> si venció el límite del cliente
    /// (<paramref name="espera"/> + <paramref name="margen"/>) sin respuesta del servidor. Si se cancela <paramref name="cancellationToken"/>,
    /// la excepción sale tal cual.
    /// </summary>
    public static async Task<int?> TomarAsync(
        DbConnection conexion,
        DbTransaction? transaccion,
        string recurso,
        string dueno,
        TimeSpan espera,
        TimeSpan margen,
        TimeProvider tiempo,
        CancellationToken cancellationToken)
    {
        using var limite = new CancellationTokenSource(espera + margen, tiempo);
        using var enlazado = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limite.Token);
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = "sp_getapplock";
        comando.CommandTimeout = 0;
        comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
        comando.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = dueno });
        comando.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)espera.TotalMilliseconds });
        var retorno = new SqlParameter("@Retorno", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        comando.Parameters.Add(retorno);

        try
        {
            await comando.ExecuteNonQueryAsync(enlazado.Token);
        }
        catch (Exception ex) when (ex is SqlException or OperationCanceledException
                                   && limite.IsCancellationRequested
                                   && !cancellationToken.IsCancellationRequested)
        {
            // SqlClient informa la cancelación como SqlException ("Operation cancelled by user") u OperationCanceledException.
            return null;
        }

        return retorno.Value is int codigo ? codigo : -999;
    }
}
