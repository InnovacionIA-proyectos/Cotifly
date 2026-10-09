using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Caso de uso de seguimiento (<c>POST /web/cotizaciones/{folio}/seguimiento</c>, interino hasta HU-005.14): marca la
/// cotización del comercial como ganada o perdida con el motivo de rechazo y la fecha (<c>marcadoAt</c>).
/// </summary>
public interface IClsSeguimientoService
{
    /// <summary>
    /// <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial (el endpoint responde 404);
    /// <see cref="clsSeguimientoService.CodigoSeguimientoInvalido"/> si el motivo supera 2000 caracteres (400).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsSeguimientoService"/>. La regla vivía en <c>IClsCotizacionRepository.MarcarSeguimientoAsync</c>
/// (portada así de <c>shared/repos.py</c>); pasa a caso de uso por la deuda de ADR-008. Igual que el Python, guarda
/// <c>ganada</c> y el motivo tal como llegan (un <c>ganada = null</c> deja la cotización sin resultado).
/// </summary>
public sealed class clsSeguimientoService(IClsCotizacionRepository cotizaciones, IClsUnitOfWork unidadTrabajo, IClsReloj reloj) : IClsSeguimientoService
{
    public const string CodigoSeguimientoInvalido = "SEGUIMIENTO_INVALIDO";
    public const int MaxMotivoRechazo = 2000;

    public async Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, CancellationToken cancellationToken = default)
    {
        if (motivoRechazo is { Length: > MaxMotivoRechazo })
        {
            return new clsErrorDominio(CodigoSeguimientoInvalido, $"El motivo de rechazo supera {MaxMotivoRechazo} caracteres.");
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (!cotizacion.ResultadoSoloLocal)
        {
            // En el flujo de costeo de Nexo: marcarla solo en Cotifly desincronizaría el resultado (HU-005.14). Las del chat, las
            // descartadas y las de costeo vencido sí se marcan aquí (Nexo no les acepta resultado).
            return new clsErrorDominio(clsCodigosError.UsarResultadoNexo,
                $"La cotización {cotizacion.Folio} está en Nexo: marca el resultado con POST /web/cotizaciones/{cotizacion.Folio}/resultado.");
        }

        cotizacion.MarcarSeguimiento(ganada, motivoRechazo, reloj.AhoraUtc);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return cotizacion;
    }
}
