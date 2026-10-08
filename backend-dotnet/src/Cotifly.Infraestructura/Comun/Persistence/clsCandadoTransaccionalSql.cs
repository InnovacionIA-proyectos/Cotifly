using System.Data;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// <see cref="IClsCandadoTransaccional"/> con <c>sp_getapplock</c> (modo <c>Exclusive</c>, dueño <c>Transaction</c>) sobre la
/// conexión de <see cref="clsCotiflyDbContext"/> del ámbito: el candado vive en SQL Server, así que serializa también entre
/// réplicas, y se libera solo al confirmar o deshacer. Lo que la operación guarde con la unidad de trabajo usa la misma
/// transacción (EF Core la toma de <c>Database.CurrentTransaction</c>).
/// </summary>
/// <remarks>
/// #125: la espera (<see cref="clsOpcionesCandadoSql"/>) es configurable y el límite del cliente no depende del reloj de pared
/// (<see cref="clsAppLockSql"/>). Si el candado no se obtiene (tiempo agotado, interbloqueo o sin respuesta), la transacción se
/// deshace —la operación todavía no corrió— y se reintenta con pausa creciente; agotados los intentos, el resultado es
/// <c>SERVICIO_OCUPADO</c> (503), nunca una excepción. La operación corre una sola vez: sus fallos no se reintentan.
/// </remarks>
public sealed class clsCandadoTransaccionalSql(
    clsCotiflyDbContext contexto,
    IOptions<clsOpcionesCandadoSql> opciones,
    TimeProvider tiempo,
    ILogger<clsCandadoTransaccionalSql> logger) : IClsCandadoTransaccional
{
    /// <summary>Largo máximo del nombre del recurso en <c>sp_getapplock</c>.</summary>
    public const int LargoMaximoRecurso = clsAppLockSql.LargoMaximoRecurso;

    public const string MensajeOcupado = "Hay otra operación en curso sobre el mismo consecutivo. No se guardó nada: reintenta en unos segundos.";

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

        var configuracion = opciones.Value;
        for (var intento = 1; ; intento++)
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            int? codigo;
            try
            {
                codigo = await clsAppLockSql.TomarAsync(
                    contexto.Database.GetDbConnection(),
                    transaccion.GetDbTransaction(),
                    recurso,
                    "Transaction",
                    TimeSpan.FromMilliseconds(configuracion.EsperaMilisegundos),
                    TimeSpan.FromMilliseconds(configuracion.MargenRespuestaMilisegundos),
                    tiempo,
                    cancellationToken);
            }
            catch
            {
                await DeshacerAsync(transaccion);
                throw;
            }

            if (codigo >= clsAppLockSql.Concedido)
            {
                return await EjecutarConCandadoAsync(transaccion, operacion, cancellationToken);
            }

            // Sin candado la operación no corrió: deshacer solo cierra la transacción vacía.
            await DeshacerAsync(transaccion);
            if (codigo is null)
            {
                // El cliente canceló la solicitud: la conexión puede quedar a medias, así que se cierra y el intento siguiente abre otra.
                await contexto.Database.CloseConnectionAsync();
            }

            if (codigo is not (null or clsAppLockSql.TiempoAgotado or clsAppLockSql.Cancelado or clsAppLockSql.Interbloqueo))
            {
                throw new InvalidOperationException($"sp_getapplock devolvió {codigo} para el candado '{recurso}'.");
            }

            if (intento >= configuracion.Intentos)
            {
                logger.LogWarning(
                    "Candado {Recurso} no disponible tras {Intentos} intentos (último código {Codigo}): se responde {CodigoError}.",
                    recurso, intento, codigo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "sin respuesta", clsCodigosError.ServicioOcupado);
                return new clsErrorDominio(clsCodigosError.ServicioOcupado, MensajeOcupado);
            }

            logger.LogInformation(
                "Candado {Recurso} no disponible en el intento {Intento} (código {Codigo}); se reintenta.",
                recurso, intento, codigo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "sin respuesta");
            await Task.Delay(Pausa(intento, configuracion.PausaBaseMilisegundos), tiempo, cancellationToken);
        }
    }

    /// <summary>Pausa antes del intento siguiente: base·2^(intento-1) más una variación aleatoria de hasta la mitad de la base.</summary>
    public static TimeSpan Pausa(int intento, int pausaBaseMilisegundos)
    {
        if (pausaBaseMilisegundos <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponencial = pausaBaseMilisegundos * (1L << Math.Clamp(intento - 1, 0, 10));
#pragma warning disable CA5394 // La variación solo separa reintentos simultáneos; no es seguridad.
        var variacion = Random.Shared.Next(0, (pausaBaseMilisegundos / 2) + 1);
#pragma warning restore CA5394
        return TimeSpan.FromMilliseconds(exponencial + variacion);
    }

    private async Task<clsResultado<TValor>> EjecutarConCandadoAsync<TValor>(
        IDbContextTransaction transaccion,
        Func<CancellationToken, Task<clsResultado<TValor>>> operacion,
        CancellationToken cancellationToken)
    {
        try
        {
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
