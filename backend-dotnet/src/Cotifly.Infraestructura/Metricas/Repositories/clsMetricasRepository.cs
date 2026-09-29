using System.Text.Json;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Metricas;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Metricas.Repositories;

/// <summary>
/// Implementación de <see cref="IClsMetricasRepository"/> con <c>Database.SqlQuery</c> sobre las vistas del 0001 (corregida
/// la de actividad por el 0003) y un JOIN con <c>UsuariosWeb</c>. Las vistas no se mapean como entidades sin clave del
/// <see cref="clsCotiflyDbContext"/> a propósito: el modelo EF es Database First de tablas y la prueba de deriva lo compara
/// con el catálogo en ambos sentidos. Las consultas son SQL parametrizado (interpolación de EF Core → <c>@p0</c>, <c>@p1</c>).
/// El tablero de HU-010.1 (<see cref="ListarCotizacionesAsync"/>) lee <c>Cotizaciones</c> con SQL propio por la misma razón:
/// las columnas <c>origen</c>/<c>destino</c> son del script 0004 y pueden no existir todavía.
/// </summary>
public sealed class clsMetricasRepository(clsCotiflyDbContext contexto) : IClsMetricasRepository
{
    /// <summary>¿El esquema tiene las columnas de ciudad del 0004? (<c>COL_LENGTH</c> es NULL si la columna no existe.)</summary>
    private const string SqlCiudadesDisponibles = """
        SELECT CAST(CASE WHEN COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'origen') IS NOT NULL
                          AND COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'destino') IS NOT NULL THEN 1 ELSE 0 END AS bit) AS [Value]
        """;

    /// <summary>
    /// Filas del tablero (ADR-016 §2). Folio base = folio sin el sufijo <c>-v{version}</c>; la vigente es la de mayor
    /// <c>version</c> de cada <c>(usuarioId, folio base)</c>, decidida sobre toda la historia y filtrada después por periodo.
    /// <c>{0}</c>/<c>{1}</c>: columnas de origen/destino o <c>NULL</c> si el 0004 no está aplicado (fragmentos fijos del código,
    /// nunca datos del usuario).
    /// </summary>
    private const string SqlCotizaciones = """
        WITH base AS (
            SELECT c.id, c.usuarioId, c.version, c.createdAt, c.costo, c.precioFinal, c.moneda, c.trmAplicada, c.margenAplicado,
                   c.ganada, c.costeoListoAt, {0} AS origen, {1} AS destino,
                   CASE WHEN c.version > 1
                             AND RIGHT(c.folio, LEN(N'-v' + CAST(c.version AS nvarchar(10)))) = N'-v' + CAST(c.version AS nvarchar(10))
                        THEN LEFT(c.folio, LEN(c.folio) - LEN(N'-v' + CAST(c.version AS nvarchar(10))))
                        ELSE c.folio END AS folioBase
            FROM dbo_cotifly.Cotizaciones c
            WHERE c.usuarioId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@usuarios))
        ),
        ordenadas AS (
            SELECT b.*, ROW_NUMBER() OVER (PARTITION BY b.usuarioId, b.folioBase ORDER BY b.version DESC, b.createdAt DESC, b.id) AS orden
            FROM base b
        )
        SELECT o.usuarioId AS UsuarioId, o.createdAt AS CreatedAt,
               CAST(CASE WHEN o.orden = 1 THEN 1 ELSE 0 END AS bit) AS EsVigente,
               o.costo AS Costo, o.precioFinal AS PrecioFinal, o.moneda AS Moneda, o.trmAplicada AS TrmAplicada,
               o.margenAplicado AS MargenAplicado, o.ganada AS Ganada, o.costeoListoAt AS CosteoListoAt,
               o.origen AS Origen, o.destino AS Destino,
               COALESCE(a.numAjustes, 0) AS NumAjustes, COALESCE(a.numDiferencias, 0) AS NumDiferencias,
               a.sumaDiferencias AS SumaDiferencias
        FROM ordenadas o
        LEFT JOIN (
            SELECT cotizacionId, COUNT(*) AS numAjustes, COUNT(diferencia) AS numDiferencias, SUM(diferencia) AS sumaDiferencias
            FROM dbo_cotifly.MargenAjustes
            WHERE cotizacionId IS NOT NULL
            GROUP BY cotizacionId
        ) a ON a.cotizacionId = o.id
        WHERE o.createdAt >= @desde AND o.createdAt < @hasta
        """;

    public async Task<clsDatosMetricas> ListarCotizacionesAsync(
        IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hastaExclusivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        var ciudades = (await contexto.Database.SqlQueryRaw<bool>(SqlCiudadesDisponibles).ToListAsync(cancellationToken)).Single();
        if (usuarios.Count == 0)
        {
            return new clsDatosMetricas([], ciudades);
        }

        var sql = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            SqlCotizaciones,
            ciudades ? "c.origen" : "CAST(NULL AS nvarchar(150))",
            ciudades ? "c.destino" : "CAST(NULL AS nvarchar(150))");
        var filas = await contexto.Database.SqlQueryRaw<clsFilaMetrica>(
                sql,
                new SqlParameter("@usuarios", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(usuarios.Distinct()) },
                new SqlParameter("@desde", System.Data.SqlDbType.DateTimeOffset) { Value = desde },
                new SqlParameter("@hasta", System.Data.SqlDbType.DateTimeOffset) { Value = hastaExclusivo })
            .ToListAsync(cancellationToken);

        return new clsDatosMetricas(
            [.. filas.Select(f => new clsCotizacionMetrica
            {
                UsuarioId = f.UsuarioId,
                CreatedAt = f.CreatedAt,
                EsVigente = f.EsVigente,
                Costo = f.Costo,
                PrecioFinal = f.PrecioFinal,
                Moneda = f.Moneda,
                TrmAplicada = f.TrmAplicada,
                MargenAplicado = f.MargenAplicado,
                Ganada = f.Ganada,
                CosteoListoAt = f.CosteoListoAt,
                Origen = f.Origen,
                Destino = f.Destino,
                NumAjustes = f.NumAjustes,
                NumDiferencias = f.NumDiferencias,
                SumaDiferencias = f.SumaDiferencias,
            })],
            ciudades);
    }

    public Task<int> ContarLineasNegocioActivasAsync(CancellationToken cancellationToken = default) =>
        contexto.MargenesPolitica.CountAsync(m => m.Activa, cancellationToken);

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

    /// <summary>Fila cruda del tablero (materializada por nombre de columna).</summary>
    internal sealed class clsFilaMetrica
    {
        public Guid UsuarioId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool EsVigente { get; set; }
        public decimal? Costo { get; set; }
        public decimal? PrecioFinal { get; set; }
        public string? Moneda { get; set; }
        public decimal TrmAplicada { get; set; }
        public decimal? MargenAplicado { get; set; }
        public bool? Ganada { get; set; }
        public DateTimeOffset? CosteoListoAt { get; set; }
        public string? Origen { get; set; }
        public string? Destino { get; set; }
        public int NumAjustes { get; set; }
        public int NumDiferencias { get; set; }
        public decimal? SumaDiferencias { get; set; }
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
