using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;

namespace Cotifly.Aplicacion.Administracion.Services;

/// <summary>
/// HU-016.1 (#154, decisión P4b del 2026-10-27): <c>GET /web/lineas-negocio</c>, las líneas de negocio activas para el desplegable
/// "Línea de negocio" de cada ruta en "Nueva solicitud" (comercial, gerente y director). Nunca expone el margen ni el método de la
/// política (eso es de <see cref="IClsLineasNegocioAdminService"/>).
/// </summary>
public interface IClsLineasNegocioCatalogoService
{
    /// <summary>Las líneas activas ordenadas por nombre, con su etiqueta.</summary>
    Task<IReadOnlyList<clsLineaNegocioOpcion>> ListarActivasAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsLineasNegocioCatalogoService"/>. Lee <c>MargenesPolitica</c> en cada petición (6 a 8 filas por
/// clave primaria): un cambio de la administración se ve de inmediato y no hace falta caché.
/// </summary>
public sealed class clsLineasNegocioCatalogoService(IClsMargenPoliticaRepository politicas) : IClsLineasNegocioCatalogoService
{
    public async Task<IReadOnlyList<clsLineaNegocioOpcion>> ListarActivasAsync(CancellationToken cancellationToken = default)
    {
        var lineas = await politicas.ListarAsync(cancellationToken);
        return [.. lineas
            .Where(l => l.Activa)
            .Select(l => new clsLineaNegocioOpcion(l.LineaNegocio, clsLineaNegocioOpcion.EtiquetaDe(l.LineaNegocio)))
            .OrderBy(l => l.LineaNegocio, StringComparer.OrdinalIgnoreCase)];
    }
}
