using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Descarga del PDF con la URL firmada de Google Chat (<c>GET /pdfs/{archivo}?t=...</c>, ADR-011 §3), alternativa al Bearer: el
/// token (HMAC, 15 minutos) nombra al comercial, que debe ser el dueño de la cotización.
/// </summary>
public interface IClsDescargaPdfFirmadaService
{
    /// <summary>
    /// <c>NO_AUTENTICADO</c> si el token está alterado, vencido o es de otro archivo (la Api responde 401); <c>NO_ENCONTRADO</c> si
    /// el archivo no es el PDF de una cotización del comercial del token (404).
    /// </summary>
    Task<clsResultado<clsArchivoPdf>> ObtenerAsync(string? archivo, string? token, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsDescargaPdfFirmadaService"/>.</summary>
public sealed partial class clsDescargaPdfFirmadaService(
    IClsFirmaUrlPdf firma,
    IClsCotizacionRepository cotizaciones,
    IClsAlmacenPdf almacen) : IClsDescargaPdfFirmadaService
{
    public const string MensajeEnlaceInvalido = "El enlace del PDF venció o no es válido. Pídelo de nuevo en el chat.";

    public async Task<clsResultado<clsArchivoPdf>> ObtenerAsync(string? archivo, string? token, CancellationToken cancellationToken = default)
    {
        if (archivo is null || !ExpresionArchivo().IsMatch(archivo))
        {
            return NoEncontrado();
        }

        if (firma.Verificar(archivo, token) is not { } usuarioId)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, MensajeEnlaceInvalido);
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(archivo[..^".pdf".Length], usuarioId, cancellationToken);
        if (cotizacion is null || !string.Equals(clsCorreoService.NombreAdjunto(cotizacion), archivo, StringComparison.Ordinal))
        {
            return NoEncontrado();
        }

        var contenido = await almacen.LeerPorReferenciaAsync(cotizacion.PdfPath, cancellationToken);
        return contenido is null ? NoEncontrado() : new clsArchivoPdf(archivo, contenido);
    }

    private static clsErrorDominio NoEncontrado() => new(clsCodigosAplicacion.NoEncontrado, "Cotización no encontrada o no es tuya.");

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.pdf\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionArchivo();
}
