using Cotifly.Aplicacion.Metricas.DTOs;

namespace Cotifly.Aplicacion.Metricas.Repositories;

/// <summary>
/// Puerto de lectura de las métricas de <c>dbo_cotifly</c>: las filas del tablero de métricas (ADR-016).
/// </summary>
public interface IClsMetricasRepository
{
    /// <summary>
    /// Una fila por cadena de versiones <c>(usuarioId, folio base)</c> de <paramref name="usuarios"/> cuya primera creación
    /// (<c>MIN(createdAt)</c>) cae en [<paramref name="desde"/>, <paramref name="hastaExclusivo"/>): valores de la versión vigente,
    /// resultado más reciente de la cadena, cantidad de versiones y resumen de los <c>MargenAjustes</c> de la vigente.
    /// </summary>
    Task<clsDatosMetricas> ListarCotizacionesAsync(
        IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hastaExclusivo, CancellationToken cancellationToken = default);

    /// <summary>Líneas de <c>MargenesPolitica</c> con <c>isActive = 1</c>.</summary>
    Task<int> ContarLineasNegocioActivasAsync(CancellationToken cancellationToken = default);
}
