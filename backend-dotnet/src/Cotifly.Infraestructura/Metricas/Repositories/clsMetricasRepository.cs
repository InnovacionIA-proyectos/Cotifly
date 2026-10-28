using System.Text.Json;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Metricas;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Metricas.Repositories;

/// <summary>
/// Implementación de <see cref="IClsMetricasRepository"/> con <c>Database.SqlQueryRaw</c> y SQL parametrizado. El tablero de HU-010.1
/// (<see cref="ListarCotizacionesAsync"/>) lee <c>CotizacionesSolicitud</c> y sus rutas con SQL propio y no con entidades sin clave del
/// <see cref="clsCotiflyDbContext"/>: el modelo EF es Database First de tablas y la prueba de deriva lo compara con el catálogo en ambos
/// sentidos (HU-021, ADR-019, decisión 8 del 2026-10-08): los conteos son por cotización; el costo es la suma del costo final de sus
/// rutas, el margen el promedio de sus rutas ponderado por costo y la ciudad la de la ruta 1. Una segunda consulta (<see cref="SqlRutas"/>)
/// trae las rutas de las versiones vigentes, con su línea de negocio y sus ajustes, para el margen ponderado y la apertura por línea
/// (#137). Los nombres de municipio son del 0010 y pueden no existir todavía.
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
    /// rutas (solo si todas lo tienen); margen = promedio de las rutas ponderado por su costo (sin costo, el promedio simple). HU-028.1
    /// (#183): las rutas <c>rechazada</c> por la Mesa de Costeo no cuentan (ni en el costo ni en el margen): el valor cotizado
    /// (<c>precioFinal</c>) ya las excluye.
    /// I1 (HU-023): <c>WITH (FORCESEEK)</c> obliga a buscar la cabecera por <c>ix_cotizaciones_usuarioId_createdAt</c> (filtro por
    /// <c>usuarioId</c>), así la lectura no toma candados S en filas de otros comerciales y no se cruza con su polling. La solución de
    /// fondo, que también cubre el choque con las escrituras del mismo comercial, es <c>READ_COMMITTED_SNAPSHOT</c>: decisión del DBA.
    /// </summary>
    private const string SqlCotizaciones = """
        WITH base AS (
            SELECT c.id, c.usuarioId, c.version, c.createdAt, rr.costo, c.precioFinal, c.moneda, c.trmAplicada, rr.margen AS margenAplicado,
                   c.ganada, c.marcadoAt, c.costeoListoAt, {0} AS origen, {1} AS destino, {2} AS deadline,
                   CASE WHEN c.version > 1
                             AND RIGHT(c.folio, LEN(N'-v' + CAST(c.version AS nvarchar(10)))) = N'-v' + CAST(c.version AS nvarchar(10))
                        THEN LEFT(c.folio, LEN(c.folio) - LEN(N'-v' + CAST(c.version AS nvarchar(10))))
                        ELSE c.folio END AS folioBase
            FROM dbo_cotifly.CotizacionesSolicitud c WITH (FORCESEEK)
            OUTER APPLY (
                SELECT CASE WHEN COUNT(*) = COUNT(l.costoFinal) THEN SUM(l.costoFinal) END AS costo,
                       CASE WHEN SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.costoFinal END) > 0
                            THEN CAST(SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.margenAplicado * l.costoFinal END)
                                      / SUM(CASE WHEN l.margenAplicado IS NOT NULL AND l.costoFinal > 0 THEN l.costoFinal END) AS decimal(9,4))
                            ELSE CAST(AVG(l.margenAplicado) AS decimal(9,4)) END AS margen
                FROM dbo_cotifly.CotizacionLineasSolicitud l
                WHERE l.cotizacionSolicitudId = c.id
                  AND (l.estadoCosteo IS NULL OR l.estadoCosteo <> N'rechazada')
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
        SELECT v.id AS CotizacionId, v.usuarioId AS UsuarioId, v.primeraCreacion AS CreatedAt, v.createdAt AS CreatedAtVigente, v.versiones AS Versiones,
               v.costo AS Costo, v.precioFinal AS PrecioFinal, v.moneda AS Moneda, v.trmAplicada AS TrmAplicada,
               v.margenAplicado AS MargenAplicado, r.ganada AS Ganada, v.costeoListoAt AS CosteoListoAt,
               r.id AS CotizacionResultadoId, r.precioFinal AS PrecioResultado, r.moneda AS MonedaResultado, r.trmAplicada AS TrmResultado, r.costo AS CostoResultado,
               v.origen AS Origen, v.destino AS Destino, v.deadline AS Deadline,
               a.numAjustes AS NumAjustes, a.numDiferencias AS NumDiferencias,
               a.sumaDiferencias AS SumaDiferencias, a.sumaDiferenciasAbs AS SumaDiferenciasAbsolutas
        FROM cadenas v
        OUTER APPLY (
            SELECT TOP (1) b2.id, b2.ganada, b2.precioFinal, b2.moneda, b2.trmAplicada, b2.costo
            FROM base b2
            WHERE b2.usuarioId = v.usuarioId AND b2.folioBase = v.folioBase AND b2.ganada IS NOT NULL
            ORDER BY CASE WHEN b2.marcadoAt IS NULL THEN 1 ELSE 0 END, b2.marcadoAt DESC, b2.version DESC
        ) r
        OUTER APPLY (
            SELECT COUNT(*) AS numAjustes, COUNT(m.diferencia) AS numDiferencias,
                   SUM(m.diferencia) AS sumaDiferencias, SUM(ABS(m.diferencia)) AS sumaDiferenciasAbs
            FROM dbo_cotifly.MargenAjustes m
            WHERE m.cotizacionId = v.id
              -- HU-028.1 (#183, B2): como en SqlRutas, no cuentan los ajustes de una ruta que la Mesa rechazó después, ni (si TODAS las rutas
              -- están rechazadas) los que no tienen ruta: así la cabecera suma exactamente lo que suman sus rutas.
              AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud x
                              WHERE x.id = m.lineaSolicitudId AND x.cotizacionSolicitudId = m.cotizacionId AND x.estadoCosteo = N'rechazada')
              AND (EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud y
                           WHERE y.cotizacionSolicitudId = v.id AND (y.estadoCosteo IS NULL OR y.estadoCosteo <> N'rechazada'))
                   OR NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud z WHERE z.cotizacionSolicitudId = v.id))
        ) a
        WHERE v.orden = 1 AND v.primeraCreacion >= @desde AND v.primeraCreacion < @hasta
        """;

    /// <summary>
    /// Rutas de las versiones vigentes (<c>@cotizaciones</c>, JSON de ids) con su línea de negocio y el resumen de sus
    /// <c>MargenAjustes</c> (HU-021.10, decisión 8). Un ajuste cuenta en la ruta de su <c>lineaSolicitudId</c>; si no tiene ruta, o la
    /// ruta no es de su cotización, cuenta en la primera ruta (menor <c>orden</c>), como la migración del 0009. Así la suma de las rutas
    /// da exactamente los ajustes de la cotización (<c>OUTER APPLY</c> por <c>cotizacionId</c> de <see cref="SqlCotizaciones"/>). HU-028.1
    /// (#183): las rutas <c>rechazada</c> no salen (no tienen precio; una ruta rechazada no admite ajustes de margen). HU-016.1 (#154): cada
    /// ruta trae también su <c>margenPoliticaSugerido</c>, la referencia del margen aplicado (sin script: la columna es del esquema base).
    /// </summary>
    private const string SqlRutas = """
        WITH rutas AS (
            SELECT l.id, l.cotizacionSolicitudId, l.orden, l.lineaNegocioId, mp.lineaNegocio, l.costoFinal, l.precioFinalLinea, l.margenAplicado,
                   l.margenPoliticaSugerido,
                   ROW_NUMBER() OVER (PARTITION BY l.cotizacionSolicitudId ORDER BY l.orden, l.id) AS posicion
            FROM dbo_cotifly.CotizacionLineasSolicitud l
            LEFT JOIN dbo_cotifly.MargenesPolitica mp ON mp.id = l.lineaNegocioId
            WHERE l.cotizacionSolicitudId IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@cotizaciones))
              AND (l.estadoCosteo IS NULL OR l.estadoCosteo <> N'rechazada')
        )
        SELECT r.cotizacionSolicitudId AS CotizacionId, r.orden AS Orden, r.lineaNegocioId AS LineaNegocioId, r.lineaNegocio AS LineaNegocio,
               r.costoFinal AS Costo, r.precioFinalLinea AS PrecioFinal, r.margenAplicado AS MargenAplicado,
               r.margenPoliticaSugerido AS MargenPolitica,
               a.numAjustes AS NumAjustes, a.numDiferencias AS NumDiferencias, a.sumaDiferencias AS SumaDiferencias
        FROM rutas r
        OUTER APPLY (
            SELECT COUNT(*) AS numAjustes, COUNT(m.diferencia) AS numDiferencias, SUM(m.diferencia) AS sumaDiferencias
            FROM dbo_cotifly.MargenAjustes m
            WHERE m.cotizacionId = r.cotizacionSolicitudId
              AND (m.lineaSolicitudId = r.id
                   OR (r.posicion = 1 AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud x
                                                      WHERE x.id = m.lineaSolicitudId AND x.cotizacionSolicitudId = m.cotizacionId)))
        ) a
        ORDER BY r.cotizacionSolicitudId, r.orden
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
        var usuariosJson = JsonSerializer.Serialize(usuarios.Distinct());
        var filas = await ConReintentoPorInterbloqueoAsync(() => contexto.Database.SqlQueryRaw<clsFilaMetrica>(
                sql,
                new SqlParameter("@usuarios", System.Data.SqlDbType.NVarChar, -1) { Value = usuariosJson },
                new SqlParameter("@desde", System.Data.SqlDbType.DateTimeOffset) { Value = desde },
                new SqlParameter("@hasta", System.Data.SqlDbType.DateTimeOffset) { Value = hastaExclusivo })
            .ToListAsync(cancellationToken));
        // B3 (#173): también las rutas de la versión ganada cuando no es la vigente (apertura por línea de negocio).
        var rutas = await ListarRutasAsync(
            [.. filas.Select(f => f.CotizacionId).Concat(filas.Select(f => f.CotizacionResultadoId).OfType<Guid>()).Distinct()], cancellationToken);

        return new clsDatosMetricas(
            [.. filas.Select(f => new clsCotizacionMetrica
            {
                Rutas = rutas[f.CotizacionId].ToList(),
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
                // B3 (#173): precio y costo de la versión que tiene el resultado (la ganada puede no ser la vigente).
                VersionResultado = f.Ganada is null
                    ? null
                    : new clsVersionResultadoMetrica(f.PrecioResultado, f.MonedaResultado, f.TrmResultado ?? 0m, f.CostoResultado),
                RutasResultado = f.Ganada is not null && f.CotizacionResultadoId is { } ganada && ganada != f.CotizacionId
                    ? rutas[ganada].ToList()
                    : null,
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

    /// <summary>Rutas de <paramref name="cotizaciones"/> (versiones vigentes), por cotización y en su orden.</summary>
    private async Task<ILookup<Guid, clsRutaMetrica>> ListarRutasAsync(IReadOnlyCollection<Guid> cotizaciones, CancellationToken cancellationToken)
    {
        if (cotizaciones.Count == 0)
        {
            return Array.Empty<(Guid, clsRutaMetrica)>().ToLookup(x => x.Item1, x => x.Item2);
        }

        var ids = JsonSerializer.Serialize(cotizaciones);
        var filas = await ConReintentoPorInterbloqueoAsync(() => contexto.Database.SqlQueryRaw<clsFilaRuta>(
                SqlRutas, new SqlParameter("@cotizaciones", System.Data.SqlDbType.NVarChar, -1) { Value = ids })
            .ToListAsync(cancellationToken));
        return filas.ToLookup(
            f => f.CotizacionId,
            f => new clsRutaMetrica
            {
                Orden = f.Orden,
                LineaNegocioId = f.LineaNegocioId,
                LineaNegocio = f.LineaNegocio,
                Costo = f.Costo,
                PrecioFinal = f.PrecioFinal,
                MargenAplicado = f.MargenAplicado,
                MargenPolitica = f.MargenPolitica,
                NumAjustes = f.NumAjustes,
                NumDiferencias = f.NumDiferencias,
                SumaDiferencias = f.SumaDiferencias,
            });
    }

    /// <summary>
    /// HU-021: la consulta del tablero lee la cabecera y sus rutas (dos tablas) mientras el costeo y el polling las escriben en una
    /// transacción; SQL Server puede elegirla como víctima de un interbloqueo (error 1205). Es una lectura sin efectos: se repite.
    /// </summary>
    /// <remarks>
    /// I1 (#174), causa medida con el grafo del interbloqueo: en READ COMMITTED la lectura retiene el candado S de la fila de la cabecera
    /// (<c>pk_cotizaciones</c>) mientras busca sus rutas (<c>pk_cotizacionLineasSolicitud</c>), y un guardado del polling tiene la ruta en X
    /// y espera la cabecera. La lectura recorre también filas de otros comerciales, así que choca con escrituras ajenas. Con tres
    /// intentos a 50 y 100 ms la siguiente escritura del polling volvía a cruzarse: ~3 % de lecturas fallidas con escrituras intermitentes.
    /// Seis intentos con espera exponencial y variación aleatoria (100 ms a 1,6 s, unos 3 s en total) dejan pasar la ráfaga. La causa de
    /// fondo era el recorrido de toda la cabecera en <see cref="SqlCotizaciones"/>: desde la integración final de HU-023 la consulta
    /// lleva <c>WITH (FORCESEEK)</c> (índice <c>ix_cotizaciones_usuarioId_createdAt</c>) y no toca filas ajenas (medido: 0 fallos en
    /// 887 lecturas). El choque con escrituras del mismo comercial sigue posible y lo cubre este reintento; la solución de fondo es
    /// <c>READ_COMMITTED_SNAPSHOT</c>, decisión del DBA (matriz de HU-023 §7.2 y <c>docs/10-operacion.md</c>).
    /// </remarks>
    private static async Task<T> ConReintentoPorInterbloqueoAsync<T>(Func<Task<T>> consulta)
    {
        const int Intentos = 6;
        for (var intento = 1; ; intento++)
        {
            try
            {
                return await consulta();
            }
            catch (SqlException ex) when (ex.Number == 1205 && intento < Intentos)
            {
                var pausa = 100 << (intento - 1);
                await Task.Delay(TimeSpan.FromMilliseconds(pausa + Random.Shared.Next(0, (pausa / 2) + 1)));
            }
        }
    }

    public Task<int> ContarLineasNegocioActivasAsync(CancellationToken cancellationToken = default) =>
        contexto.MargenesPolitica.CountAsync(m => m.Activa, cancellationToken);

    /// <summary>Columnas opcionales del script 0004 presentes en el esquema.</summary>
    internal sealed class clsColumnasOpcionales
    {
        public bool Ciudades { get; set; }
        public bool Deadline { get; set; }
    }

    /// <summary>Fila cruda del tablero, una por cadena (materializada por nombre de columna).</summary>
    internal sealed class clsFilaMetrica
    {
        public Guid CotizacionId { get; set; }
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
        public Guid? CotizacionResultadoId { get; set; }
        public decimal? PrecioResultado { get; set; }
        public string? MonedaResultado { get; set; }
        public decimal? TrmResultado { get; set; }
        public decimal? CostoResultado { get; set; }
        public DateTimeOffset? CosteoListoAt { get; set; }
        public string? Origen { get; set; }
        public string? Destino { get; set; }
        public DateTimeOffset? Deadline { get; set; }
        public int NumAjustes { get; set; }
        public int NumDiferencias { get; set; }
        public decimal? SumaDiferencias { get; set; }
        public decimal? SumaDiferenciasAbsolutas { get; set; }
    }

    /// <summary>Fila cruda de una ruta del tablero (HU-021.10).</summary>
    internal sealed class clsFilaRuta
    {
        public Guid CotizacionId { get; set; }
        public int Orden { get; set; }
        public int? LineaNegocioId { get; set; }
        public string? LineaNegocio { get; set; }
        public decimal? Costo { get; set; }
        public decimal? PrecioFinal { get; set; }
        public decimal? MargenAplicado { get; set; }

        /// <summary>HU-016.1 (#154): <c>margenPoliticaSugerido</c> de la ruta.</summary>
        public decimal? MargenPolitica { get; set; }

        public int NumAjustes { get; set; }
        public int NumDiferencias { get; set; }
        public decimal? SumaDiferencias { get; set; }
    }
}
