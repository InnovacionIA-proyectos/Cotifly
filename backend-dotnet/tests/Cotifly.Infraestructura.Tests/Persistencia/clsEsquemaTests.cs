using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Scripts <c>migrations_mssql</c> aplicados en SQL Server 2019 y verificación de deriva entre el modelo
/// EF (Database First) y el esquema real (ADR-008): tablas, columnas, tipos y nulabilidad.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEsquemaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly string[] _tablasEsperadas =
    [
        "CorreoDestinatarios", "Correos", "Cotizaciones", "EventosConversacion", "EventosHerramienta",
        "MargenAjustes", "MargenesPolitica", "Roles", "SapMapeoTrqType", "Sesiones",
        "TokensRefresco", "TokensRevocados", "UsuariosRoles", "UsuariosWeb",
    ];

    [Fact]
    public async Task LosScripts0001Y0002CreanLasCatorceTablas()
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0));

        tablas.Should().BeEquivalentTo(_tablasEsperadas);
    }

    [Fact]
    public async Task ElScript0002EsIdempotente()
    {
        var antes = await FotoEsquemaAsync();
        var filasAntes = await ContarFilasAsync();

        var reaplicar = () => clsBaseDatosPruebas.EjecutarScriptAsync(
            BaseDatos.CadenaCotifly, clsBaseDatosPruebas.RutaScript("0002_identidad_estado_agente.sql"));

        await reaplicar.Should().NotThrowAsync();
        (await FotoEsquemaAsync()).Should().Equal(antes, "re-aplicar el 0002 no debe cambiar columnas, claves, índices, CHECK ni defaults");
        (await ContarFilasAsync()).Should().Be(filasAntes, "re-aplicar el 0002 no debe tocar datos");
    }

    [Fact]
    public async Task LasClavesPrimariasTienenLosNombresYColumnasDelScript()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, kc.name,
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly' AND kc.type = 'PK'
            GROUP BY t.name, kc.name
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}({r.GetString(2)})");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().Select(e =>
        {
            var (tabla, objeto) = Tabla(e);
            var pk = e.FindPrimaryKey()!;
            return $"{tabla}:{pk.GetName()}({string.Join(',', pk.Properties.Select(p => p.GetColumnName(objeto)))})";
        });

        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LasClavesForaneasCoincidenEnNombreColumnasYTablaReferenciada()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, fk.name, OBJECT_NAME(fk.referenced_object_id),
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
            GROUP BY t.name, fk.name, fk.referenced_object_id
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}({r.GetString(3)})->{r.GetString(2)}");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetForeignKeys().Select(fk =>
                $"{tabla}:{fk.GetConstraintName()}({string.Join(',', fk.Properties.Select(p => p.GetColumnName(objeto)))})->{fk.PrincipalEntityType.GetTableName()}");
        });

        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LosIndicesYRestriccionesUnicasCoincidenEnColumnasOrdenIncludeUnicidadYFiltro()
    {
        var bd = await LeerIndicesAsync();

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetIndexes().Select(i =>
            {
                var claves = i.Properties.Select((p, n) =>
                    p.GetColumnName(objeto) + (i.IsDescending is { Count: > 0 } d && d[n] ? " DESC" : string.Empty));
                var incluidas = (i.GetIncludeProperties() ?? [])
                    .Select(nombre => e.FindProperty(nombre)!.GetColumnName(objeto)!)
                    .Order(StringComparer.Ordinal);
                return Indice(tabla, i.GetDatabaseName()!, i.IsUnique, string.Join(',', claves), string.Join(',', incluidas), i.GetFilter());
            });
        });

        ef.Should().BeEquivalentTo(bd);
        bd.Where(x => x.Contains(":ix_", StringComparison.Ordinal)).Should().HaveCount(10, "el 0002 crea 10 índices ix_");
        bd.Should().Contain(x => x.StartsWith("UsuariosWeb:uq_usuariosWeb_nombreUsuario", StringComparison.Ordinal) && x.EndsWith("filtro=nombreusuarioisnotnull", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("UsuariosRoles:uq_usuariosRoles_usuarioId_activo unico=True", StringComparison.Ordinal) && x.EndsWith("filtro=activo=1", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("Roles:uq_roles_idRolExterno unico=True", StringComparison.Ordinal) && x.EndsWith("filtro=idrolexternoisnotnull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LosCheckDelEsquemaEstanDeclaradosEnElModelo()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, cc.name
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e => e.GetCheckConstraints().Select(c => $"{e.GetTableName()}:{c.Name}"));

        // SQL Server reescribe la expresión (IN -> OR, paréntesis, corchetes): se comparan los nombres.
        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LaCollationDeCadaColumnaDeTextoCoincideConElModelo()
    {
        var bd = await LeerAsync(
            "SELECT TABLE_NAME, COLUMN_NAME, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo_cotifly' AND COLLATION_NAME IS NOT NULL",
            r => (Clave: (r.GetString(0), r.GetString(1)), Collation: r.GetString(2)));
        var porColumna = bd.ToDictionary(x => x.Clave, x => x.Collation);

        var modelo = await ModeloAsync();
        var diferencias = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetProperties()
                .Where(p => porColumna.ContainsKey((tabla, p.GetColumnName(objeto)!)) || p.GetCollation() is not null)
                .Select(p => (Columna: $"{tabla}.{p.GetColumnName(objeto)}", Ef: p.GetCollation(),
                    Bd: porColumna.GetValueOrDefault((tabla, p.GetColumnName(objeto)!))))
                .Where(x => x.Ef != x.Bd)
                .Select(x => $"{x.Columna}: EF '{x.Ef}' vs base '{x.Bd}'");
        });

        diferencias.Should().BeEmpty();
        porColumna[("TokensRefresco", "tokenHash")].Should().Be(clsCotiflyDbContext.CollationBinaria);
        porColumna[("Cotizaciones", "folio")].Should().Be(clsCotiflyDbContext.Collation);
    }

    [Fact]
    public async Task ElSeedDelScript0001TraeLasOchoLineasYSusMapeos()
    {
        var lineas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio IN ('Nacional','Distribucion','Importacion','Exportacion','Internacional','Almacenamiento','Almacenamiento3PL','ITR')");
        var mapeos = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType WHERE salesOrg = ''");

        lineas.Should().Be(8);
        mapeos.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public async Task CadaEntidadDelModeloEstaMapeadaATablaYColumnasExistentesConTiposCompatibles()
    {
        var columnasBd = await LeerColumnasAsync();
        var diferencias = new List<string>();

        var modelo = await ConsultarAsync(s => Task.FromResult(Contexto(s).Model));
        foreach (var entidad in modelo.GetEntityTypes())
        {
            var tabla = entidad.GetTableName()!;
            entidad.GetSchema().Should().Be(clsCotiflyDbContext.Esquema, $"{entidad.ClrType.Name} debe vivir en dbo_cotifly");
            var objeto = StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema);

            foreach (var propiedad in entidad.GetProperties())
            {
                var columna = propiedad.GetColumnName(objeto)!;
                if (!columnasBd.TryGetValue((tabla, columna), out var real))
                {
                    diferencias.Add($"{tabla}.{columna}: mapeada en EF pero no existe en la base");
                    continue;
                }

                var tipoEf = Normalizar(propiedad.GetColumnType());
                if (tipoEf != real.Tipo)
                {
                    diferencias.Add($"{tabla}.{columna}: EF '{tipoEf}' vs base '{real.Tipo}'");
                }

                if (propiedad.IsColumnNullable(objeto) != real.Nula)
                {
                    diferencias.Add($"{tabla}.{columna}: EF nula={propiedad.IsColumnNullable(objeto)} vs base nula={real.Nula}");
                }
            }
        }

        diferencias.Should().BeEmpty("el modelo EF debe coincidir con el esquema de los scripts (Database First)");
    }

    [Fact]
    public async Task CadaTablaYColumnaDelEsquemaEstaMapeadaEnElModelo()
    {
        var columnasBd = await LeerColumnasAsync();
        var modelo = await ConsultarAsync(s => Task.FromResult(Contexto(s).Model));

        var mapeadas = modelo.GetEntityTypes()
            .SelectMany(e =>
            {
                var tabla = e.GetTableName()!;
                var objeto = StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema);
                return e.GetProperties().Select(p => (tabla, p.GetColumnName(objeto)!));
            })
            .ToHashSet();

        columnasBd.Keys.Where(k => !mapeadas.Contains(k)).Select(k => $"{k.Tabla}.{k.Columna}")
            .Should().BeEmpty("toda columna de dbo_cotifly debe tener su propiedad en el modelo");
        modelo.GetEntityTypes().Select(e => e.GetTableName()).Should().BeEquivalentTo(_tablasEsperadas);
    }

    [Fact]
    public async Task CadaDbSetSePuedeConsultarContraElEsquemaReal()
    {
        await ConsultarAsync(async s =>
        {
            var c = Contexto(s);
            await c.Cotizaciones.Take(1).ToListAsync(Ct);
            await c.MargenAjustes.Take(1).ToListAsync(Ct);
            await c.Correos.Include(x => x.Destinatarios).Take(1).ToListAsync(Ct);
            await c.CorreoDestinatarios.Take(1).ToListAsync(Ct);
            await c.Sesiones.Take(1).ToListAsync(Ct);
            await c.EventosConversacion.Take(1).ToListAsync(Ct);
            await c.EventosHerramienta.Take(1).ToListAsync(Ct);
            await c.UsuariosWeb.Take(1).ToListAsync(Ct);
            await c.MargenesPolitica.Take(1).ToListAsync(Ct);
            await c.SapMapeoTrqType.Take(1).ToListAsync(Ct);
            await c.Roles.Take(1).ToListAsync(Ct);
            await c.UsuariosRoles.Take(1).ToListAsync(Ct);
            await c.TokensRefresco.Take(1).ToListAsync(Ct);
            await c.TokensRevocados.Take(1).ToListAsync(Ct);
            return true;
        });
    }

    [Fact]
    public async Task ElCheckDeEstadoAgenteRechazaJsonInvalido()
    {
        var insertar = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Sesiones (spaceId, threadId, estado, estadoAgente) VALUES ('s-{Guid.NewGuid():N}', 't', 'activa', 'esto no es json')");

        (await insertar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_sesiones_estadoAgente_json");
    }

    [Fact]
    public async Task ElCheckDeRolesSoloAceptaLosRolesDeCotifly()
    {
        var insertar = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.Roles (codigo) VALUES ('admin')");

        (await insertar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_roles_codigo");
    }

    [Fact]
    public async Task ReaplicarEl0002NoPisaElRolExternoQueFijoElDba()
    {
        await clsBaseDatosPruebas.EjecutarScriptAsync(BaseDatos.CadenaCotifly, clsBaseDatosPruebas.RutaScript("0002_identidad_estado_agente.sql"));

        var comercial = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'");
        var filas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly, "SELECT COUNT(*) FROM dbo_cotifly.Roles");

        comercial.Should().Be(clsBaseDatosPruebas.RolExternoComercial);
        filas.Should().Be(2);
    }

    /// <summary>Modelo de diseño: el de ejecución no conserva CHECK, INCLUDE ni otros metadatos solo relacionales.</summary>
    private Task<IModel> ModeloAsync() => ConsultarAsync(s => Task.FromResult(Contexto(s).GetService<IDesignTimeModel>().Model));

    private static (string Tabla, StoreObjectIdentifier Objeto) Tabla(IEntityType entidad)
    {
        var tabla = entidad.GetTableName()!;
        return (tabla, StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema));
    }

    private static string Indice(string tabla, string nombre, bool unico, string claves, string incluidas, string? filtro) =>
        $"{tabla}:{nombre} unico={unico} claves=({claves}) include=({incluidas}) filtro={NormalizarFiltro(filtro)}";

    /// <summary>SQL Server guarda <c>([nombreUsuario] IS NOT NULL)</c>; EF, <c>[nombreUsuario] IS NOT NULL</c>.</summary>
    private static string NormalizarFiltro(string? filtro) =>
        new string((filtro ?? string.Empty).Where(c => c is not ('(' or ')' or '[' or ']' or ' ')).ToArray()).ToLowerInvariant();

    /// <summary>Índices y restricciones UNIQUE (no las PK) de dbo_cotifly.</summary>
    private Task<List<string>> LeerIndicesAsync() =>
        LeerAsync(
            """
            SELECT t.name, i.name, i.is_unique, i.filter_definition,
                   STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END END, ',')
                       WITHIN GROUP (ORDER BY ic.key_ordinal, c.name),
                   STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal, c.name)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly' AND i.is_primary_key = 0 AND i.name IS NOT NULL
            GROUP BY t.name, i.name, i.is_unique, i.filter_definition
            """,
            r => Indice(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(4),
                r.IsDBNull(5) ? string.Empty : r.GetString(5), r.IsDBNull(3) ? null : r.GetString(3)));

    /// <summary>
    /// Descripción ordenada de todo el esquema (columnas con tipo/nulabilidad/collation/default,
    /// claves, FK, índices y CHECK), para comparar antes y después de re-aplicar un script.
    /// </summary>
    private async Task<List<string>> FotoEsquemaAsync()
    {
        var columnas = await LeerAsync(
            """
            SELECT CONCAT(TABLE_NAME COLLATE DATABASE_DEFAULT, '.', COLUMN_NAME COLLATE DATABASE_DEFAULT, ' ', DATA_TYPE COLLATE DATABASE_DEFAULT,
                          '(', CHARACTER_MAXIMUM_LENGTH, ',', NUMERIC_PRECISION, ',', NUMERIC_SCALE, ') ', IS_NULLABLE COLLATE DATABASE_DEFAULT, ' ',
                          COLLATION_NAME COLLATE DATABASE_DEFAULT, ' ', COLUMN_DEFAULT COLLATE DATABASE_DEFAULT)
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo_cotifly'
            """,
            r => r.GetString(0));
        var restricciones = await LeerAsync(
            """
            SELECT CONCAT(OBJECT_NAME(o.parent_object_id) COLLATE DATABASE_DEFAULT, ':', o.name COLLATE DATABASE_DEFAULT, ' ',
                          o.type_desc COLLATE DATABASE_DEFAULT, ' ', OBJECT_DEFINITION(o.object_id) COLLATE DATABASE_DEFAULT)
            FROM sys.objects o
            WHERE SCHEMA_NAME(o.schema_id) = 'dbo_cotifly' AND o.type IN ('PK', 'UQ', 'F', 'C', 'D')
            """,
            r => r.GetString(0));
        var indices = await LeerIndicesAsync();

        return [.. columnas.Concat(restricciones).Concat(indices).Order(StringComparer.Ordinal)];
    }

    private Task<int> ContarFilasAsync() =>
        clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            """
            SELECT CAST(SUM(p.rows) AS INT) FROM sys.partitions p
            JOIN sys.tables t ON t.object_id = p.object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly' AND p.index_id IN (0, 1)
            """);

    private async Task<Dictionary<(string Tabla, string Columna), (string Tipo, bool Nula)>> LeerColumnasAsync()
    {
        var filas = await LeerAsync(
            """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA = 'dbo_cotifly' AND t.TABLE_TYPE = 'BASE TABLE'
            """,
            r =>
            {
                var tipo = r.GetString(2);
                int? largo = r.IsDBNull(3) ? null : r.GetInt32(3);
                var tipoCompleto = tipo switch
                {
                    "nvarchar" or "varchar" or "nchar" or "char" => $"{tipo}({(largo == -1 ? "max" : largo)})",
                    "decimal" or "numeric" => $"{tipo}({r.GetByte(4)},{r.GetInt32(5)})",
                    _ => tipo,
                };
                return (Tabla: r.GetString(0), Columna: r.GetString(1), Tipo: tipoCompleto, Nula: r.GetString(6) == "YES");
            });

        return filas.ToDictionary(f => (f.Tabla, f.Columna), f => (f.Tipo, f.Nula));
    }

    private async Task<List<T>> LeerAsync<T>(string sql, Func<SqlDataReader, T> mapear)
    {
        await using var conexion = new SqlConnection(BaseDatos.CadenaCotifly);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        var filas = new List<T>();
        while (await lector.ReadAsync(Ct))
        {
            filas.Add(mapear(lector));
        }

        return filas;
    }

    private static string Normalizar(string? tipo) =>
        (tipo ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
