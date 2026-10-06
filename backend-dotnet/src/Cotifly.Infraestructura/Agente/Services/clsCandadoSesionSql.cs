using System.Data;
using System.Security.Cryptography;
using System.Text;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// <see cref="IClsCandadoSesion"/> con <c>sp_getapplock</c> (<c>Exclusive</c>, dueño <c>Session</c>) sobre una conexión propia a
/// <c>dbo_cotifly</c> que se mantiene abierta durante el turno. No usa la transacción del <c>DbContext</c>: el turno dura lo que tarde el
/// modelo y sus herramientas guardan (y el folio abre su propia transacción con candado), así que no puede ir dentro de una transacción.
/// Recurso <c>cotifly:sesion:{spaceId}:{threadId}</c> (si pasa de 255 caracteres, su SHA-256). Si la conexión se corta, SQL Server
/// libera el candado solo.
/// </summary>
public sealed class clsCandadoSesionSql(IOptions<clsOpcionesConexiones> conexiones) : IClsCandadoSesion
{
    public const string Prefijo = "cotifly:sesion:";
    private const int LargoMaximoRecurso = 255;

    public async Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var recurso = Recurso(spaceId, threadId);
        // Sin pool: el candado es de la sesión física; así nunca queda vivo en una conexión devuelta al pool.
        var conexion = new SqlConnection(new SqlConnectionStringBuilder(conexiones.Value.Cotifly ?? string.Empty) { Pooling = false }.ConnectionString);
        try
        {
            await conexion.OpenAsync(cancellationToken);
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "sp_getapplock";
            comando.CommandType = CommandType.StoredProcedure;
            comando.CommandTimeout = (int)Math.Ceiling(espera.TotalSeconds) + 30;
            comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
            comando.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
            comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
            comando.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)espera.TotalMilliseconds });
            var retorno = comando.Parameters.Add(new SqlParameter("@RETURN_VALUE", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue });
            await comando.ExecuteNonQueryAsync(cancellationToken);

            // 0 = concedido, 1 = concedido tras esperar; negativo = tiempo agotado, cancelado o interbloqueo.
            if ((int)retorno.Value >= 0)
            {
                return new clsCandadoTomado(conexion, recurso);
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

    /// <summary>Nombre del recurso del candado (máximo 255 caracteres).</summary>
    public static string Recurso(string spaceId, string threadId)
    {
        var recurso = $"{Prefijo}{spaceId.Trim()}:{threadId.Trim()}";
        return recurso.Length <= LargoMaximoRecurso
            ? recurso
            : Prefijo + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recurso)));
    }

    private sealed class clsCandadoTomado(SqlConnection conexion, string recurso) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (conexion.State == ConnectionState.Open)
                {
                    await using var comando = conexion.CreateCommand();
                    comando.CommandText = "sp_releaseapplock";
                    comando.CommandType = CommandType.StoredProcedure;
                    comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
                    comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Session" });
                    await comando.ExecuteNonQueryAsync();
                }
            }
            catch (SqlException)
            {
                // Cerrar la conexión también libera el candado de sesión.
            }
            finally
            {
                await conexion.DisposeAsync();
            }
        }
    }
}
