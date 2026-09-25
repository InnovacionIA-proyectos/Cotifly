using Cotifly.Aplicacion.Metricas.DTOs;

namespace Cotifly.Aplicacion.Metricas.Repositories;

/// <summary>
/// Puerto de lectura de las vistas de métricas de <c>dbo_cotifly</c> (<c>vActividadPorComercial</c>,
/// <c>vAjustesMargenPorComercial</c>) con el nombre del comercial (JOIN con <c>UsuariosWeb</c>), filtradas por alcance.
/// </summary>
public interface IClsMetricasRepository
{
    Task<IReadOnlyList<clsActividadComercial>> ListarActividadAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<clsAjustesMargenComercial>> ListarAjustesMargenAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default);
}
