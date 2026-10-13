using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-023.3 (#158): recalcular los camiones de UNA ruta (<c>POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos</c>) cuando
/// cambiaron sus mercancías o su peso: pide de nuevo los camiones al cubicador y reemplaza los de esa ruta, con su resultado.
/// </summary>
public interface IClsRecalculoVehiculosService
{
    /// <summary>
    /// La vista del costeo con los camiones nuevos de la ruta, o el error: <c>COTIZACION_NO_ENCONTRADA</c>, <c>RUTA_INEXISTENTE</c>,
    /// <c>CONFLICTO_CONCURRENCIA</c>, <c>ESTADO_NO_PERMITE</c> (la solicitud ya está en Nexo: usar re-solicitar con una versión nueva),
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (mercancías sin código: el comercial las vuelve a escoger) o el error del cubicador.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> RecalcularAsync(
        Guid usuarioId, string folio, Guid rutaId, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsRecalculoVehiculosService"/>.</summary>
/// <remarks>
/// Solo antes de que los camiones estén en Nexo (estado <c>pendiente_envio</c> o <c>error_envio</c>): después, cambiar los camiones aquí
/// los desincronizaría de las líneas de Nexo, así que se usa la re-solicitud con una versión nueva (que también vuelve a planificar).
/// La ruta se manda al cubicador con su mismo id (<c>idLineaSolicitud</c>): es una corrida nueva y Cotifly copia los
/// <c>idRecomendacionVehiculo</c> de ESA respuesta (decisión 3 de HU-023; nunca "la última corrida").
/// </remarks>
public sealed class clsRecalculoVehiculosService(
    IClsCotizacionRepository cotizaciones,
    IClsPlanificadorCamiones planificador,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsRecalculoVehiculosService> logger) : IClsRecalculoVehiculosService
{
    public const string MensajeYaEnNexo =
        "Los camiones de esta solicitud ya están en Nexo: para cambiarlos pide una versión nueva (POST .../costeo/re-solicitar).";

    public async Task<clsResultado<clsCosteoCotizacion>> RecalcularAsync(
        Guid usuarioId, string folio, Guid rutaId, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        var resuelta = cotizacion.ResolverRuta(null, rutaId);
        if (resuelta.EsFallo)
        {
            return resuelta.Error;
        }

        var ruta = resuelta.Valor;
        if (cotizacion.EstadoCosteo is not (EstadoCosteo.PendienteEnvio or EstadoCosteo.ErrorEnvio))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite, MensajeYaEnNexo);
        }

        var linea = ruta.ALinea() with { TipoVehiculo = null, Carroceria = null };
        if (linea.Posiciones.Count == 0 || linea.Posiciones.Any(p => string.IsNullOrWhiteSpace(p.CodigoMercancia)))
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"La ruta {ruta.Orden} tiene mercancías sin código del catálogo del cubicador (anteriores al cambio): vuelve a escogerlas.");
        }

        var plan = await planificador.PlanificarAsync(usuarioId, ruta.Id, linea, cancellationToken);
        if (plan.EsFallo)
        {
            return plan.Error;
        }

        var reemplazo = cotizacion.ReemplazarCamionesDeRuta(ruta.Id, plan.Valor, reloj.AhoraUtc);
        if (reemplazo.EsFallo)
        {
            return reemplazo.Error;
        }

        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        logger.LogInformation("Camiones de la ruta {Ruta} de {Folio} recalculados con el cubicador: {Camiones} camión(es), resultado {Resultado}.",
            ruta.Orden, cotizacion.Folio, plan.Valor.Camiones.Count, plan.Valor.Cubicacion?.Resultado ?? "respaldo");
        return clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl);
    }
}
