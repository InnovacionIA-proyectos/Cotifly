using Cotifly.Aplicacion.Metricas.DTOs;

namespace Cotifly.Aplicacion.Metricas.Repositories;

/// <summary>
/// Puerto de lectura de las métricas de <c>dbo_cotifly</c>: las vistas del borrador (<c>vActividadPorComercial</c>,
/// <c>vAjustesMargenPorComercial</c>) con el nombre del comercial, y las filas del tablero de HU-010.1 (ADR-016).
/// </summary>
public interface IClsMetricasRepository
{
    Task<IReadOnlyList<clsActividadComercial>> ListarActividadAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<clsAjustesMargenComercial>> ListarAjustesMargenAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cotizaciones de <paramref name="usuarios"/> con <c>createdAt</c> en [<paramref name="desde"/>, <paramref name="hastaExclusivo"/>),
    /// todas las versiones, cada una marcada si es la vigente de su cadena <c>(usuarioId, folio base)</c> (decidido sobre toda
    /// la historia, no solo el periodo), con el resumen de sus <c>MargenAjustes</c>.
    /// </summary>
    Task<clsDatosMetricas> ListarCotizacionesAsync(
        IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hastaExclusivo, CancellationToken cancellationToken = default);

    /// <summary>Líneas de <c>MargenesPolitica</c> con <c>isActive = 1</c>.</summary>
    Task<int> ContarLineasNegocioActivasAsync(CancellationToken cancellationToken = default);
}
