using System.Diagnostics;
using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Administracion.Services;

/// <summary>
/// Administración de las políticas de margen por línea de negocio (HU-004.9, <c>GET/PUT /web/admin/lineas-negocio</c>). Solo
/// el rol <c>gerente_comercial</c> (HU-010), revalidado aquí además de la política del endpoint. No crea ni borra líneas.
/// </summary>
public interface IClsLineasNegocioAdminService
{
    /// <summary>Todas las líneas (activas e inactivas), ordenadas por nombre. <see cref="clsCodigosAplicacion.SinPermiso"/> si no es gerente.</summary>
    Task<clsResultado<IReadOnlyList<clsLineaNegocioAdmin>>> ListarAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cambia margen, método y estado de una línea existente y audita el cambio (quién, antes y después). Errores:
    /// <see cref="clsCodigosAplicacion.SinPermiso"/> (403), <see cref="clsCodigosAplicacion.SolicitudInvalida"/> (400: falta un
    /// campo, margen fuera de 0–0,99 o método distinto de <c>margen</c>/<c>markup</c>) y
    /// <see cref="clsCodigosAplicacion.NoEncontrado"/> (404: la línea no existe).
    /// </summary>
    Task<clsResultado<clsLineaNegocioAdmin>> ActualizarAsync(int id, clsCambioLineaNegocio cambio, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsLineasNegocioAdminService"/>.</summary>
public sealed class clsLineasNegocioAdminService(
    IClsContextoUsuario usuario,
    IClsMargenPoliticaRepository politicas,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria) : IClsLineasNegocioAdminService
{
    /// <summary>Nombre del evento en <c>EventosHerramienta</c>.</summary>
    public const string HerramientaAuditoria = "admin_actualizar_linea_negocio";

    /// <summary>Margen máximo que admite el contrato (<c>ActualizarLineaNegocio.margen</c>), para ambos métodos.</summary>
    public const decimal MargenMaximo = 0.99m;

    public async Task<clsResultado<IReadOnlyList<clsLineaNegocioAdmin>>> ListarAsync(CancellationToken cancellationToken = default)
    {
        if (ValidarGerente() is { } error)
        {
            return error;
        }

        var lineas = await politicas.ListarAsync(cancellationToken);
        return lineas.Select(clsLineaNegocioAdmin.Desde).ToList();
    }

    public async Task<clsResultado<clsLineaNegocioAdmin>> ActualizarAsync(int id, clsCambioLineaNegocio cambio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cambio);
        var reloj = Stopwatch.StartNew();
        var entrada = new
        {
            usuario_id = usuario.UsuarioId,
            nombre_usuario = usuario.NombreUsuario,
            linea_negocio_id = id,
            margen = cambio.Margen,
            metodo_calculo = cambio.MetodoCalculo,
            is_active = cambio.IsActive,
        };

        var resultado = await AplicarAsync(id, cambio, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            entrada,
            resultado.EsExitoso ? resultado.Valor.Cambio : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)reloj.ElapsedMilliseconds,
            null);
        return resultado.EsExitoso ? resultado.Valor.Linea : resultado.Error;
    }

    private async Task<clsResultado<(clsLineaNegocioAdmin Linea, object Cambio)>> AplicarAsync(int id, clsCambioLineaNegocio cambio, CancellationToken cancellationToken)
    {
        if (ValidarGerente() is { } error)
        {
            return error;
        }

        if (cambio.Margen is not { } margen || string.IsNullOrWhiteSpace(cambio.MetodoCalculo) || cambio.IsActive is not { } activa)
        {
            return Invalida("Faltan campos: margen, metodo_calculo e is_active son obligatorios.");
        }

        if (margen is < 0m or > MargenMaximo)
        {
            return Invalida($"El margen debe estar entre 0 y {MargenMaximo.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        var politica = await politicas.ObtenerPorIdAsync(id, cancellationToken);
        if (politica is null)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, "La línea de negocio no existe.");
        }

        var antes = clsLineaNegocioAdmin.Desde(politica);
        var actualizada = politica.Actualizar(margen, cambio.MetodoCalculo, activa);
        if (actualizada.EsFallo)
        {
            return Invalida(actualizada.Error.Mensaje);
        }

        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        var despues = clsLineaNegocioAdmin.Desde(politica);
        return (despues, (object)new { antes, despues });
    }

    private clsErrorDominio? ValidarGerente() => usuario.UsuarioId is null
        ? new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.")
        : usuario.Rol == RolInterno.GerenteComercial
            ? null
            : new clsErrorDominio(clsCodigosAplicacion.SinPermiso, "No tienes permiso para esta operación.");

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);
}
