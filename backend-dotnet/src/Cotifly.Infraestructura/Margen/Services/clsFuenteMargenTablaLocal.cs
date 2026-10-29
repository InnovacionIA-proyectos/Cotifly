using System.Data.Common;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Fuente del margen de SAP TM desde la copia local (HU-042.2, #243, ADR-027): <c>dbo_cotifly.SapMargenCliente</c>, que el job diario de
/// Datos (HU-042.1, #242) sincroniza desde <c>RentabClienteSet</c>. Cotifly no llama a SAP al cotizar; reemplaza los modos
/// <c>export_json</c> y <c>odata</c> de #13.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>"No configurada".</b> Si <c>SapMargenSyncLog</c> no tiene ninguna corrida <c>ok</c>, la copia nunca se llenó (local, QA sin
/// job): se responde <see cref="EstadoConsultaMargenSap.NoConfigurada"/> sin leer la tabla y el resolver usa la política de la línea.</item>
/// <item><b>Búsqueda.</b> Solo filas <c>activo = 1</c> por (organización, TrqType, BP), con organización y TrqType recortados y en
/// mayúsculas. El BP se compara sin ceros a la izquierda en los dos lados (SAP hoy lo manda sin ceros, el catálogo de clientes puede
/// traer <c>0000000580</c>): la consulta usa el índice único <c>(salesOrgId, trqType, bpSap)</c> por sus dos primeras columnas y filtra
/// por el final del BP; las pocas candidatas se comparan en memoria. Gana la coincidencia exacta y luego la más corta.</item>
/// <item><b>Sin caché.</b> Dos consultas pequeñas e indexadas por resolución (la última corrida <c>ok</c> y la fila), <c>AsNoTracking</c>; el
/// envío a Nexo resuelve una vez por línea de negocio, no por ruta.</item>
/// <item><b>No caduca.</b> Una copia vieja se sigue usando: el resolver agrega un aviso al detalle según
/// <c>SapTm:AvisoSincronizacionHoras</c>.</item>
/// <item><b>Errores.</b> Un fallo de la base (conexión, tiempo agotado, tabla ausente) se traduce a
/// <see cref="clsFuenteMargenNoDisponibleException"/> (el resolver cae a la política con "SAP TM no disponible"); el detalle técnico
/// queda en Debug y el mensaje no incluye datos de conexión.</item>
/// </list>
/// </remarks>
public sealed class clsFuenteMargenTablaLocal(clsCotiflyDbContext contexto, ILogger<clsFuenteMargenTablaLocal> logger) : IClsFuenteMargen
{
    /// <summary>Etiqueta de la fuente para los logs.</summary>
    public const string ModoTablaLocal = "tabla_local";

    public string Modo => ModoTablaLocal;

    public async Task<clsConsultaMargenSap> ConsultarAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(salesOrg);
        ArgumentNullException.ThrowIfNull(trqType);
        ArgumentNullException.ThrowIfNull(bpCliente);

        var organizacion = salesOrg.Trim().ToUpperInvariant();
        var tipo = trqType.Trim().ToUpperInvariant();
        var bp = NormalizarBp(bpCliente);

        try
        {
            var ultimaSincronizacion = await contexto.SapMargenSyncLog.AsNoTracking()
                .Where(l => l.Status == clsSapMargenSyncLogFila.EstadoOk)
                .OrderByDescending(l => l.IniciadoAt)
                .Select(l => (DateTimeOffset?)(l.FinalizadoAt ?? l.IniciadoAt))
                .FirstOrDefaultAsync(cancellationToken);
            if (ultimaSincronizacion is null)
            {
                return clsConsultaMargenSap.NoConfigurada;
            }

            if (bp.Length == 0)
            {
                return clsConsultaMargenSap.SinRegistro(ultimaSincronizacion);
            }

            var candidatas = await contexto.SapMargenCliente.AsNoTracking()
                .Where(m => m.SalesOrgId == organizacion && m.TrqType == tipo && m.Activo && m.BpSap.EndsWith(bp))
                .Select(m => new { m.BpSap, m.MargenRecomendado, m.ActualizadoAt })
                .ToListAsync(cancellationToken);
            var fila = candidatas
                .Where(c => NormalizarBp(c.BpSap) == bp)
                .OrderBy(c => string.Equals(c.BpSap.Trim(), bpCliente.Trim(), StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(c => c.BpSap.Length)
                .FirstOrDefault();

            return fila is null
                ? clsConsultaMargenSap.SinRegistro(ultimaSincronizacion)
                : clsConsultaMargenSap.ConValor(fila.MargenRecomendado, fila.ActualizadoAt, ultimaSincronizacion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de la fuente
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException or RetryLimitExceededException
                                       or OperationCanceledException)
        {
            logger.LogWarning("La copia local del margen de SAP TM no se pudo leer ({Error}) consultando {TrqType} en {SalesOrg}.",
                ex.GetType().Name, tipo, organizacion);
            logger.LogDebug(ex, "Detalle del fallo leyendo dbo_cotifly.SapMargenCliente.");
            throw new clsFuenteMargenNoDisponibleException("No se pudo leer la copia local del margen de SAP TM.", ex);
        }
    }

    /// <summary>
    /// BP para comparar: recortado y sin ceros a la izquierda (<c>"0000000580"</c> → <c>"580"</c>); un BP de solo ceros queda en
    /// <c>"0"</c> y uno vacío en <c>""</c> (sin registro).
    /// </summary>
    public static string NormalizarBp(string? bp)
    {
        var recortado = (bp ?? string.Empty).Trim();
        if (recortado.Length == 0)
        {
            return string.Empty;
        }

        var sinCeros = recortado.TrimStart('0');
        return sinCeros.Length == 0 ? "0" : sinCeros;
    }
}
