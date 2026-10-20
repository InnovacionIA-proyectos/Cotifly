using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// #191: purga periódica (cada hora; la primera, a los dos minutos de arrancar) de las <c>Idempotency-Key</c> vencidas de
/// <c>dbo_cotifly.CapturasIdempotentes</c>, en lotes de <see cref="clsAlmacenIdempotenciaSql.LotePurga"/> hasta vaciarlas (con un tope
/// por ronda). Las vencidas ya no cuentan aunque sigan en la tabla (la toma las ignora y las borra al reusar la clave): esto solo acota el
/// tamaño. Con varias réplicas cada una purga; el <c>DELETE</c> es idempotente. Un fallo (base caída, sin el 06) queda como warning y se
/// reintenta en la ronda siguiente: nunca detiene la API. Si el almacén registrado no es el de SQL (pruebas), no hace nada.
/// </summary>
public sealed class clsLimpiezaIdempotenciaHostedService(
    IClsAlmacenIdempotencia almacen, TimeProvider tiempo, ILogger<clsLimpiezaIdempotenciaHostedService> logger) : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromHours(1);
    public static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(2);

    /// <summary>Lotes máximos por ronda (100.000 filas): una tabla enorme se termina de vaciar en las rondas siguientes.</summary>
    public const int LotesPorRonda = 100;

    /// <summary>Una ronda de purga. Devuelve las filas borradas o <c>null</c> si falló o no hay almacén SQL.</summary>
    public async Task<int?> LimpiarAsync(CancellationToken cancellationToken = default)
    {
        if (almacen is not clsAlmacenIdempotenciaSql sql)
        {
            return null;
        }

        try
        {
            var total = 0;
            for (var lote = 0; lote < LotesPorRonda; lote++)
            {
                var borradas = await sql.PurgarVencidasAsync(cancellationToken);
                total += borradas;
                if (borradas < clsAlmacenIdempotenciaSql.LotePurga)
                {
                    break;
                }
            }

            if (total > 0)
            {
                logger.LogInformation("Purga de Idempotency-Key: {Borradas} claves vencidas.", total);
            }

            return total;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("No se pudo purgar las Idempotency-Key vencidas ({Tipo}): {Mensaje}", ex.GetType().Name, ex.Message);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(EsperaInicial, tiempo, stoppingToken);
            using var temporizador = new PeriodicTimer(Intervalo, tiempo);
            do
            {
                await LimpiarAsync(stoppingToken);
            }
            while (await temporizador.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado de la API.
        }
    }
}
