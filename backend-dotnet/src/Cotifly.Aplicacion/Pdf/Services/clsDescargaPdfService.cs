using System.Diagnostics;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>PDF listo para descargar (<c>GET /pdfs/{archivo}</c>).</summary>
public sealed record clsArchivoPdf(string Nombre, byte[] Contenido);

/// <summary>
/// Descarga autenticada del PDF de una cotización (<c>GET /pdfs/{archivo}</c>; en el Python era público). Puede descargarlo el
/// dueño de la cotización o un <c>gerente_comercial</c> de la misma regional; cualquier otro caso es
/// <see cref="clsCodigosAplicacion.NoEncontrado"/> (404, igual que si no existiera).
/// </summary>
public interface IClsDescargaPdfService
{
    Task<clsResultado<clsArchivoPdf>> ObtenerAsync(string? archivo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsDescargaPdfService"/>. Revalida el rol dentro del caso de uso (además de la política del
/// endpoint):
/// <list type="bullet">
/// <item>El archivo debe cumplir <c>^[A-Za-z0-9_-]+\.pdf$</c> y ser el PDF guardado de la cotización con ese folio.</item>
/// <item>Dueño: siempre.</item>
/// <item><c>gerente_comercial</c>: si su regional (<c>UsuariosWeb.regional</c>) es conocida, solo PDF de comerciales de esa
/// regional; si la suya es nula (aún no se cruza con DimEjecutivo) no se puede comprobar y se permite, como dice el contrato
/// ("o un gerente_comercial"). Cada acceso de un gerente a un PDF ajeno, y cada rechazo por dueño, queda en la auditoría
/// (<c>descargar_pdf</c>).</item>
/// </list>
/// </summary>
public sealed partial class clsDescargaPdfService(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsAlmacenPdf almacen,
    IClsAuditoriaService auditoria) : IClsDescargaPdfService
{
    public const string HerramientaAuditoria = "descargar_pdf";
    private const string ExtensionPdf = ".pdf";

    public async Task<clsResultado<clsArchivoPdf>> ObtenerAsync(string? archivo, CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        if (archivo is null || !ExpresionArchivo().IsMatch(archivo))
        {
            return NoEncontrado();
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioAsync(archivo[..^ExtensionPdf.Length], cancellationToken);
        if (cotizacion is null || !string.Equals(clsCorreoService.NombreAdjunto(cotizacion), archivo, StringComparison.Ordinal))
        {
            return NoEncontrado();
        }

        if (!cotizacion.EsDe(usuarioId))
        {
            var reloj = Stopwatch.StartNew();
            var permitido = usuario.Rol == RolInterno.GerenteComercial && await EsDeSuRegionalAsync(usuarioId, cotizacion, cancellationToken);
            await auditoria.RegistrarAsync(
                HerramientaAuditoria,
                new { archivo, usuario_id = usuarioId, rol = usuario.Rol is { } rol ? clsCodigoEnum.ACodigo(rol) : null },
                new { permitido, dueno_id = cotizacion.UsuarioId },
                permitido,
                (int)reloj.ElapsedMilliseconds,
                null);
            if (!permitido)
            {
                return NoEncontrado();
            }
        }

        var contenido = await almacen.LeerPorReferenciaAsync(cotizacion.PdfPath, cancellationToken);
        return contenido is null ? NoEncontrado() : new clsArchivoPdf(archivo, contenido);
    }

    private async Task<bool> EsDeSuRegionalAsync(Guid gerenteId, clsCotizacion cotizacion, CancellationToken cancellationToken)
    {
        var gerente = await usuarios.ObtenerPorIdAsync(gerenteId, cancellationToken);
        if (gerente is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(gerente.Regional))
        {
            return true;
        }

        var dueno = cotizacion.UsuarioId is { } duenoId ? await usuarios.ObtenerPorIdAsync(duenoId, cancellationToken) : null;
        return string.Equals(dueno?.Regional?.Trim(), gerente.Regional.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static clsErrorDominio NoEncontrado() => new(clsCodigosAplicacion.NoEncontrado, "Cotización no encontrada o no es tuya.");

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.pdf\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionArchivo();
}
