using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Listado regional del gerente comercial (HU-016.1, #154; <c>GET /web/cotizaciones/regional</c>): las cotizaciones de los miembros de su
/// regional, fila por fila, con la forma de "Mis cotizaciones" y el comercial dueño. La regional se resuelve en el servidor con
/// <see cref="IClsRegionalUsuario"/> (la misma regla que <c>GET /web/metricas/regional</c>); nunca se acepta por parámetro.
/// </summary>
public interface IClsCotizacionesRegionalService
{
    /// <summary>
    /// Cotizaciones de la regional del usuario autenticado. Errores: <c>NO_AUTENTICADO</c>, <c>SIN_PERMISO</c> (no es
    /// <c>gerente_comercial</c>), <c>REGIONAL_NO_ASIGNADA</c> y <c>REGIONAL_NO_DISPONIBLE</c>.
    /// </summary>
    Task<clsResultado<clsCotizacionesRegional>> ListarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsCotizacionesRegionalService"/>.</summary>
public sealed class clsCotizacionesRegionalService(
    IClsContextoUsuario usuario,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsCotizacionRepository cotizaciones,
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsCotizacionesRegionalService
{
    public const string MensajeSinPermiso = "Solo el gerente comercial ve las cotizaciones de la regional.";

    public async Task<clsResultado<clsCotizacionesRegional>> ListarAsync(CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is not { } rol)
        {
            return ErrorNoAutenticado();
        }

        // La política GerenteComercial ya filtra en la Api; se revalida aquí por si el caso de uso se llama desde otro lado.
        if (rol != RolInterno.GerenteComercial)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SinPermiso, MensajeSinPermiso);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (gerente is null)
        {
            return ErrorNoAutenticado();
        }

        var regional = await regionales.ResolverAsync(gerente, cancellationToken);
        if (regional.EsFallo)
        {
            return regional.Error;
        }

        var miembros = await regionales.ListarMiembrosAsync(regional.Valor.Regional, cancellationToken);
        if (miembros.EsFallo)
        {
            return miembros.Error;
        }

        var porId = miembros.Valor.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var filas = await cotizaciones.ListarPorUsuariosAsync([.. porId.Keys], IClsCotizacionRepository.LimitePorDefecto, cancellationToken);
        var baseUrl = opcionesPdf.Value.BaseUrl;
        var resumen = new List<clsCotizacionResumen>(filas.Count);
        foreach (var fila in filas)
        {
            // Defensa: nunca una fila de alguien fuera de la regional, aunque el repositorio la devolviera.
            if (fila.Cotizacion.UsuarioId is { } dueno && porId.TryGetValue(dueno, out var miembro))
            {
                resumen.Add(clsCotizacionResumen.Desde(fila.Cotizacion, fila.LineaNegocio, baseUrl, fila.MargenAplicado, fila.OrigenMargen) with
                {
                    Comercial = new clsComercialCotizacion(miembro.Id, miembro.NombreUsuario, miembro.NombreCompleto),
                });
            }
        }

        return new clsCotizacionesRegional(regional.Valor.Regional, regional.Valor.Fuente, resumen.Count, resumen);
    }

    private static clsErrorDominio ErrorNoAutenticado() => new(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
}
