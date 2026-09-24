using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Margen.Repositories;

/// <summary>Puerto de lectura/edición de la política de margen por línea (<c>MargenesPolitica</c>).</summary>
public interface IClsMargenPoliticaRepository
{
    /// <summary>
    /// Política de la línea por nombre exacto (la collation de la columna no distingue mayúsculas).
    /// La resolución tolerante a tildes es del caso de uso de margen (#10). Incluye líneas inactivas.
    /// </summary>
    Task<clsMargenPolitica?> ObtenerPorLineaAsync(string lineaNegocio, CancellationToken cancellationToken = default);

    /// <summary>Política por id (<c>Cotizaciones.lineaNegocioId</c>).</summary>
    Task<clsMargenPolitica?> ObtenerPorIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Todas las líneas, ordenadas por nombre (administración, HU-010).</summary>
    Task<IReadOnlyList<clsMargenPolitica>> ListarAsync(CancellationToken cancellationToken = default);
}
