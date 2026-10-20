using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Margen.Repositories;

/// <summary>Puerto de los ajustes (negociaciones) de margen (<c>MargenAjustes</c>).</summary>
public interface IClsMargenAjusteRepository
{
    void Agregar(clsMargenAjuste ajuste);

    /// <summary>Ajustes de una cotización, del más antiguo al más reciente.</summary>
    Task<IReadOnlyList<clsMargenAjuste>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-028 (#186): deja sin ruta (<c>lineaSolicitudId = NULL</c>) los ajustes de las <paramref name="rutas"/> que se quitan al editar la
    /// solicitud (conservan la cotización y el folio). Se escribe al guardar la unidad de trabajo, antes de borrar las rutas. Devuelve cuántos.
    /// </summary>
    Task<int> DesvincularRutasAsync(IReadOnlyCollection<Guid> rutas, CancellationToken cancellationToken = default);
}
