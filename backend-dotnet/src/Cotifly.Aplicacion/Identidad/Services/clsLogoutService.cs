using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary><c>POST /api/auth/logout</c>: siempre termina sin error (el endpoint responde 204, anti-enumeración).</summary>
public interface IClsLogoutService
{
    Task CerrarSesionAsync(clsSolicitudLogout solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsLogoutService"/> (ADR-009): revoca la familia del refresh recibido (toda la cadena de
/// rotación de esa sesión) y registra el <c>jti</c> del access en <c>TokensRevocados</c> hasta su vencimiento, además de
/// revocarlo al instante en este proceso (<see cref="IClsRevocacionAccesos.RegistrarLocal"/>). Un refresh desconocido,
/// un <c>jti</c> repetido o un fallo de la base no cambian la respuesta: quedan en el log y en la auditoría
/// (herramienta <c>logout</c>).
/// </summary>
public sealed class clsLogoutService(
    IClsTokenRefrescoRepository tokens,
    IClsTokenRevocadoRepository revocados,
    IClsRevocacionAccesos revocacion,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsAuditoriaService auditoria,
    ILogger<clsLogoutService> logger) : IClsLogoutService
{
    public async Task CerrarSesionAsync(clsSolicitudLogout solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);

        var inicio = Stopwatch.GetTimestamp();
        var refreshRevocados = 0;
        var accesoRevocado = false;
        var exito = true;
        try
        {
            var ahora = reloj.AhoraUtc;
            if (!string.IsNullOrWhiteSpace(solicitud.Jti) && solicitud.AccesoExpiraAt is { } expira)
            {
                revocacion.RegistrarLocal(solicitud.Jti, expira);
            }

            if (!string.IsNullOrWhiteSpace(solicitud.Refresh) && solicitud.Refresh.Length <= 512
                && await tokens.ObtenerPorHashAsync(clsTokenOpaco.Hash(solicitud.Refresh), cancellationToken) is { } token)
            {
                refreshRevocados = await tokens.RevocarFamiliaAsync(token.FamiliaId, ahora, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(solicitud.Jti) && solicitud.AccesoExpiraAt is { } vence
                && clsTokenRevocado.Crear(solicitud.Jti, vence, solicitud.UsuarioId) is { EsExitoso: true } revocado)
            {
                revocados.Agregar(revocado.Valor);
                try
                {
                    await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
                }
                catch (clsViolacionUnicidadException)
                {
                    // Logout repetido con el mismo access: el jti ya estaba revocado.
                }

                accesoRevocado = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("No se pudo completar el logout ({Tipo}): {Mensaje}", ex.GetType().Name, ex.Message);
            exito = false;
        }

        await auditoria.RegistrarAsync(
            clsCodigosAcceso.HerramientaLogout,
            new { solicitud.UsuarioId, ConRefresh = !string.IsNullOrWhiteSpace(solicitud.Refresh), ConAcceso = solicitud.Jti is not null },
            new { Motivo = exito ? clsMotivosAcceso.SesionCerrada : clsMotivosAcceso.ErrorInterno, RefreshRevocados = refreshRevocados, AccesoRevocado = accesoRevocado },
            exito,
            clsEmisionSesion.DuracionMs(inicio),
            threadId: null);
    }
}
