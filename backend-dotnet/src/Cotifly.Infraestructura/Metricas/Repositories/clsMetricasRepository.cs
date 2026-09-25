using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Metricas.Repositories;

/// <summary>
/// Implementación de <see cref="IClsMetricasRepository"/> con <c>Database.SqlQuery</c> sobre las vistas del 0001 (corregida
/// la de actividad por el 0003) y un JOIN con <c>UsuariosWeb</c>. Las vistas no se mapean como entidades sin clave del
/// <see cref="clsCotiflyDbContext"/> a propósito: el modelo EF es Database First de tablas y la prueba de deriva lo compara
/// con el catálogo en ambos sentidos. Las consultas son SQL parametrizado (interpolación de EF Core → <c>@p0</c>, <c>@p1</c>).
/// </summary>
public sealed class clsMetricasRepository(clsCotiflyDbContext contexto) : IClsMetricasRepository
{
    public async Task<IReadOnlyList<clsActividadComercial>> ListarActividadAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alcance);
        var consulta = alcance.UsuarioId is { } usuarioId
            ? contexto.Database.SqlQuery<clsFilaActividad>($"""
                SELECT v.usuarioId AS UsuarioId, u.nombreCompleto AS NombreCompleto, v.numCotizaciones AS NumCotizaciones,
                       v.numCotizacionesEnviadas AS NumCotizacionesEnviadas, v.numAjustes AS NumAjustes, v.ultimaCotizacion AS UltimaCotizacion
                FROM dbo_cotifly.vActividadPorComercial v
                LEFT JOIN dbo_cotifly.UsuariosWeb u ON u.id = v.usuarioId
                WHERE v.usuarioId = {usuarioId}
                """)
            : contexto.Database.SqlQuery<clsFilaActividad>($"""
                SELECT v.usuarioId AS UsuarioId, u.nombreCompleto AS NombreCompleto, v.numCotizaciones AS NumCotizaciones,
                       v.numCotizacionesEnviadas AS NumCotizacionesEnviadas, v.numAjustes AS NumAjustes, v.ultimaCotizacion AS UltimaCotizacion
                FROM dbo_cotifly.vActividadPorComercial v
                INNER JOIN dbo_cotifly.UsuariosWeb u ON u.id = v.usuarioId
                WHERE u.regional = {alcance.Regional!}
                """);

        var filas = await consulta.ToListAsync(cancellationToken);
        return filas
            .OrderBy(f => f.NombreCompleto ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(f => f.UsuarioId)
            .Select(f => new clsActividadComercial(f.UsuarioId, f.NombreCompleto, f.NumCotizaciones, f.NumCotizacionesEnviadas, f.NumAjustes, f.UltimaCotizacion))
            .ToList();
    }

    public async Task<IReadOnlyList<clsAjustesMargenComercial>> ListarAjustesMargenAsync(clsAlcanceMetricas alcance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alcance);
        var consulta = alcance.UsuarioId is { } usuarioId
            ? contexto.Database.SqlQuery<clsFilaAjustes>($"""
                SELECT v.usuarioId AS UsuarioId, u.nombreCompleto AS NombreCompleto, v.lineaNegocio AS LineaNegocio, v.numAjustes AS NumAjustes,
                       v.diferenciaPromedio AS DiferenciaPromedio, v.diferenciaMin AS DiferenciaMin, v.diferenciaMax AS DiferenciaMax,
                       v.ultimoAjuste AS UltimoAjuste
                FROM dbo_cotifly.vAjustesMargenPorComercial v
                LEFT JOIN dbo_cotifly.UsuariosWeb u ON u.id = v.usuarioId
                WHERE v.usuarioId = {usuarioId}
                """)
            : contexto.Database.SqlQuery<clsFilaAjustes>($"""
                SELECT v.usuarioId AS UsuarioId, u.nombreCompleto AS NombreCompleto, v.lineaNegocio AS LineaNegocio, v.numAjustes AS NumAjustes,
                       v.diferenciaPromedio AS DiferenciaPromedio, v.diferenciaMin AS DiferenciaMin, v.diferenciaMax AS DiferenciaMax,
                       v.ultimoAjuste AS UltimoAjuste
                FROM dbo_cotifly.vAjustesMargenPorComercial v
                INNER JOIN dbo_cotifly.UsuariosWeb u ON u.id = v.usuarioId
                WHERE u.regional = {alcance.Regional!}
                """);

        var filas = await consulta.ToListAsync(cancellationToken);
        return filas
            .OrderBy(f => f.NombreCompleto ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(f => f.UsuarioId)
            .ThenBy(f => f.LineaNegocio ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(f => new clsAjustesMargenComercial(
                f.UsuarioId, f.NombreCompleto, f.LineaNegocio, f.NumAjustes, f.DiferenciaPromedio, f.DiferenciaMin, f.DiferenciaMax, f.UltimoAjuste))
            .ToList();
    }

    /// <summary>Fila cruda de <c>vActividadPorComercial</c> (materializada por nombre de columna).</summary>
    internal sealed class clsFilaActividad
    {
        public Guid UsuarioId { get; set; }
        public string? NombreCompleto { get; set; }
        public int NumCotizaciones { get; set; }
        public int NumCotizacionesEnviadas { get; set; }
        public int NumAjustes { get; set; }
        public DateTimeOffset? UltimaCotizacion { get; set; }
    }

    /// <summary>Fila cruda de <c>vAjustesMargenPorComercial</c>.</summary>
    internal sealed class clsFilaAjustes
    {
        public Guid? UsuarioId { get; set; }
        public string? NombreCompleto { get; set; }
        public string? LineaNegocio { get; set; }
        public int NumAjustes { get; set; }
        public decimal? DiferenciaPromedio { get; set; }
        public decimal? DiferenciaMin { get; set; }
        public decimal? DiferenciaMax { get; set; }
        public DateTimeOffset? UltimoAjuste { get; set; }
    }
}
