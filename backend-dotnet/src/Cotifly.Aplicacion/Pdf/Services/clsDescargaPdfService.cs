using System.Diagnostics;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>PDF listo para descargar (<c>GET /pdfs/{archivo}</c>).</summary>
public sealed record clsArchivoPdf(string Nombre, byte[] Contenido);

/// <summary>
/// Descarga autenticada del PDF de una cotización (<c>GET /pdfs/{archivo}</c>). Puede descargarlo el
/// dueño de la cotización, el <c>director</c> (cualquier regional) o un <c>gerente_comercial</c> cuya regional efectiva
/// incluye al dueño; cualquier otro caso es
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
/// <item><c>gerente_comercial</c>: solo PDF de los miembros de su regional efectiva, con la misma regla que el listado
/// <c>GET /web/cotizaciones/regional</c> (<see cref="IClsRegionalUsuario.EsMiembroDeMiRegionalAsync"/>: <c>DimEjecutivo</c> primero,
/// <c>UsuariosWeb.regional</c> de respaldo). Si el gerente no tiene regional (<c>REGIONAL_NO_ASIGNADA</c>) o la
/// fuente viva está caída (<c>REGIONAL_NO_DISPONIBLE</c>), no se puede comprobar el alcance y se niega con 404 (fail-closed):
/// solo el dueño. Cada acceso de un gerente a un PDF ajeno, y cada rechazo por dueño, queda en la auditoría
/// (<c>descargar_pdf</c>).</item>
/// </list>
/// </summary>
public sealed partial class clsDescargaPdfService(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
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
            var (permitido, motivo) = usuario.Rol is { } rolGerencial && rolGerencial.EsGerencial()
                ? await EsDeSuRegionalAsync(usuarioId, rolGerencial, cotizacion, cancellationToken)
                : (false, null);
            await auditoria.RegistrarAsync(
                HerramientaAuditoria,
                new { archivo, usuario_id = usuarioId, rol = usuario.Rol is { } rol ? clsCodigoEnum.ACodigo(rol) : null },
                new { permitido, dueno_id = cotizacion.UsuarioId, motivo },
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

    /// <summary>
    /// ¿El dueño de <paramref name="cotizacion"/> es miembro de la regional efectiva del gerente? Cualquier error de la regional
    /// niega (fail-closed); <c>motivo</c> lleva su código para la auditoría.
    /// </summary>
    private async Task<(bool Permitido, string? Motivo)> EsDeSuRegionalAsync(Guid gerenteId, RolInterno rol, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (cotizacion.UsuarioId is not { } duenoId)
        {
            return (false, null);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(gerenteId, cancellationToken);
        if (gerente is null)
        {
            return (false, null);
        }

        // El director descarga los de cualquier regional; el gerente, los de su regional (IClsRegionalUsuario).
        var miembro = await regionales.PuedeVerDeAsync(gerente, rol, duenoId, cancellationToken);
        return miembro.EsFallo ? (false, miembro.Error.Codigo) : (miembro.Valor, null);
    }

    private static clsErrorDominio NoEncontrado() => new(clsCodigosAplicacion.NoEncontrado, "Cotización no encontrada o no es tuya.");

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.pdf\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionArchivo();
}
