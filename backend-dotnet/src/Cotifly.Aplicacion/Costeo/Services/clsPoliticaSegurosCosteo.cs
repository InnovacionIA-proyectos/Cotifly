using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Seguros;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-046.3 (#269): la política de seguros en el costeo. Lee la política de sus tablas (sin caché), completa el <c>codigoServicio</c> de los
/// conexos que no lo tienen con el índice del catálogo de Nexo (<see cref="IClsCategoriasConexos.CodigosAsync"/>) y arma el bloque
/// <c>politica_seguros</c> de cada ruta (<see cref="clsRutaCosteo.PoliticaSeguros"/>). Decisión 7 de HU-046.1: una cotización finalizada no se
/// recalcula (sin bloque); una ruta rechazada por la Mesa de Costeo no cuenta (sin bloque).
/// </summary>
public static class clsPoliticaSegurosCosteo
{
    /// <summary>
    /// La política vigente, o <c>null</c> sin repositorio o si la lectura lanza (y un aviso en el log): la vista del costeo sale igual, sin
    /// el bloque. "Finalizar" no usa esta: allí un error de lectura es un error (no finaliza sin evaluar).
    /// </summary>
    public static async Task<clsPoliticaSeguros?> LeerSinFallarAsync(
        IClsPoliticaSegurosRepository? politicas, ILogger logger, string folio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (politicas is null)
        {
            return null;
        }

        try
        {
            return await politicas.ObtenerAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logPoliticaNoDisponible(logger, folio, ex);
            return null;
        }
    }

    /// <summary>
    /// Completa en memoria el <c>codigoServicio</c> de los conexos que no lo tienen con lo que ya sabe el índice del catálogo de Nexo (sin
    /// esperarlo: si está frío, se calienta en segundo plano y la siguiente lectura los completa). Nunca falla. Devuelve los conexos que
    /// completó; quien llama los guarda.
    /// </summary>
    public static async Task<IReadOnlyList<clsServicioConexoRuta>> CompletarCodigosAsync(
        IClsCategoriasConexos? catalogo, clsCotizacionSolicitud cotizacion, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(logger);
        if (catalogo is null || cotizacion.Rutas.SelectMany(r => r.Conexos).All(c => c.CodigoServicio is not null))
        {
            return [];
        }

        try
        {
            var codigos = await catalogo.CodigosAsync(cotizacion, cancellationToken);
            return codigos.Count == 0 ? [] : cotizacion.CompletarCodigosConexos(c => codigos.GetValueOrDefault(c.IdServicioConexo));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logCodigosNoDisponibles(logger, cotizacion.Folio, ex);
            return [];
        }
    }

    /// <summary>
    /// <paramref name="vista"/> con el bloque <c>politica_seguros</c> de cada ruta cotizable según <paramref name="politica"/>. Sin política
    /// (no se pudo leer) o con la cotización finalizada, la vista sale igual (bloques en <c>null</c>).
    /// </summary>
    public static clsCosteoCotizacion ConPolitica(clsCosteoCotizacion vista, clsCotizacionSolicitud cotizacion, clsPoliticaSeguros? politica)
    {
        ArgumentNullException.ThrowIfNull(vista);
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (politica is null || cotizacion.Finalizada)
        {
            return vista;
        }

        var porId = cotizacion.Rutas.ToDictionary(r => r.Id);
        return vista with
        {
            Rutas =
            [
                .. vista.Rutas.Select(r => porId.TryGetValue(r.LineaSolicitudId, out var ruta) && !ruta.Rechazada
                    ? r with { PoliticaSeguros = Bloque(ruta.EvaluarPoliticaSeguros(politica, CuentaSegunVista(r))) }
                    : r),
            ],
        };
    }

    /// <summary>
    /// Decisión del usuario (2026-11-09): ¿el conexo cuenta para cumplir una medida? No si su envío a Nexo quedó <c>fallido_definitivo</c>
    /// o <c>incierto</c> (nunca llegó o no se sabe); sí si está <c>enviado</c>, <c>pendiente</c> (lo bloquea #228 aparte) o sin estado.
    /// </summary>
    public static bool CuentaParaLaPolitica(string? estadoNexo) =>
        estadoNexo is not (clsEnvioConexosNexoService.VisibleFallidoDefinitivo or clsEnvioConexosNexoService.VisibleIncierto);

    /// <summary>Lo de <see cref="CuentaParaLaPolitica"/> con el <c>estado_nexo</c> que ya trae la vista de la ruta.</summary>
    private static Func<clsServicioConexoRuta, bool> CuentaSegunVista(clsRutaCosteo ruta)
    {
        var excluidos = ruta.ServiciosConexos.Where(s => !CuentaParaLaPolitica(s.EstadoNexo)).Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return c => !excluidos.Contains(c.IdServicioConexo);
    }

    /// <summary>El bloque <c>politica_seguros</c> de una ruta.</summary>
    public static clsPoliticaSegurosRuta Bloque(clsEvaluacionPoliticaSeguro evaluacion)
    {
        ArgumentNullException.ThrowIfNull(evaluacion);
        return new clsPoliticaSegurosRuta(
            evaluacion.Tramo is { } tramo ? new clsTramoPoliticaSegurosRuta(tramo.Nombre, tramo.ValorDesde, tramo.ValorHasta) : null,
            [.. evaluacion.Recomendadas.Select(m => new clsMedidaRecomendadaRuta(m.Codigo, m.Nombre))],
            [.. evaluacion.Obligatorias.Select(m => new clsMedidaObligatoriaRuta(m.Codigo, m.Nombre, m.Estado, m.Conexos))],
            evaluacion.Aviso)
        {
            AvisoCodigo = evaluacion.AvisoCodigo,
        };
    }

    private static readonly Action<ILogger, string, Exception?> _logPoliticaNoDisponible = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1735, nameof(_logPoliticaNoDisponible)),
        "No se pudo leer la política de seguros para el costeo de {Folio}; la respuesta sale sin politica_seguros.");

    private static readonly Action<ILogger, string, Exception?> _logCodigosNoDisponibles = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1736, nameof(_logCodigosNoDisponibles)),
        "No se pudo leer el codigoServicio de los conexos de {Folio} en el catálogo de Nexo; se completan en otra lectura.");
}

/// <summary>
/// <c>PoliticaSegurosRuta</c> (HU-046.3, #269): lo que exige la política de seguros a la ruta. <see cref="Tramo"/> es <c>null</c> sin valor
/// de la mercancía (o con valor 0) o si ningún tramo activo lo contiene; entonces <see cref="Aviso"/> lo explica y nada se exige.
/// </summary>
public sealed record clsPoliticaSegurosRuta(
    clsTramoPoliticaSegurosRuta? Tramo,
    IReadOnlyList<clsMedidaRecomendadaRuta> MedidasRecomendadas,
    IReadOnlyList<clsMedidaObligatoriaRuta> MedidasObligatorias,
    string? Aviso)
{
    /// <summary>
    /// B7 de la auditoría: código estable del <see cref="Aviso"/>: <c>sin_valor_mercancia</c> (el comercial registra el valor) o
    /// <c>sin_tramo</c> (error de datos de la política); <c>null</c> sin aviso.
    /// </summary>
    public string? AvisoCodigo { get; init; }
}

/// <summary>El tramo de la ruta: su nombre y sus límites en pesos (<see cref="ValorDesde"/> exclusivo, <see cref="ValorHasta"/> incluido; <c>null</c> = sin tope).</summary>
public sealed record clsTramoPoliticaSegurosRuta(string Nombre, decimal ValorDesde, decimal? ValorHasta);

/// <summary>Una medida recomendada: su código y el nombre que se muestra.</summary>
public sealed record clsMedidaRecomendadaRuta(string Codigo, string Nombre);

/// <summary>
/// Una medida obligatoria: <see cref="Estado"/> <c>cumplida</c> o <c>pendiente</c> y los <c>codigoServicio</c> de Nexo que la cumplen
/// (<see cref="Conexos"/>, para el botón "agregar").
/// </summary>
public sealed record clsMedidaObligatoriaRuta(string Codigo, string Nombre, string Estado, IReadOnlyList<string> Conexos);
