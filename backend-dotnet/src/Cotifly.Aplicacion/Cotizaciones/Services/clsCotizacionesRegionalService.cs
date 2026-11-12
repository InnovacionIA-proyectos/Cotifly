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
/// Listado regional del gerente comercial (<c>GET /web/cotizaciones/regional</c>): las cotizaciones de los miembros de su
/// regional, fila por fila, con la forma de "Mis cotizaciones" y el comercial dueño. El alcance se resuelve en el servidor con
/// <see cref="IClsRegionalUsuario.ResolverAlcanceAsync"/> (la misma regla que <c>GET /web/metricas/regional</c>): el gerente solo la suya;
/// el director todas o la que pide con <c>?regional=</c>.
/// </summary>
public interface IClsCotizacionesRegionalService
{
    /// <summary>
    /// Cotizaciones del alcance del usuario autenticado (<see cref="IClsRegionalUsuario.ResolverAlcanceAsync"/>): la regional del gerente;
    /// para el director, todas o la de <paramref name="regional"/>. Errores: <c>NO_AUTENTICADO</c>, <c>SIN_PERMISO</c> (no
    /// es <c>gerente_comercial</c> ni <c>director</c>), <c>REGIONAL_NO_PERMITIDA</c> (el gerente pidió otra regional),
    /// <c>REGIONAL_NO_ASIGNADA</c> y <c>REGIONAL_NO_DISPONIBLE</c>.
    /// </summary>
    Task<clsResultado<clsCotizacionesRegional>> ListarAsync(string? regional = null, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsCotizacionesRegionalService"/>.</summary>
public sealed class clsCotizacionesRegionalService(
    IClsContextoUsuario usuario,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsCotizacionRepository cotizaciones,
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsCotizacionesRegionalService
{
    public const string MensajeSinPermiso = "Solo el gerente comercial o el director ven las cotizaciones de la regional.";

    public async Task<clsResultado<clsCotizacionesRegional>> ListarAsync(string? regional = null, CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is not { } rol)
        {
            return ErrorNoAutenticado();
        }

        // La política GerenteComercial ya filtra en la Api; se revalida aquí por si el caso de uso se llama desde otro lado.
        if (!rol.EsGerencial())
        {
            return new clsErrorDominio(clsCodigosAplicacion.SinPermiso, MensajeSinPermiso);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (gerente is null)
        {
            return ErrorNoAutenticado();
        }

        // El alcance (la regional del gerente; todas o la elegida para el director) lo decide IClsRegionalUsuario.
        var alcance = await regionales.ResolverAlcanceAsync(gerente, rol, regional, cancellationToken);
        if (alcance.EsFallo)
        {
            return alcance.Error;
        }

        var porId = alcance.Valor.Miembros.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var filas = await cotizaciones.ListarPorUsuariosAsync([.. porId.Keys], IClsCotizacionRepository.LimitePorDefecto, cancellationToken);
        var baseUrl = opcionesPdf.Value.BaseUrl;
        var resumen = new List<clsCotizacionResumen>(filas.Count);
        foreach (var fila in filas)
        {
            // Defensa: nunca una fila de alguien fuera de la regional, aunque el repositorio la devolviera.
            if (fila.Cotizacion.UsuarioId is { } dueno && porId.TryGetValue(dueno, out var miembro))
            {
                resumen.Add(clsCotizacionResumen.Desde(fila.Cotizacion, fila.LineaNegocio, baseUrl, fila.MargenAplicado, fila.OrigenMargen, fila.Carga) with
                {
                    Comercial = new clsComercialCotizacion(miembro.Id, miembro.NombreUsuario, miembro.NombreCompleto),
                });
            }
        }

        return new clsCotizacionesRegional(alcance.Valor.Regional, alcance.Valor.Fuente, resumen.Count, resumen);
    }

    private static clsErrorDominio ErrorNoAutenticado() => new(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
}
