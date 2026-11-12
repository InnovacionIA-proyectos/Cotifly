using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// "Mis cotizaciones" (<c>GET /web/mis-cotizaciones</c>) y el resumen que devuelve el seguimiento: solo cotizaciones del
/// comercial autenticado (aislamiento por dueño, ADR-009), máximo 100, de la más reciente a la más antigua.
/// </summary>
public interface IClsMisCotizacionesService
{
    /// <summary>Cotizaciones del comercial; lista vacía si el id está vacío o no tiene ninguna.</summary>
    Task<clsMisCotizaciones> ListarAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumen (<c>CotizacionResumen</c>) de una cotización ya autorizada, con el nombre de su línea. Se llama DESPUÉS de confirmar
    /// (captura, resultado, reintento, re-solicitud): si la lectura del nombre de la línea falla (timeout de la base, #173), no lanza;
    /// devuelve el resumen con lo que está en memoria, <c>linea_negocio = null</c> y el aviso <see cref="clsMisCotizacionesService.AvisoResumenIncompleto"/>.
    /// </summary>
    Task<clsCotizacionResumen> ResumirAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsMisCotizacionesService"/>.</summary>
public sealed class clsMisCotizacionesService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsMisCotizacionesService>? logger = null) : IClsMisCotizacionesService
{
    /// <summary>
    /// Aviso de <c>CotizacionResumen.avisos</c> (#173): lo esencial ya quedó guardado (y enviado a Nexo), pero una lectura secundaria
    /// del resumen falló; el nombre de la línea sale <c>null</c>. El frontend recarga con <c>GET /web/mis-cotizaciones</c>.
    /// </summary>
    public const string AvisoResumenIncompleto = "RESUMEN_INCOMPLETO";

    private readonly ILogger _logger = logger ?? NullLogger<clsMisCotizacionesService>.Instance;

    public async Task<clsMisCotizaciones> ListarAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty)
        {
            return new clsMisCotizaciones(0, []);
        }

        var filas = await cotizaciones.ListarConLineaPorUsuarioAsync(usuarioId, IClsCotizacionRepository.LimitePorDefecto, cancellationToken);
        var baseUrl = opcionesPdf.Value.BaseUrl;
        var resumen = filas.Select(f => clsCotizacionResumen.Desde(f.Cotizacion, f.LineaNegocio, baseUrl, f.MargenAplicado, f.OrigenMargen, f.Carga)).ToList();
        return new clsMisCotizaciones(resumen.Count, resumen);
    }

    public async Task<clsCotizacionResumen> ResumirAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // HU-021: la línea de negocio de las rutas, o "Varias" si tienen líneas distintas (una ruta sin línea no cuenta, HU-021.9).
        var lineas = cotizacion.Rutas.Select(r => r.LineaNegocioId).OfType<int>().Distinct().ToList();
        if (lineas.Count != 1)
        {
            return clsCotizacionResumen.Desde(cotizacion, lineas.Count > 1 ? clsCotizacionConLinea.Varias : null, opcionesPdf.Value.BaseUrl);
        }

        try
        {
            var nombre = (await politicas.ObtenerPorIdAsync(lineas[0], cancellationToken))?.LineaNegocio;
            return clsCotizacionResumen.Desde(cotizacion, nombre, opcionesPdf.Value.BaseUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // #173: la cotización ya está guardada (y quizá en Nexo). Un 500 aquí haría que el cliente reintentara y duplicara la
            // solicitud: se responde con lo que hay en memoria y un aviso.
            _logResumenIncompleto(_logger, cotizacion.Folio, ex);
            return clsCotizacionResumen.Desde(cotizacion, null, opcionesPdf.Value.BaseUrl) with { Avisos = [AvisoResumenIncompleto] };
        }
    }

    private static readonly Action<ILogger, string, Exception?> _logResumenIncompleto = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1730, nameof(_logResumenIncompleto)),
        "No se pudo leer la línea de negocio del resumen de {Folio}; se responde sin ella (RESUMEN_INCOMPLETO).");
}
