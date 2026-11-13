using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// <c>POST /api/auth/elegir-rol</c> (ADR-025). Errores: <see cref="clsCodigosAcceso.SolicitudInvalida"/> (400, cuerpo
/// inválido o un <c>rol</c> que no es un código de rol) y <see cref="clsCodigosAcceso.SesionExpirada"/> (401 "Inicia sesión de nuevo."
/// para todo rechazo; el motivo real queda en la auditoría).
/// </summary>
public interface IClsEleccionRolService
{
    Task<clsResultado<clsSesionEmitida>> ElegirAsync(clsSolicitudElegirRol solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsEleccionRolService"/>. En orden:
/// <list type="number">
/// <item>Valida el ticket de elección (<see cref="IClsEmisorTicketEleccion"/>: firma con la clave derivada, emisor, audiencia, tipo y
/// vigencia de 5 minutos). Vencido o inválido: 401.</item>
/// <item>Lo CONSUME antes de nada más: registra su <c>jti</c> (con prefijo <see cref="PrefijoJti"/>) en <c>TokensRevocados</c> y lo
/// confirma. La clave primaria hace que de dos usos (aunque sean simultáneos o en réplicas distintas) solo uno pase; el otro es
/// <c>ticket_reutilizado</c>. Cualquier rechazo posterior deja el ticket gastado: hay que volver a entrar.</item>
/// <item>El rol elegido debe estar entre los que se ofrecieron en el ticket (<c>rol_no_disponible</c>).</item>
/// <item>Repite la verificación doble completa (almacén de ClientePro, asignaciones activas, vínculo; sin alta) con el
/// <c>NOMBREUSUARIO</c> del usuario del ticket: si le quitaron el rol entre el login y la elección, la verificación lo rechaza
/// (<c>rol_externo_cambio</c>) o el rol ya no está disponible (<c>rol_no_disponible</c>).</item>
/// <item>Emite el access con ese rol y el refresh (familia nueva, <c>rolSesion</c> = el rol elegido) y actualiza <c>ultimaSesion</c>.</item>
/// </list>
/// Audita cada intento (herramienta <c>elegir_rol</c>, hilo <c>auth/{nombreUsuario}</c>). Cualquier excepción inesperada responde el
/// 401 (fail-closed); solo la cancelación del llamador se propaga.
/// </summary>
public sealed class clsEleccionRolService(
    clsVerificadorIdentidad verificador,
    IClsEmisorTicketEleccion tickets,
    IClsTokenRevocadoRepository revocados,
    IClsTokenRefrescoRepository tokens,
    IClsUsuarioWebRepository usuarios,
    IClsEmisorTokens emisor,
    IClsConfiguracionJwt configuracion,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    ILogger<clsEleccionRolService> logger) : IClsEleccionRolService
{
    /// <summary>Prefijo del <c>jti</c> del ticket consumido en <c>TokensRevocados</c> (no choca con los <c>jti</c> de los access).</summary>
    public const string PrefijoJti = "eleccion:";

    public async Task<clsResultado<clsSesionEmitida>> ElegirAsync(clsSolicitudElegirRol solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        if (string.IsNullOrWhiteSpace(solicitud.Ticket) || solicitud.Ticket.Length > clsCodigosAcceso.LargoMaximoTicket)
        {
            return new clsErrorDominio(clsCodigosAcceso.SolicitudInvalida, "El campo 'ticket' es obligatorio.");
        }

        if (!clsCodigoEnum.TryDesdeCodigo<RolInterno>(solicitud.Rol?.Trim(), out var rol))
        {
            return new clsErrorDominio(clsCodigosAcceso.SolicitudInvalida,
                "El campo 'rol' es obligatorio y debe ser comercial, gerente_comercial o director.");
        }

        var inicio = Stopwatch.GetTimestamp();
        string motivo;
        string? regla = null;
        string? nombreUsuario = null;
        Guid? usuarioId = null;
        IReadOnlyList<RolInterno>? ofrecidos = null;
        clsSesionEmitida? sesion = null;
        try
        {
            var ahora = reloj.AhoraUtc;
            var (ticket, rechazo) = await tickets.ValidarAsync(solicitud.Ticket);
            if (ticket is null)
            {
                motivo = rechazo ?? clsMotivosAcceso.TicketInvalido;
            }
            else
            {
                usuarioId = ticket.UsuarioId;
                nombreUsuario = ticket.NombreUsuario;
                ofrecidos = ticket.Roles;
                (motivo, regla, sesion) = await ElegirConTicketAsync(ticket, rol, ahora, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error inesperado al elegir el rol.");
            motivo = clsMotivosAcceso.ErrorInterno;
            sesion = null;
        }

        if (sesion is null)
        {
            logger.LogInformation("Elección de rol rechazada: {Motivo}.", motivo);
        }

        await auditoria.RegistrarAsync(
            clsCodigosAcceso.HerramientaEleccionRol,
            new { UsuarioId = usuarioId, RolSolicitado = clsCodigoEnum.ACodigo(rol), RolesOfrecidos = ofrecidos?.Select(r => clsCodigoEnum.ACodigo(r)).ToList() },
            new { Resultado = sesion is null ? "rechazado" : "aceptado", Motivo = motivo, Regla = regla, Rol = sesion?.Usuario.Rol },
            sesion is not null,
            clsEmisionSesion.DuracionMs(inicio),
            nombreUsuario is null ? null : clsCodigosAcceso.PrefijoHilo + nombreUsuario);

        return sesion is null ? clsCodigosAcceso.ErrorSesionExpirada() : sesion;
    }

    private async Task<(string Motivo, string? Regla, clsSesionEmitida? Sesion)> ElegirConTicketAsync(
        clsTicketEleccionValido ticket, RolInterno rol, DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        // 1. Uso único: el jti se consume (y se confirma) antes de cualquier otra validación.
        var consumo = clsTokenRevocado.Crear(PrefijoJti + ticket.Jti, ticket.ExpiraAt, ticket.UsuarioId);
        if (consumo.EsFallo)
        {
            return (clsMotivosAcceso.TicketInvalido, null, null);
        }

        revocados.Agregar(consumo.Valor);
        try
        {
            await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        }
        catch (clsViolacionUnicidadException)
        {
            logger.LogWarning("Reuso de un ticket de elección de rol del usuario {UsuarioId}.", ticket.UsuarioId);
            return (clsMotivosAcceso.TicketReutilizado, null, null);
        }

        // 2. Solo uno de los roles que se le ofrecieron en el login.
        if (!ticket.Roles.Contains(rol))
        {
            return (clsMotivosAcceso.RolNoDisponible, null, null);
        }

        // 3. Revalidación completa (ClientePro y asignaciones activas): el ticket solo prueba que el login pasó hace un momento.
        var usuario = await usuarios.ObtenerPorIdAsync(ticket.UsuarioId, cancellationToken);
        if (usuario?.NombreUsuario is not { Length: > 0 } nombre
            || !string.Equals(nombre, ticket.NombreUsuario, StringComparison.OrdinalIgnoreCase))
        {
            return (clsMotivosAcceso.IdentidadInconsistente, null, null);
        }

        var verificacion = await verificador.VerificarAsync(nombre, permitirAlta: false, ahora, cancellationToken);
        if (verificacion.Identidad is not { } identidad)
        {
            return (verificacion.Motivo, verificacion.Regla, null);
        }

        if (identidad.Usuario.Id != usuario.Id)
        {
            return (clsMotivosAcceso.IdentidadInconsistente, verificacion.Regla, null);
        }

        if (!identidad.PuedeEntrarCon(rol))
        {
            return (clsMotivosAcceso.RolNoDisponible, verificacion.Regla, null);
        }

        // 4. Sesión con el rol elegido; el refresh lo recuerda (rolSesion).
        identidad.Usuario.RegistrarSesion(ahora);
        var sesion = clsEmisionSesion.Emitir(emisor, identidad.Usuario, rol, clsEmisionSesion.NuevoRefresh(
            identidad.Usuario.Id, ahora, configuracion.VigenciaRefresh, familiaId: null, rol, out var refresh));
        tokens.Agregar(refresh);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return (clsMotivosAcceso.RolElegido, verificacion.Regla, sesion);
    }
}
