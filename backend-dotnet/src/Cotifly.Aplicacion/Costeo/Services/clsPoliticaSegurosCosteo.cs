using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Seguros;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// La política de seguros en el costeo. Lee la política de sus tablas (sin caché), completa el <c>codigoServicio</c> de los
/// conexos que no lo tienen con el índice del catálogo de Nexo (<see cref="IClsCategoriasConexos.CodigosAsync"/>) y arma el bloque
/// <c>politica_seguros</c> de cada ruta (<see cref="clsRutaCosteo.PoliticaSeguros"/>). Una cotización finalizada no se
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
    /// ¿El conexo cuenta para cumplir una medida? No si su envío a Nexo quedó <c>fallido_definitivo</c>
    /// o <c>incierto</c> (nunca llegó o no se sabe); sí si está <c>enviado</c>, <c>pendiente</c> (lo bloquea otra validación) o sin estado.
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
            evaluacion.Tramo is { } tramo ? new clsTramoPoliticaSegurosRuta(tramo.Id, tramo.Nombre, tramo.ValorDesde, tramo.ValorHasta) : null,
            evaluacion.Aviso,
            [.. evaluacion.Recomendadas.Select(Medida)],
            [.. evaluacion.Obligatorias.Select(Medida)]);
    }

    private static clsMedidaPolitica Medida(clsMedidaEvaluada medida) =>
        new(medida.Codigo, medida.Nombre, medida.EsCobrable, medida.Estado, medida.Conexos);

    private static readonly Action<ILogger, string, Exception?> _logPoliticaNoDisponible = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1735, nameof(_logPoliticaNoDisponible)),
        "No se pudo leer la política de seguros para el costeo de {Folio}; la respuesta sale sin politica_seguros.");

    private static readonly Action<ILogger, string, Exception?> _logCodigosNoDisponibles = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1736, nameof(_logCodigosNoDisponibles)),
        "No se pudo leer el codigoServicio de los conexos de {Folio} en el catálogo de Nexo; se completan en otra lectura.");
}

/// <summary>
/// <c>PoliticaSegurosRuta</c>: lo que exige la política de seguros a la ruta. <see cref="Tramo"/> es
/// <c>null</c> sin valor de la mercancía (o con valor 0) o si ningún tramo activo lo contiene; <see cref="Aviso"/> es
/// <c>"SIN_VALOR_MERCANCIA"</c> o <c>null</c>. <see cref="Recomendadas"/> y <see cref="Obligatorias"/> traen la misma forma de medida.
/// </summary>
public sealed record clsPoliticaSegurosRuta(
    clsTramoPoliticaSegurosRuta? Tramo,
    string? Aviso,
    IReadOnlyList<clsMedidaPolitica> Recomendadas,
    IReadOnlyList<clsMedidaPolitica> Obligatorias);

/// <summary>El tramo de la ruta: su <see cref="Id"/>, su nombre y sus límites en pesos (<see cref="ValorDesde"/> exclusivo, <see cref="ValorHasta"/> incluido; <c>null</c> = sin tope).</summary>
public sealed record clsTramoPoliticaSegurosRuta(Guid Id, string Nombre, decimal ValorDesde, decimal? ValorHasta);

/// <summary>
/// Una medida de la política (<c>Medida = {codigo, nombre, es_cobrable, estado, conexos[]}</c>): su código, el nombre
/// que se muestra, si <see cref="EsCobrable"/>, el <see cref="Estado"/> (<c>cumplida</c> | <c>pendiente</c> | <c>sin_verificar</c>) y los
/// <c>codigoServicio</c> de Nexo que la cumplen (<see cref="Conexos"/>, para el botón "agregar").
/// </summary>
public sealed record clsMedidaPolitica(string Codigo, string Nombre, bool EsCobrable, string Estado, IReadOnlyList<string> Conexos);
