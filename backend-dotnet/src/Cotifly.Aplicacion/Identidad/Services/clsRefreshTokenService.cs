using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// <c>POST /api/auth/refresh</c>: rota el refresh y emite un par nuevo. Todo rechazo es
/// <see cref="clsCodigosAcceso.SesionExpirada"/> (401 "Inicia sesión de nuevo.").
/// </summary>
public interface IClsRefreshTokenService
{
    Task<clsResultado<clsSesionEmitida>> RenovarAsync(string? refresh, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsRefreshTokenService"/> (ADR-009):
/// <list type="bullet">
/// <item>Busca el token por su SHA-256. Desconocido o vencido: 401.</item>
/// <item>Ya revocado o rotado: se trata como robo (reuso), 401 y se revocan todos los refresh vigentes del usuario.
/// Excepción: si fue rotado hace menos de <see cref="GraciaRotacionConcurrente"/>, es la otra mitad de dos renovaciones
/// simultáneas del mismo cliente (dos pestañas): 401 sin revocar nada.</item>
/// <item>Vuelve a hacer la verificación doble (almacén, rol externo, asignación, vínculo); si ya no pasa, 401.</item>
/// <item>HU-040.2 (#230, ADR-025): conserva el rol de la sesión (<c>TokensRefresco.rolSesion</c>, el elegido o el único) y exige que siga
/// entre los roles disponibles; si se lo quitaron, 401 <c>rol_externo_cambio</c>. Un refresh anterior al 16 (sin rol) de quien hoy tiene
/// varios roles no adivina: 401 <c>eleccion_rol_requerida</c> (vuelve a entrar y elige). El rol viaja en el refresh nuevo.</item>
/// <item>Rota de forma atómica (<see cref="IClsTokenRefrescoRepository.RotarAsync"/>): el nuevo hereda la familia y el
/// vencimiento absoluto (30 días desde el login; la rotación no extiende la sesión). Si otra renovación ganó: 401.</item>
/// </list>
/// Audita cada intento con token conocido (herramienta <c>refresh_sesion</c>, hilo <c>auth/{nombreUsuario}</c>).
/// </summary>
public sealed class clsRefreshTokenService(
    clsVerificadorIdentidad verificador,
    IClsTokenRefrescoRepository tokens,
    IClsUsuarioWebRepository usuarios,
    IClsEmisorTokens emisor,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    ILogger<clsRefreshTokenService> logger) : IClsRefreshTokenService
{
    /// <summary>Ventana en la que el reuso de un token recién rotado se trata como renovación concurrente, no como robo.</summary>
    public static readonly TimeSpan GraciaRotacionConcurrente = TimeSpan.FromSeconds(10);

    public async Task<clsResultado<clsSesionEmitida>> RenovarAsync(string? refresh, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refresh) || refresh.Length > 512)
        {
            return clsCodigosAcceso.ErrorSesionExpirada();
        }

        var inicio = Stopwatch.GetTimestamp();
        string motivo;
        string? regla = null;
        string? nombreUsuario = null;
        Guid? usuarioId = null;
        clsSesionEmitida? sesion = null;
        try
        {
            var ahora = reloj.AhoraUtc;
            var token = await tokens.ObtenerPorHashAsync(clsTokenOpaco.Hash(refresh), cancellationToken);
            usuarioId = token?.UsuarioId;
            if (token is null)
            {
                motivo = clsMotivosAcceso.RefreshDesconocido;
            }
            else if (token.RevocadoAt is { } revocadoAt)
            {
                if (token.ReemplazadoPorId is not null && ahora - revocadoAt < GraciaRotacionConcurrente)
                {
                    motivo = clsMotivosAcceso.RotacionConcurrente;
                }
                else
                {
                    var revocados = await tokens.RevocarActivosDelUsuarioAsync(token.UsuarioId, ahora, cancellationToken);
                    logger.LogWarning(
                        "Reuso de un refresh ya revocado del usuario {UsuarioId}: se revocaron {Cantidad} refresh vigentes.",
                        token.UsuarioId, revocados);
                    motivo = clsMotivosAcceso.RefreshReutilizado;
                }
            }
            else if (token.ExpiraAt <= ahora)
            {
                motivo = clsMotivosAcceso.RefreshVencido;
            }
            else
            {
                var usuario = await usuarios.ObtenerPorIdAsync(token.UsuarioId, cancellationToken);
                nombreUsuario = usuario?.NombreUsuario;
                if (usuario is null || string.IsNullOrWhiteSpace(nombreUsuario))
                {
                    motivo = clsMotivosAcceso.UsuarioNoEncontrado;
                }
                else
                {
                    var verificacion = await verificador.VerificarAsync(nombreUsuario, permitirAlta: false, ahora, cancellationToken);
                    motivo = verificacion.Motivo;
                    regla = verificacion.Regla;
                    if (verificacion.Identidad is { } identidad && identidad.Usuario.Id != usuario.Id)
                    {
                        motivo = clsMotivosAcceso.IdentidadInconsistente;
                    }
                    else if (verificacion.Identidad is { } valida)
                    {
                        var (rolSesion, rechazoRol) = RolDeLaSesion(token, valida);
                        if (rolSesion is not { } rol)
                        {
                            motivo = rechazoRol!;
                        }
                        else
                        {
                            var plano = clsEmisionSesion.NuevoRefresh(usuario.Id, ahora, token.ExpiraAt - ahora, token.FamiliaId, rol, out var nuevo);
                            if (await tokens.RotarAsync(token.Id, nuevo, ahora, cancellationToken))
                            {
                                sesion = clsEmisionSesion.Emitir(emisor, valida.Usuario, rol, plano);
                                motivo = clsMotivosAcceso.Renovado;
                            }
                            else
                            {
                                motivo = clsMotivosAcceso.RotacionConcurrente;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error inesperado al renovar la sesión.");
            motivo = clsMotivosAcceso.ErrorInterno;
            sesion = null;
        }

        if (sesion is null)
        {
            logger.LogInformation("Refresh rechazado: {Motivo}.", motivo);
        }

        await auditoria.RegistrarAsync(
            clsCodigosAcceso.HerramientaRefresh,
            new { UsuarioId = usuarioId },
            new { Resultado = sesion is null ? "rechazado" : "aceptado", Motivo = motivo, Regla = regla, Rol = sesion?.Usuario.Rol },
            sesion is not null,
            clsEmisionSesion.DuracionMs(inicio),
            nombreUsuario is null ? null : clsCodigosAcceso.PrefijoHilo + nombreUsuario);

        return sesion is null ? clsCodigosAcceso.ErrorSesionExpirada() : sesion;
    }

    /// <summary>
    /// HU-040.2 (#230): el rol con el que se renueva. Con <c>rolSesion</c>, ese mismo si sigue disponible (si no, <c>rol_externo_cambio</c>).
    /// Sin él (refresh anterior al script 16), el rol único de la verificación, o <c>eleccion_rol_requerida</c> si ahora tiene varios.
    /// </summary>
    private static (RolInterno? Rol, string? Motivo) RolDeLaSesion(clsTokenRefresco token, clsIdentidadVerificada identidad)
    {
        if (token.RolSesion is { } guardado)
        {
            return identidad.PuedeEntrarCon(guardado) ? (guardado, null) : (null, clsMotivosAcceso.RolExternoCambio);
        }

        return identidad.RequiereEleccion ? (null, clsMotivosAcceso.EleccionRolRequerida) : (identidad.Rol, null);
    }
}
