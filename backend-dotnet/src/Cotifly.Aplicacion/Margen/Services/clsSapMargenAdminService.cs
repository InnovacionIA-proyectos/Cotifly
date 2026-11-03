using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Administración de la sincronización del margen de SAP TM (HU-042.2, #243, ADR-027): <c>GET /web/admin/sap-margen/estado</c> y
/// <c>POST /web/admin/sap-margen/sincronizar</c>. Solo los roles gerenciales (gerente comercial y director), como el resto de
/// <c>/web/admin/*</c>; se revalida aquí además de la política del endpoint.
/// </summary>
public interface IClsSapMargenAdminService
{
    /// <summary>
    /// Estado de la copia local. Errores: <see cref="clsCodigosAplicacion.SinPermiso"/> (403) y <see cref="clsCodigosError.SapMargenNoConfigurado"/> (503).
    /// </summary>
    Task<clsResultado<clsEstadoSincronizacionMargenSap>> EstadoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fuerza una corrida (ignora el intervalo, no el candado) y espera su resultado: <c>ok</c>, <c>error</c> u <c>omitido</c>. Queda en la
    /// auditoría. Errores: <see cref="clsCodigosAplicacion.SinPermiso"/> (403), <see cref="clsCodigosError.SapMargenNoConfigurado"/> (503) y
    /// <see cref="clsCodigosError.SapMargenSincronizacionEnCurso"/> (409).
    /// </summary>
    Task<clsResultado<clsResultadoSincronizacionMargenSap>> SincronizarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsSapMargenAdminService"/>.</summary>
public sealed class clsSapMargenAdminService(
    IClsContextoUsuario usuario,
    IClsSincronizacionMargenSap sincronizacion,
    IClsAuditoriaService auditoria) : IClsSapMargenAdminService
{
    /// <summary>Nombre del evento en <c>EventosHerramienta</c>.</summary>
    public const string HerramientaAuditoria = "admin_sincronizar_margen_sap";

    public const string MensajeNoConfigurado =
        "La sincronización del margen de SAP TM no está configurada en este ambiente: el margen usa la copia local existente o la política de la línea.";

    public const string MensajeEnCurso = "Ya hay una sincronización del margen de SAP TM en curso. Intenta de nuevo cuando termine.";

    public async Task<clsResultado<clsEstadoSincronizacionMargenSap>> EstadoAsync(CancellationToken cancellationToken = default)
    {
        if (Validar() is { } error)
        {
            return error;
        }

        return await sincronizacion.EstadoAsync(cancellationToken);
    }

    public async Task<clsResultado<clsResultadoSincronizacionMargenSap>> SincronizarAsync(CancellationToken cancellationToken = default)
    {
        if (Validar() is { } error)
        {
            return error;
        }

        var reloj = Stopwatch.StartNew();
        var resultado = await sincronizacion.SincronizarAsync(forzada: true, cancellationToken);
        clsResultado<clsResultadoSincronizacionMargenSap> respuesta = resultado.Estado switch
        {
            clsResultadoSincronizacionMargenSap.EstadoEnCurso => new clsErrorDominio(clsCodigosError.SapMargenSincronizacionEnCurso, MensajeEnCurso),
            clsResultadoSincronizacionMargenSap.EstadoNoConfigurada => new clsErrorDominio(clsCodigosError.SapMargenNoConfigurado, MensajeNoConfigurado),
            _ => resultado,
        };

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { usuario_id = usuario.UsuarioId, nombre_usuario = usuario.NombreUsuario },
            new
            {
                estado = resultado.Estado,
                filas_recibidas = resultado.FilasRecibidas,
                filas_insertadas = resultado.Corrida?.FilasInsertadas,
                filas_actualizadas = resultado.Corrida?.FilasActualizadas,
                filas_desactivadas = resultado.Corrida?.FilasDesactivadas,
                mensaje_error = resultado.Corrida?.MensajeError,
            },
            resultado.Estado == clsResultadoSincronizacionMargenSap.EstadoOk,
            (int)reloj.ElapsedMilliseconds,
            null);
        return respuesta;
    }

    private clsErrorDominio? Validar()
    {
        if (usuario.UsuarioId is null)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        if (!usuario.Rol.EsGerencial())
        {
            return new clsErrorDominio(clsCodigosAplicacion.SinPermiso, "No tienes permiso para esta operación.");
        }

        return sincronizacion.Configurada ? null : new clsErrorDominio(clsCodigosError.SapMargenNoConfigurado, MensajeNoConfigurado);
    }
}
