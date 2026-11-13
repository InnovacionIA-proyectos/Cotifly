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
/// <c>POST /api/auth/login-clientepro</c> (patrón "Login SSO — Cliente Pro", ADR-009). Errores:
/// <see cref="clsCodigosAcceso.SolicitudInvalida"/> (400) y <see cref="clsCodigosAcceso.CredencialesInvalidas"/> (401
/// genérico para todo rechazo; el motivo real queda en la auditoría y en el log). Con un solo rol devuelve la
/// sesión; con varios (ADR-025), la elección pendiente (ticket de un solo uso para <c>POST /api/auth/elegir-rol</c>), sin sesión.
/// </summary>
public interface IClsLoginClienteProService
{
    Task<clsResultado<clsResultadoLogin>> IniciarSesionAsync(clsSolicitudLoginClientePro solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsLoginClienteProService"/>: valida el cuerpo, hace la verificación doble
/// (<see cref="clsVerificadorIdentidad"/>, con alta JIT en el primer ingreso), actualiza <c>ultimaSesion</c>, emite el
/// access (JWT) y el refresh opaco (se guarda su SHA-256, nueva familia, con el rol de la sesión) y audita cada intento en
/// <c>EventosHerramienta</c> (herramienta <c>login_clientepro</c>, hilo <c>auth/{idUsuario}</c>). Cualquier excepción
/// inesperada también responde el 401 genérico (fail-closed); solo la cancelación del llamador se propaga.
/// <para>
/// Si la persona tiene varios roles disponibles NO se emite sesión (ADR-025): se guardan el alta y los datos corporativos
/// refrescados, se emite el ticket de elección (<see cref="IClsEmisorTicketEleccion"/>, 5 minutos, un solo uso) y se audita
/// <c>eleccion_rol_pendiente</c> con los roles ofrecidos. <c>ultimaSesion</c> se actualiza cuando elige.
/// </para>
/// </summary>
public sealed class clsLoginClienteProService(
    clsVerificadorIdentidad verificador,
    IClsTokenRefrescoRepository tokens,
    IClsEmisorTokens emisor,
    IClsEmisorTicketEleccion tickets,
    IClsConfiguracionJwt configuracion,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    ILogger<clsLoginClienteProService> logger) : IClsLoginClienteProService
{
    public async Task<clsResultado<clsResultadoLogin>> IniciarSesionAsync(
        clsSolicitudLoginClientePro solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        var idUsuario = solicitud.IdUsuario?.Trim();
        if (string.IsNullOrEmpty(idUsuario) || idUsuario.Length > clsCodigosAcceso.LargoMaximoIdUsuario)
        {
            return new clsErrorDominio(clsCodigosAcceso.SolicitudInvalida,
                $"El campo 'idUsuario' es obligatorio y de máximo {clsCodigosAcceso.LargoMaximoIdUsuario} caracteres.");
        }

        if (solicitud.NombreUsuario is { Length: > clsCodigosAcceso.LargoMaximoNombreUsuario })
        {
            return new clsErrorDominio(clsCodigosAcceso.SolicitudInvalida,
                $"El campo 'nombreUsuario' supera {clsCodigosAcceso.LargoMaximoNombreUsuario} caracteres.");
        }

        var inicio = Stopwatch.GetTimestamp();
        string motivo;
        string? regla = null;
        Guid? usuarioRechazado = null;
        clsResultadoLogin? resultado = null;
        IReadOnlyList<string>? rolesOtorgados = null;
        IReadOnlyList<string>? rolesRevocados = null;
        try
        {
            var ahora = reloj.AhoraUtc;
            var verificacion = await verificador.VerificarAsync(idUsuario, permitirAlta: true, ahora, cancellationToken);
            motivo = verificacion.Motivo;
            regla = verificacion.Regla;
            usuarioRechazado = verificacion.UsuarioIdRechazado;
            if (verificacion.Identidad is { } identidad)
            {
                // Deltas de la reconciliación de roles, para la auditoría (solo códigos; sin datos sensibles).
                rolesOtorgados = identidad.Otorgados.Count > 0 ? [.. identidad.Otorgados.Select(clsCodigoEnum.ACodigo)] : null;
                rolesRevocados = identidad.Revocados.Count > 0 ? [.. identidad.Revocados.Select(clsCodigoEnum.ACodigo)] : null;
                clsResultadoLogin emitido;
                if (identidad.RequiereEleccion)
                {
                    // Sin sesión todavía. El ticket va ligado al usuario y a los roles que se le ofrecen.
                    emitido = clsResultadoLogin.ConEleccion(EleccionPendiente(identidad));
                }
                else
                {
                    identidad.Usuario.RegistrarSesion(ahora);
                    var sesion = clsEmisionSesion.Emitir(emisor, identidad.Usuario, identidad.Rol, clsEmisionSesion.NuevoRefresh(
                        identidad.Usuario.Id, ahora, configuracion.VigenciaRefresh, familiaId: null, identidad.Rol, out var refresh));
                    tokens.Agregar(refresh);
                    emitido = clsResultadoLogin.ConSesion(sesion);
                }

                try
                {
                    // Con elección pendiente se guardan igual el alta JIT (usuario y asignaciones) y los datos corporativos refrescados.
                    await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
                    resultado = emitido;
                    if (emitido.Eleccion is not null)
                    {
                        motivo = clsMotivosAcceso.EleccionRolPendiente;
                    }
                }
                catch (clsViolacionUnicidadException ex)
                {
                    // Dos primeros ingresos simultáneos de la misma persona: el otro ya la creó; reintentar entra.
                    logger.LogWarning("Alta JIT concurrente de '{IdUsuario}' ({Restriccion}).", idUsuario, ex.Restriccion);
                    motivo = clsMotivosAcceso.AltaConcurrente;
                }
            }
            else
            {
                logger.LogInformation("Login rechazado para '{IdUsuario}': {Motivo}.", idUsuario, motivo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error inesperado en el login de '{IdUsuario}'.", idUsuario);
            motivo = clsMotivosAcceso.ErrorInterno;
            resultado = null;
        }

        await auditoria.RegistrarAsync(
            clsCodigosAcceso.HerramientaLogin,
            new { IdUsuario = idUsuario, NombreUsuarioInformado = solicitud.NombreUsuario, RolesInformados = solicitud.Roles },
            new
            {
                Resultado = resultado is null ? "rechazado" : resultado.Eleccion is null ? "aceptado" : "eleccion_pendiente",
                Motivo = motivo,
                Regla = regla,
                UsuarioId = resultado?.Sesion?.Usuario.Id ?? resultado?.Eleccion?.UsuarioId ?? usuarioRechazado,
                Rol = resultado?.Sesion?.Usuario.Rol,
                RolesDisponibles = resultado?.Eleccion?.Roles.Select(r => r.Codigo).ToList(),
                // Deltas de la reconciliación de roles (actor "sistema/login", sin revisor humano); solo cuando hubo cambios y
                // se guardaron (si el login no terminó aceptado/elección, los cambios no se persistieron y no se auditan como aplicados).
                RolesOtorgados = resultado is null ? null : rolesOtorgados,
                RolesRevocados = resultado is null ? null : rolesRevocados,
                ActorReconciliacion = resultado is null || (rolesOtorgados is null && rolesRevocados is null)
                    ? null : clsCodigosAcceso.ActorReconciliacion,
            },
            resultado is not null,
            clsEmisionSesion.DuracionMs(inicio),
            clsCodigosAcceso.PrefijoHilo + idUsuario);

        return resultado is null ? clsCodigosAcceso.ErrorCredencialesInvalidas() : resultado;
    }

    private clsEleccionRolPendiente EleccionPendiente(clsIdentidadVerificada identidad)
    {
        var nombreUsuario = identidad.Usuario.NombreUsuario
            ?? throw new InvalidOperationException("El usuario verificado no tiene nombreUsuario.");
        var ticket = tickets.Emitir(identidad.Usuario.Id, nombreUsuario, [.. identidad.RolesDisponibles.Select(r => r.Codigo)]);
        return new clsEleccionRolPendiente(
            ticket.Ticket,
            ticket.ExpiraAt,
            [.. identidad.RolesDisponibles.Select(r => new clsRolDisponible(clsCodigoEnum.ACodigo(r.Codigo), r.Descripcion ?? clsCodigoEnum.ACodigo(r.Codigo)))],
            identidad.Usuario.Id,
            nombreUsuario,
            identidad.Usuario.NombreCompleto ?? nombreUsuario);
    }
}

/// <summary>Armado común de la sesión del login y del refresh.</summary>
internal static class clsEmisionSesion
{
    /// <summary>
    /// Genera un refresh opaco y su entidad (solo con el hash). <paramref name="familiaId"/> nulo inicia familia. <paramref name="rol"/>
    /// es el rol de la sesión (<c>TokensRefresco.rolSesion</c>).
    /// </summary>
    public static string NuevoRefresh(
        Guid usuarioId, DateTimeOffset ahora, TimeSpan vigencia, Guid? familiaId, RolInterno rol, out clsTokenRefresco entidad)
    {
        var plano = clsTokenOpaco.Generar();
        var emitido = clsTokenRefresco.Emitir(usuarioId, clsTokenOpaco.Hash(plano), ahora, vigencia, familiaId, rol);
        entidad = emitido.EsExitoso
            ? emitido.Valor
            : throw new InvalidOperationException($"No se pudo emitir el refresh: {emitido.Error.Mensaje}");
        return plano;
    }

    public static clsSesionEmitida Emitir(IClsEmisorTokens emisor, clsUsuarioWeb usuario, RolInterno rol, string refresh)
    {
        var nombreUsuario = usuario.NombreUsuario
            ?? throw new InvalidOperationException("El usuario verificado no tiene nombreUsuario.");
        var acceso = emisor.EmitirAcceso(usuario.Id, nombreUsuario, rol);
        return new clsSesionEmitida(
            acceso.Token,
            refresh,
            new clsUsuarioSesion(
                usuario.Id,
                nombreUsuario,
                usuario.Email ?? string.Empty,
                usuario.NombreCompleto ?? nombreUsuario,
                clsCodigoEnum.ACodigo(rol),
                usuario.Regional),
            acceso.ExpiraAt);
    }

    public static int DuracionMs(long inicio) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
}
