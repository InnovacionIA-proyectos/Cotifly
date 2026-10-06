using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Implementación de <see cref="IClsVistaPreviaCorreoService"/>: mismo asunto, cuerpo y adjunto que usaría
/// <see cref="clsCorreoService"/>, sin tocar el transporte ni la base.
/// </summary>
public sealed class clsVistaPreviaCorreoService(
    IClsCotizacionRepository cotizaciones,
    IClsDestinatariosPreviosService destinatariosPrevios) : IClsVistaPreviaCorreoService
{
    public async Task<clsResultado<clsVistaPreviaCorreo>> PrevisualizarAsync(
        Guid usuarioId, string folio, clsBorradorCorreo? borrador = null, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio))
        {
            return NoEncontrada();
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var para = Limpiar(borrador?.Para);
        var cc = Limpiar(borrador?.Cc);
        if (para.Count == 0)
        {
            var previos = await destinatariosPrevios.ObtenerAsync(usuarioId, cotizacion.NitCliente, cancellationToken);
            para = [.. previos.Para];
            if (cc.Count == 0)
            {
                cc = [.. previos.Cc];
            }
        }

        var cuerpo = string.IsNullOrWhiteSpace(borrador?.Cuerpo) ? clsPlantillaCorreo.CuerpoPorDefecto : borrador.Cuerpo.Trim();
        var asunto = string.IsNullOrWhiteSpace(borrador?.Asunto) ? clsPlantillaCorreo.AsuntoPorDefecto(cotizacion.Folio) : borrador.Asunto.Trim();
        return new clsVistaPreviaCorreo(asunto, cuerpo, para, cc, clsCorreoService.NombreAdjunto(cotizacion));
    }

    private static List<string> Limpiar(IReadOnlyList<string>? emails) =>
        emails is null ? [] : [.. emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim())];

    private static clsErrorDominio NoEncontrada() =>
        new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
