using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
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
/// Recalcula los camiones de UNA ruta (<c>POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos</c>) cuando
/// cambiaron sus mercancías o su peso: pide de nuevo los camiones al cubicador y reemplaza los de esa ruta, con su resultado.
/// </summary>
public interface IClsRecalculoVehiculosService
{
    /// <summary>
    /// La vista del costeo con los camiones nuevos de la ruta, o el error: <c>COTIZACION_NO_ENCONTRADA</c>, <c>RUTA_INEXISTENTE</c>,
    /// <c>CONFLICTO_CONCURRENCIA</c>, <c>PDF_YA_ENVIADO</c> (entregada o finalizada; versión nueva en <c>detalle</c>),
    /// <c>ESTADO_NO_PERMITE</c> (la solicitud ya está en Nexo: usar re-solicitar con una versión nueva),
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (mercancías sin código: el comercial las vuelve a escoger) o el error del cubicador.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> RecalcularAsync(
        Guid usuarioId, string folio, Guid rutaId, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsRecalculoVehiculosService"/>.</summary>
/// <remarks>
/// Solo si la solicitud nunca llegó a Nexo y no hay un envío en curso ni incierto
/// (<see cref="clsCotizacionSolicitud.PuedeRecalcularCamiones"/>: la misma regla que el reenvío). El guardado exige la versión del
/// <c>If-Match</c> con la fila bloqueada, y el reclamo del reenvío sube esa versión: un recálculo y un reenvío nunca se pisan.
/// Después del envío, cambiar los camiones aquí los desincronizaría de las líneas de Nexo: se usa la re-solicitud con una versión nueva.
/// La ruta se manda al cubicador con su mismo id (<c>idLineaSolicitud</c>): es una corrida nueva y Cotifly copia los
/// <c>idRecomendacionVehiculo</c> de ESA respuesta (nunca "la última corrida").
/// </remarks>
public sealed class clsRecalculoVehiculosService(
    IClsCotizacionRepository cotizaciones,
    IClsPlanificadorCamiones planificador,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsRecalculoVehiculosService> logger,
    IClsAuditoriaService auditoria) : IClsRecalculoVehiculosService
{
    /// <summary>Herramienta de la auditoría del recálculo (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "recalcular_vehiculos_ruta";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Cambio de los camiones de la ruta después de finalizar o enviar";

    public const string MensajeYaEnNexo =
        "Los camiones de esta solicitud ya están en Nexo o se están enviando: para cambiarlos pide una versión nueva (POST .../costeo/re-solicitar) o espera el envío.";

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

        var cronometro = Stopwatch.StartNew();
        var (resultado, orden, planAplicado) = await RecalcularRutaAsync(cotizacion, rutaId, usuarioId, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, ruta_orden = orden ?? cotizacion.Rutas.FirstOrDefault(r => r.Id == rutaId)?.Orden, linea_solicitud_id = rutaId },
            resultado.EsExitoso
                ? (object)new { camiones = planAplicado?.Camiones.Count, resultado_cubicador = planAplicado?.Cubicacion?.Resultado ?? "respaldo" }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    /// <summary>El recálculo en sí; devuelve también la ruta (su orden, si se resolvió) y el plan aplicado para la auditoría.</summary>
    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, int? Orden, clsPlanRuta? Plan)> RecalcularRutaAsync(
        clsCotizacionSolicitud cotizacion, Guid rutaId, Guid usuarioId, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), null, null);
        }

        var resuelta = cotizacion.ResolverRuta(null, rutaId);
        if (resuelta.EsFallo)
        {
            return (resuelta.Error, null, null);
        }

        var ruta = resuelta.Valor;
        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersion) is { } cerrada)
        {
            // Entregada o finalizada, los camiones cambian en una versión nueva (con la llamada en el detalle).
            return (cerrada, ruta.Orden, null);
        }

        if (!cotizacion.PuedeRecalcularCamiones(reloj.AhoraUtc))
        {
            return (new clsErrorDominio(clsCodigosError.EstadoNoPermite, MensajeYaEnNexo), ruta.Orden, null);
        }

        // Con el vehículo elegido de la ruta como respaldo (si el cubicador no escoge o falla, se valida contra Nexo y se usa).
        var linea = ruta.ALineaConElegido();
        if (linea.Posiciones.Count == 0 || linea.Posiciones.Any(p => string.IsNullOrWhiteSpace(p.CodigoMercancia)))
        {
            return (new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                linea.Cubicabilidad == CubicabilidadRuta.Cubicable
                    ? $"La ruta {ruta.Orden} tiene mercancías sin código del catálogo del cubicador (anteriores al cambio): vuelve a escogerlas."
                    : $"La ruta {ruta.Orden} no tiene mercancías cubicables (con tipo de mercancía, medidas y peso): no se cubica y lleva el vehículo elegido."),
                ruta.Orden, null);
        }

        var plan = await planificador.PlanificarAsync(usuarioId, ruta.Id, linea, cancellationToken);
        if (plan.EsFallo)
        {
            return (plan.Error, ruta.Orden, null);
        }

        var reemplazo = cotizacion.ReemplazarCamionesDeRuta(ruta.Id, plan.Valor, reloj.AhoraUtc);
        if (reemplazo.EsFallo)
        {
            return (reemplazo.Error, ruta.Orden, null);
        }

        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), ruta.Orden, null);
        }

        logger.LogInformation("Camiones de la ruta {Ruta} de {Folio} recalculados con el cubicador: {Camiones} camión(es), resultado {Resultado}.",
            ruta.Orden, cotizacion.Folio, plan.Valor.Camiones.Count, plan.Valor.Cubicacion?.Resultado ?? "respaldo");
        return (clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl), ruta.Orden, plan.Valor);
    }
}
