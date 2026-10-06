using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Datos para prellenar una versión nueva (<c>GET /web/cotizaciones/{id}/nueva-version</c>, HU-004.4). Solo lectura: no
/// modifica la cotización original. La versión nueva se crea después con
/// <see cref="IClsGeneradorFolioService.CrearNuevaVersionAsync{TValor}"/>.
/// </summary>
public interface IClsNuevaVersionService
{
    /// <summary><c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial (el endpoint responde 404).</summary>
    Task<clsResultado<clsDatosNuevaVersion>> ObtenerDatosAsync(Guid usuarioId, Guid cotizacionId, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsNuevaVersionService"/>.</summary>
public sealed class clsNuevaVersionService(IClsCotizacionRepository cotizaciones, IClsMargenPoliticaRepository politicas) : IClsNuevaVersionService
{
    public async Task<clsResultado<clsDatosNuevaVersion>> ObtenerDatosAsync(Guid usuarioId, Guid cotizacionId, CancellationToken cancellationToken = default)
    {
        var origen = usuarioId == Guid.Empty || cotizacionId == Guid.Empty
            ? null
            : await cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacionId, usuarioId, cancellationToken);
        if (origen is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var folio = clsFolio.Desde(origen.Folio);
        var folioBase = folio.EsExitoso ? folio.Valor.Base : origen.Folio;
        var maxima = await cotizaciones.ObtenerVersionMaximaAsync(folioBase, cancellationToken);
        var linea = origen.LineaNegocioId is { } id ? await politicas.ObtenerPorIdAsync(id, cancellationToken) : null;

        return new clsDatosNuevaVersion(
            origen.Id,
            folioBase,
            Math.Max(maxima, origen.Version) + 1,
            origen.ClienteNombre,
            origen.NitCliente,
            origen.OrigenCliente is { } origenCliente ? clsCodigoEnum.ACodigo(origenCliente) : null,
            linea?.LineaNegocio,
            origen.Costo,
            origen.MargenPolitica,
            origen.MargenAplicado,
            origen.OrigenMargen is { } origenMargen ? clsCodigoEnum.ACodigo(origenMargen) : null,
            origen.MetodoCalculo,
            origen.Moneda);
    }
}
