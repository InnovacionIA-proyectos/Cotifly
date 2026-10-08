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
/// El tablero de HU-010.1 (<see cref="ListarCotizacionesAsync"/>) lee <c>CotizacionesSolicitud</c> y sus rutas con SQL propio por la
/// misma razón (HU-021, ADR-019, decisión 8 del 2026-10-08): los conteos son por cotización; el costo es la suma del costo final de sus
/// rutas, el margen el promedio de sus rutas ponderado por costo y la ciudad la de la ruta 1 (las métricas por ruta llegan con #137). Los
/// nombres de municipio son del 0010 y pueden no existir todavía.
/// </summary>
public sealed class clsMetricasRepository(clsCotiflyDbContext contexto) : IClsMetricasRepository
{
    /// <summary>
    /// ¿El esquema tiene las columnas opcionales del script 0004? (<c>COL_LENGTH</c> es NULL si la columna no existe.)
    /// </summary>
    private const string SqlColumnasOpcionales = """
        SELECT CAST(CASE WHEN COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenNombre') IS NOT NULL
                          AND COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'destinoNombre') IS NOT NULL THEN 1 ELSE 0 END AS bit) AS Ciudades,
               CAST(CASE WHEN COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'deadline') IS NOT NULL THEN 1 ELSE 0 END AS bit) AS Deadline
        """;

    /// <summary>
    /// Una fila por cadena de versiones (ADR-016 §2). Folio base = folio sin el sufijo <c>-v{version}</c>; cadena =
    /// <c>(usuarioId, folio base)</c>. Valores de la versión vigente (mayor <c>version</c>); periodo por la primera creación de la
    /// cadena (<c>MIN(createdAt)</c>); resultado = el último no nulo de la cadena (mayor <c>marcadoAt</c>, sin fecha al final, y
    /// luego mayor <c>version</c>); ajustes de la vigente con <c>OUTER APPLY</c> por <c>cotizacionId</c> (índice del 0002).
    /// <c>{0}</c>/<c>{1}</c>/<c>{2}</c>: origen y destino de la ruta 1 y <c>deadline</c> (o <c>NULL</c> si la columna no existe);
    /// <c>{3}</c>: la lectura de la ruta 1 (fragmentos fijos del código, nunca datos del usuario). Costo = suma de <c>costoFinal</c> de las
    /// rutas (solo si todas lo tienen); margen = promedio de las rutas ponderado por su costo (sin costo, el promedio simple).
    /// </summary>
    private const string SqlCotizaciones = """
        WITH base AS (
            SELECT c.id, c.usuarioId, c.version, c.createdAt, rr.costo, c.precioFinal, c.moneda, c.trmAplicada, rr.margen AS margenAplicado,
                   c.ganada, c.marcadoAt, c.costeoListoAt, {0} AS origen, {1} AS destino, {2} AS deadline,
                   CASE WHEN c.version > 1
                             AND RIGHT(c.folio, LEN(N'-v' + CAST(c.version AS nvarchar(10)))) = N'-v' + CAST(c.version AS nvarchar(10))
                        THEN LEFT(c.folio, LEN(c.folio) - LEN(N'-v' + CAST(c.version AS nvarchar(10))))
                        ELSE c.folio END AS folioBase
            FROM dbo_cotifly.CotizacionesSolicitud c
            OUTER APPLY (
                SELECT CASE WHEN COUNT(*) = COUNT(l.costoFinal) THEN SUM(l.costoFinal) END AS costo,
                       CASE WHEN SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.costoFinal END) > 0
                            THEN CAST(SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.margenAplicado * l.costoFinal END)
                                      / SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.costoFinal END) AS decimal(9,4))
                            ELSE CAST(AVG(l.margenAplicado) AS decimal(9,4)) END AS margen
                FROM dbo_cotifly.CotizacionLineasSolicitud l
                WHERE l.cotizacionSolicitudId = c.id
            ) rr
            {3}
            WHERE c.usuarioId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@usuarios))
        ),
        cadenas AS (
            SELECT b.*,
                   ROW_NUMBER() OVER (PARTITION BY b.usuarioId, b.folioBase ORDER BY b.version DESC, b.createdAt DESC, b.id) AS orden,
                   MIN(b.createdAt) OVER (PARTITION BY b.usuarioId, b.folioBase) AS primeraCreacion,
                   COUNT(*) OVER (PARTITION BY b.usuarioId, b.folioBase) AS versiones
            FROM base b
        )
        SELECT v.usuarioId AS UsuarioId, v.primeraCreacion AS CreatedAt, v.createdAt AS CreatedAtVigente, v.versiones AS Versiones,
               v.costo AS Costo, v.precioFinal AS PrecioFinal, v.moneda AS Moneda, v.trmAplicada AS TrmAplicada,
               v.margenAplicado AS MargenAplicado, r.ganada AS Ganada, v.costeoListoAt AS CosteoListoAt,
               v.origen AS Origen, v.destino AS Destino, v.deadline AS Deadline,
               a.numAjustes AS NumAjustes, a.numDiferencias AS NumDiferencias,
               a.sumaDiferencias AS SumaDiferencias, a.sumaDiferenciasAbs AS SumaDiferenciasAbsolutas
        FROM cadenas v
        OUTER APPLY (
            SELECT TOP (1) b2.ganada
            FROM base b2
            WHERE b2.usuarioId = v.usuarioId AND b2.folioBase = v.folioBase AND b2.ganada IS NOT NULL
            ORDER BY CASE WHEN b2.marcadoAt IS NULL THEN 1 ELSE 0 END, b2.marcadoAt DESC, b2.version DESC
        ) r
        OUTER APPLY (
            SELECT COUNT(*) AS numAjustes, COUNT(m.diferencia) AS numDiferencias,
                   SUM(m.diferencia) AS sumaDiferencias, SUM(ABS(m.diferencia)) AS sumaDiferenciasAbs
            FROM dbo_cotifly.MargenAjustes m
            WHERE m.cotizacionId = v.id
        ) a
        WHERE v.orden = 1 AND v.primeraCreacion >= @desde AND v.primeraCreacion < @hasta
        """;

    public async Task<clsDatosMetricas> ListarCotizacionesAsync(
        IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hastaExclusivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuarios);
        var columnas = (await contexto.Database.SqlQueryRaw<clsColumnasOpcionales>(SqlColumnasOpcionales).ToListAsync(cancellationToken)).Single();
        if (usuarios.Count == 0)
        {
            return new clsDatosMetricas([], columnas.Ciudades, columnas.Deadline);
        }

        var sql = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            SqlCotizaciones,
            columnas.Ciudades ? "r1.origenNombre" : "CAST(NULL AS nvarchar(150))",
            columnas.Ciudades ? "r1.destinoNombre" : "CAST(NULL AS nvarchar(150))",
            columnas.Deadline ? "c.deadline" : "CAST(NULL AS datetimeoffset)",
            columnas.Ciudades
                ? "OUTER APPLY (SELECT TOP (1) l1.origenNombre, l1.destinoNombre FROM dbo_cotifly.CotizacionLineasSolicitud l1 WHERE l1.cotizacionSolicitudId = c.id ORDER BY l1.orden) r1"
                : string.Empty);
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
                CreatedAtVigente = f.CreatedAtVigente,
                Versiones = f.Versiones,
                Costo = f.Costo,
                PrecioFinal = f.PrecioFinal,
                Moneda = f.Moneda,
                TrmAplicada = f.TrmAplicada,
                MargenAplicado = f.MargenAplicado,
                Ganada = f.Ganada,
                CosteoListoAt = f.CosteoListoAt,
                Origen = f.Origen,
                Destino = f.Destino,
                Deadline = f.Deadline,
                NumAjustes = f.NumAjustes,
                NumDiferencias = f.NumDiferencias,
                SumaDiferencias = f.SumaDiferencias,
                SumaDiferenciasAbsolutas = f.SumaDiferenciasAbsolutas,
            })],
            columnas.Ciudades,
            columnas.Deadline);
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
                       v.ultimoAjuste AS UltimoAjuste, v.numAjustesBajoSugerido AS NumAjustesBajoSugerido
                FROM dbo_cotifly.vAjustesMargenPorComercial v
                LEFT JOIN dbo_cotifly.UsuariosWeb u ON u.id = v.usuarioId
                WHERE v.usuarioId = {usuarioId}
                """)
            : contexto.Database.SqlQuery<clsFilaAjustes>($"""
                SELECT v.usuarioId AS UsuarioId, u.nombreCompleto AS NombreCompleto, v.lineaNegocio AS LineaNegocio, v.numAjustes AS NumAjustes,
                       v.diferenciaPromedio AS DiferenciaPromedio, v.diferenciaMin AS DiferenciaMin, v.diferenciaMax AS DiferenciaMax,
                       v.ultimoAjuste AS UltimoAjuste, v.numAjustesBajoSugerido AS NumAjustesBajoSugerido
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
                f.UsuarioId, f.NombreCompleto, f.LineaNegocio, f.NumAjustes, f.DiferenciaPromedio, f.DiferenciaMin, f.DiferenciaMax, f.UltimoAjuste,
                f.NumAjustesBajoSugerido))
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

    /// <summary>Columnas opcionales del script 0004 presentes en el esquema.</summary>
    internal sealed class clsColumnasOpcionales
    {
        public bool Ciudades { get; set; }
        public bool Deadline { get; set; }
    }

    /// <summary>Fila cruda del tablero, una por cadena (materializada por nombre de columna).</summary>
    internal sealed class clsFilaMetrica
    {
        public Guid UsuarioId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset CreatedAtVigente { get; set; }
        public int Versiones { get; set; }
        public decimal? Costo { get; set; }
        public decimal? PrecioFinal { get; set; }
        public string? Moneda { get; set; }
        public decimal TrmAplicada { get; set; }
        public decimal? MargenAplicado { get; set; }
        public bool? Ganada { get; set; }
        public DateTimeOffset? CosteoListoAt { get; set; }
        public string? Origen { get; set; }
        public string? Destino { get; set; }
        public DateTimeOffset? Deadline { get; set; }
        public int NumAjustes { get; set; }
        public int NumDiferencias { get; set; }
        public decimal? SumaDiferencias { get; set; }
        public decimal? SumaDiferenciasAbsolutas { get; set; }
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
        public int NumAjustesBajoSugerido { get; set; }
    }
}
