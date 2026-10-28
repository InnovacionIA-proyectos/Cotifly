using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>Una cotización leída por su dueño (<see cref="SoloLectura"/> = <c>false</c>) o por un gerente o director en solo consulta.</summary>
public sealed record clsCotizacionLeida(clsCotizacionSolicitud Cotizacion, bool SoloLectura);

/// <summary>
/// Quién puede LEER una cotización por folio (HU-027.1 #179, HU-037.3 #211): la regla del detalle (<c>GET /web/cotizaciones/{folio}</c>),
/// que comparte el Excel de la cotización (HU-018.1, #155, parte C).
/// <list type="bullet">
/// <item>el dueño, siempre;</item>
/// <item>el <c>director</c> (cualquier regional) o un <c>gerente_comercial</c> cuya regional efectiva incluye al dueño
/// (<see cref="IClsRegionalUsuario.PuedeVerDeAsync"/>), en solo consulta. Si la regional no se puede comprobar (sin regional o fuente
/// caída) se niega (fail-closed). Cada consulta de un gerente o director a una cotización ajena, permitida o no, queda en la auditoría con
/// la herramienta del llamador;</item>
/// <item>cualquier otro caso: <c>COTIZACION_NO_ENCONTRADA</c> (404, igual que si no existiera).</item>
/// </list>
/// </summary>
public sealed class clsAccesoLecturaCotizacion(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsAuditoriaService auditoria)
{
    public async Task<clsResultado<clsCotizacionLeida>> ObtenerAsync(string folio, string herramientaAuditoria, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(herramientaAuditoria);
        if (usuario.UsuarioId is not { } usuarioId)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        if (string.IsNullOrWhiteSpace(folio))
        {
            return NoEncontrada();
        }

        // El dueño primero (la consulta filtra por dueño): un comercial ajeno nunca carga el agregado de otro.
        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is not null)
        {
            return new clsCotizacionLeida(cotizacion, SoloLectura: false);
        }

        if (usuario.Rol is not { } rol || !rol.EsGerencial())
        {
            return NoEncontrada();
        }

        cotizacion = await cotizaciones.ObtenerPorFolioAsync(folio, cancellationToken);
        if (cotizacion is null || !await PermitidoAlGerenteAsync(usuarioId, rol, folio, cotizacion, herramientaAuditoria, cancellationToken))
        {
            return NoEncontrada();
        }

        return new clsCotizacionLeida(cotizacion, SoloLectura: true);
    }

    /// <summary>
    /// ¿El dueño es miembro de la regional efectiva del gerente? La misma regla que el listado regional y el PDF; cualquier error de la
    /// regional niega (fail-closed). Queda en la auditoría, permitido o no.
    /// </summary>
    private async Task<bool> PermitidoAlGerenteAsync(
        Guid gerenteId, RolInterno rol, string folio, clsCotizacionSolicitud cotizacion, string herramienta, CancellationToken cancellationToken)
    {
        var reloj = Stopwatch.StartNew();
        var (permitido, motivo) = await EsDeSuRegionalAsync(gerenteId, rol, cotizacion, cancellationToken);
        await auditoria.RegistrarAsync(
            herramienta,
            new { folio, usuario_id = gerenteId, rol = clsCodigoEnum.ACodigo(rol) },
            new { permitido, dueno_id = cotizacion.UsuarioId, motivo },
            permitido,
            (int)reloj.ElapsedMilliseconds,
            null);
        return permitido;
    }

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

        // HU-037.3 (#211): el director ve las de cualquier regional; el gerente, las de su regional (IClsRegionalUsuario).
        var miembro = await regionales.PuedeVerDeAsync(gerente, rol, duenoId, cancellationToken);
        return miembro.EsFallo ? (false, miembro.Error.Codigo) : (miembro.Valor, null);
    }

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
