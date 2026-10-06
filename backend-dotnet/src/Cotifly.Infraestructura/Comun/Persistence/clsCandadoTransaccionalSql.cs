using System.Data;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// <see cref="IClsCandadoTransaccional"/> con <c>sp_getapplock</c> (modo <c>Exclusive</c>, dueño <c>Transaction</c>) sobre la
/// conexión de <see cref="clsCotiflyDbContext"/> del ámbito: el candado vive en SQL Server, así que serializa también entre
/// réplicas, y se libera solo al confirmar o deshacer. Lo que la operación guarde con la unidad de trabajo usa la misma
/// transacción (EF Core la toma de <c>Database.CurrentTransaction</c>).
/// </summary>
public sealed class clsCandadoTransaccionalSql(clsCotiflyDbContext contexto) : IClsCandadoTransaccional
{
    /// <summary>Espera máxima por el candado. El folio se calcula en milisegundos; 15 s solo se alcanzan con la base degradada.</summary>
    public static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(15);

    /// <summary>Largo máximo del nombre del recurso en <c>sp_getapplock</c>.</summary>
    public const int LargoMaximoRecurso = 255;

    public async Task<clsResultado<TValor>> EjecutarAsync<TValor>(
        string recurso,
        Func<CancellationToken, Task<clsResultado<TValor>>> operacion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurso);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recurso.Length, LargoMaximoRecurso, nameof(recurso));
        ArgumentNullException.ThrowIfNull(operacion);
        if (contexto.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Ya hay una transacción abierta en esta unidad de trabajo: el candado no se anida.");
        }

        await using var transaccion = await contexto.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await TomarCandadoAsync(transaccion, recurso, cancellationToken);
            var resultado = await operacion(cancellationToken);
            if (resultado.EsExitoso)
            {
                await transaccion.CommitAsync(cancellationToken);
            }
            else
            {
                await DeshacerAsync(transaccion);
            }

            return resultado;
        }
        catch
        {
            await DeshacerAsync(transaccion);
            throw;
        }
    }

    private async Task TomarCandadoAsync(IDbContextTransaction transaccion, string recurso, CancellationToken cancellationToken)
    {
        var conexion = contexto.Database.GetDbConnection();
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion.GetDbTransaction();
        comando.CommandType = CommandType.StoredProcedure;
        comando.CommandText = "sp_getapplock";
        comando.CommandTimeout = (int)EsperaMaxima.TotalSeconds + 15;
        comando.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, LargoMaximoRecurso) { Value = recurso });
        comando.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        comando.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Transaction" });
        comando.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = (int)EsperaMaxima.TotalMilliseconds });
        var retorno = new SqlParameter("@Retorno", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        comando.Parameters.Add(retorno);

        await comando.ExecuteNonQueryAsync(cancellationToken);

        // 0 = concedido; 1 = concedido tras esperar; negativo = tiempo agotado (-1), cancelado (-2), interbloqueo (-3) o error (-999).
        if (retorno.Value is not int codigo || codigo < 0)
        {
            throw new TimeoutException($"No se obtuvo el candado '{recurso}' (sp_getapplock devolvió {retorno.Value}).");
        }
    }

    private async Task DeshacerAsync(IDbContextTransaction transaccion)
    {
        try
        {
            await transaccion.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // La transacción ya no está activa (p. ej. la conexión se cerró): no hay nada que deshacer.
        }
        catch (SqlException)
        {
            // Idem: el servidor ya la terminó.
        }

        // Lo que la operación dejó en memoria corresponde a una transacción deshecha.
        contexto.ChangeTracker.Clear();
    }
}
