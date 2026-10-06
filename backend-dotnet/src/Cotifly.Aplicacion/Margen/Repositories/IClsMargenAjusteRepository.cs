using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Margen.Repositories;

/// <summary>Puerto de los ajustes (negociaciones) de margen (<c>MargenAjustes</c>).</summary>
public interface IClsMargenAjusteRepository
{
    void Agregar(clsMargenAjuste ajuste);

    /// <summary>Ajustes de una cotización, del más antiguo al más reciente.</summary>
    Task<IReadOnlyList<clsMargenAjuste>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);
}
