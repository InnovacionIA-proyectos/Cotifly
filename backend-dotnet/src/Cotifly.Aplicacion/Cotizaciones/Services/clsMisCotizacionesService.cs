using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Cotizaciones;
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

    /// <summary>Resumen (<c>CotizacionResumen</c>) de una cotización ya autorizada, con el nombre de su línea.</summary>
    Task<clsCotizacionResumen> ResumirAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsMisCotizacionesService"/>.</summary>
public sealed class clsMisCotizacionesService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsMisCotizacionesService
{
    public async Task<clsMisCotizaciones> ListarAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty)
        {
            return new clsMisCotizaciones(0, []);
        }

        var filas = await cotizaciones.ListarConLineaPorUsuarioAsync(usuarioId, IClsCotizacionRepository.LimitePorDefecto, cancellationToken);
        var baseUrl = opcionesPdf.Value.BaseUrl;
        var resumen = filas.Select(f => clsCotizacionResumen.Desde(f.Cotizacion, f.LineaNegocio, baseUrl, f.MargenAplicado, f.OrigenMargen)).ToList();
        return new clsMisCotizaciones(resumen.Count, resumen);
    }

    public async Task<clsCotizacionResumen> ResumirAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // HU-021: la línea de negocio de la ruta 1, o "Varias" si sus rutas tienen líneas distintas.
        var lineas = cotizacion.Rutas.Select(r => r.LineaNegocioId).Distinct().ToList();
        var nombre = lineas.Count > 1
            ? clsCotizacionConLinea.Varias
            : cotizacion.PrimeraRuta?.LineaNegocioId is { } id ? (await politicas.ObtenerPorIdAsync(id, cancellationToken))?.LineaNegocio : null;
        return clsCotizacionResumen.Desde(cotizacion, nombre, opcionesPdf.Value.BaseUrl);
    }
}
