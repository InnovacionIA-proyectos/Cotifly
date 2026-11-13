using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// La cotización AJENA que el usuario autenticado puede consultar en solo lectura, con la regla del detalle
/// (<see cref="IClsDetalleCotizacionService"/>) y del PDF: el <c>director</c> (cualquier regional) o un
/// <c>gerente_comercial</c> cuya regional efectiva incluye al dueño (<see cref="IClsRegionalUsuario.PuedeVerDeAsync"/>). Lo usan la vista y
/// el Excel de la licitación. El dueño NO pasa por aquí: quien llama busca primero la cotización propia.
/// </summary>
public interface IClsAccesoCotizacionAjena
{
    /// <summary>
    /// La cotización <paramref name="folio"/> si <paramref name="usuarioId"/> (el usuario autenticado) es gerencial y puede verla; si no,
    /// <c>null</c> (quien llama responde 404, igual que si no existiera). Un comercial ni siquiera carga la cotización y no se audita. Con un
    /// rol gerencial, cada intento sobre una cotización que existe queda en la auditoría con <paramref name="herramienta"/>, permitido o
    /// no; si la regional no se puede comprobar (sin regional o fuente caída) se niega (fail-closed) con el código en <c>motivo</c>. Con
    /// <paramref name="tipo"/>, una cotización de otro tipo (p. ej. un folio spot pedido como licitación) es <c>null</c> ANTES de auditar:
    /// quien llama la iba a responder 404, así que no queda un "permitido" falso en la auditoría.
    /// </summary>
    Task<clsCotizacionSolicitud?> ObtenerAsync(
        Guid usuarioId, string folio, string herramienta, TipoSolicitud? tipo = null, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsAccesoCotizacionAjena"/>.</summary>
public sealed class clsAccesoCotizacionAjenaService(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsAuditoriaService auditoria) : IClsAccesoCotizacionAjena
{
    public async Task<clsCotizacionSolicitud?> ObtenerAsync(
        Guid usuarioId, string folio, string herramienta, TipoSolicitud? tipo = null, CancellationToken cancellationToken = default)
    {
        // Solo para el usuario de la petición: el rol sale de su sesión.
        if (usuario.UsuarioId != usuarioId || usuario.Rol is not { } rol || !rol.EsGerencial() || string.IsNullOrWhiteSpace(folio))
        {
            return null;
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioAsync(folio, cancellationToken);
        // El tipo se filtra antes de auditar: así un folio spot pedido como licitación no queda "permitido" y luego en 404.
        if (cotizacion is null || cotizacion.EsDe(usuarioId) || (tipo is not null && cotizacion.TipoSolicitud != tipo))
        {
            return null;
        }

        var reloj = Stopwatch.StartNew();
        var (permitido, motivo) = await PuedeVerAsync(usuarioId, rol, cotizacion, cancellationToken);
        await auditoria.RegistrarAsync(
            herramienta,
            new { folio, usuario_id = usuarioId, rol = clsCodigoEnum.ACodigo(rol) },
            new { permitido, dueno_id = cotizacion.UsuarioId, motivo },
            permitido,
            (int)reloj.ElapsedMilliseconds,
            null);
        return permitido ? cotizacion : null;
    }

    private async Task<(bool Permitido, string? Motivo)> PuedeVerAsync(Guid usuarioId, RolInterno rol, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (cotizacion.UsuarioId is not { } duenoId)
        {
            return (false, null);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (gerente is null)
        {
            return (false, null);
        }

        var miembro = await regionales.PuedeVerDeAsync(gerente, rol, duenoId, cancellationToken);
        return miembro.EsFallo ? (false, miembro.Error.Codigo) : (miembro.Valor, null);
    }
}
