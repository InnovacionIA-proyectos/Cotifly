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
/// genérico para todo rechazo; el motivo real queda en la auditoría y en el log).
/// </summary>
public interface IClsLoginClienteProService
{
    Task<clsResultado<clsSesionEmitida>> IniciarSesionAsync(clsSolicitudLoginClientePro solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsLoginClienteProService"/>: valida el cuerpo, hace la verificación doble
/// (<see cref="clsVerificadorIdentidad"/>, con alta JIT en el primer ingreso), actualiza <c>ultimaSesion</c>, emite el
/// access (JWT) y el refresh opaco (se guarda su SHA-256, nueva familia) y audita cada intento en
/// <c>EventosHerramienta</c> (herramienta <c>login_clientepro</c>, hilo <c>auth/{idUsuario}</c>). Cualquier excepción
/// inesperada también responde el 401 genérico (fail-closed); solo la cancelación del llamador se propaga.
/// </summary>
public sealed class clsLoginClienteProService(
    clsVerificadorIdentidad verificador,
    IClsTokenRefrescoRepository tokens,
    IClsEmisorTokens emisor,
    IClsConfiguracionJwt configuracion,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    ILogger<clsLoginClienteProService> logger) : IClsLoginClienteProService
{
    public async Task<clsResultado<clsSesionEmitida>> IniciarSesionAsync(
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
        clsSesionEmitida? sesion = null;
        try
        {
            var ahora = reloj.AhoraUtc;
            var verificacion = await verificador.VerificarAsync(idUsuario, permitirAlta: true, ahora, cancellationToken);
            motivo = verificacion.Motivo;
            regla = verificacion.Regla;
            usuarioRechazado = verificacion.UsuarioIdRechazado;
            if (verificacion.Identidad is { } identidad)
            {
                identidad.Usuario.RegistrarSesion(ahora);
                var emitida = clsEmisionSesion.Emitir(emisor, identidad.Usuario, identidad.Rol, clsEmisionSesion.NuevoRefresh(
                    identidad.Usuario.Id, ahora, configuracion.VigenciaRefresh, familiaId: null, identidad.Rol, out var refresh));
                tokens.Agregar(refresh);
                try
                {
                    await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
                    sesion = emitida;
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
            sesion = null;
        }

        await auditoria.RegistrarAsync(
            clsCodigosAcceso.HerramientaLogin,
            new { IdUsuario = idUsuario, NombreUsuarioInformado = solicitud.NombreUsuario, RolesInformados = solicitud.Roles },
            new
            {
                Resultado = sesion is null ? "rechazado" : "aceptado",
                Motivo = motivo,
                Regla = regla,
                UsuarioId = sesion?.Usuario.Id ?? usuarioRechazado,
                Rol = sesion?.Usuario.Rol,
            },
            sesion is not null,
            clsEmisionSesion.DuracionMs(inicio),
            clsCodigosAcceso.PrefijoHilo + idUsuario);

        return sesion is null ? clsCodigosAcceso.ErrorCredencialesInvalidas() : sesion;
    }
}

/// <summary>Armado común de la sesión del login y del refresh.</summary>
internal static class clsEmisionSesion
{
    /// <summary>
    /// Genera un refresh opaco y su entidad (solo con el hash). <paramref name="familiaId"/> nulo inicia familia. <paramref name="rol"/>
    /// es el rol de la sesión (HU-040.1, <c>TokensRefresco.rolSesion</c>).
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
