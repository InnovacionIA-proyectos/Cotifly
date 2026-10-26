using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-036.2 (#206): finalizar la cotización (<c>POST /web/cotizaciones/{folio}/finalizar</c>, con <c>If-Match</c>). El comercial acepta el
/// precio y desde ahí queda congelado (ADR-015 §5, HU-036).
/// </summary>
public interface IClsFinalizacionCotizacionService
{
    /// <summary>
    /// La cotización finalizada (o la misma sin cambios si ya lo estaba: idempotente), o el error: <c>COTIZACION_NO_ENCONTRADA</c> (no
    /// existe o no es del comercial), <c>CONFLICTO_CONCURRENCIA</c> (el <c>If-Match</c> no es la versión vigente, también si ya estaba
    /// finalizada) o <c>COTIZACION_NO_FINALIZABLE</c> (<c>estado_costeo</c> distinto de <c>autocosteo</c> y <c>entregada</c>).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsFinalizacionCotizacionService"/>.</summary>
/// <remarks>
/// <para>El orden de las validaciones es el de las demás mutaciones de la etapa final: primero el dueño (404), después la versión del
/// <c>If-Match</c> (409 <c>CONFLICTO_CONCURRENCIA</c>, como en el resultado) y por último la regla del dominio
/// (<see cref="clsCotizacionSolicitud.Finalizar"/>).</para>
/// <para><b>Concurrencia.</b> La escritura se confirma con la versión del <c>If-Match</c> sobre la fila bloqueada
/// (<see cref="IClsUnitOfWork.GuardarCambiosSiVersionAsync"/>) y sube la versión de negocio. Si el polling cambió el precio entre la lectura
/// del comercial y su clic, la versión ya no coincide: 409 y el comercial recarga (nunca finaliza un precio que no vio). Si la finalización
/// gana, la vuelta del polling que se cruzó no guarda (conflicto) y la siguiente ve la cotización finalizada (precio congelado, #208).</para>
/// <para>Cada finalización que cambia algo, o que se rechaza, queda en <c>EventosHerramienta</c> (<see cref="HerramientaAuditoria"/>,
/// sin montos). Re-finalizar no audita (no cambia nada).</para>
/// </remarks>
public sealed class clsFinalizacionCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria) : IClsFinalizacionCotizacionService
{
    /// <summary>Herramienta de la auditoría de la finalización (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "finalizar_cotizacion";

    public async Task<clsResultado<clsCotizacionSolicitud>> FinalizarAsync(
        Guid usuarioId, string folio, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var cronometro = Stopwatch.StartNew();
        var (resultado, cambio) = await FinalizarAsync(cotizacion, usuarioId, versionEsperada, cancellationToken);
        if (resultado.EsFallo || cambio)
        {
            await auditoria.RegistrarAsync(
                HerramientaAuditoria,
                new { folio = cotizacion.Folio, version_esperada = versionEsperada },
                resultado.EsExitoso
                    ? (object)new
                    {
                        finalizada_at = cotizacion.FinalizadaAt,
                        estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                        version = cotizacion.VersionNegocio,
                    }
                    : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
                resultado.EsExitoso,
                (int)cronometro.ElapsedMilliseconds,
                cotizacion.ThreadId);
        }

        return resultado;
    }

    private async Task<(clsResultado<clsCotizacionSolicitud> Resultado, bool Cambio)> FinalizarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false);
        }

        var finalizada = cotizacion.Finalizar(usuarioId, reloj.AhoraUtc);
        if (finalizada.EsFallo)
        {
            return (finalizada.Error, false);
        }

        if (!finalizada.Valor)
        {
            // Ya estaba finalizada: 200 sin cambios (mismo ETag), como re-marcar el mismo resultado.
            return (cotizacion, false);
        }

        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? (cotizacion, true)
            : (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false);
    }
}
