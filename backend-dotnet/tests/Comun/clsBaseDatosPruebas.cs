using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SQL Server 2019 en Testcontainers con el esquema real (ADR-008, ADR-010):
/// crea <c>dbTarifario_Nexo_Pruebas</c>, aplica <c>migrations_mssql/0001</c>, <c>0002</c> y <c>0003</c> partidos por
/// <c>GO</c>, y crea una <c>dbsolucionesti</c> falsa con las tres tablas del almacén corporativo y sus tipos reales:
/// <c>dbo.tblSAPUsuariosApp</c> (ID NUMERIC, incluida la columna CLAVE, que el backend nunca debe leer),
/// <c>dbo.tblSAPUsuarioRoles</c> (IDUSUARIO e IDROLES INT) y <c>dbo.tblSAPRoles</c>. Mapea los roles internos de
/// <c>dbo_cotifly.Roles</c> a los roles externos de prueba (<see cref="RolExternoComercial"/>,
/// <see cref="RolExternoGerente"/>). Un contenedor por ensamblado de pruebas (o por clase, según el fixture).
/// </summary>
/// <remarks>
/// Dentro del contenedor del SDK se necesita el socket de Docker montado y
/// <c>TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal</c> (ver docs/08-testing.md, §8.5).
/// </remarks>
public sealed partial class clsBaseDatosPruebas : IAsyncLifetime
{
    public const string Imagen = "mcr.microsoft.com/mssql/server:2019-latest";
    public const string NombreBase = "dbTarifario_Nexo_Pruebas";
    public const string NombreBaseIdentidad = "dbsolucionesti";
    public static readonly string[] Scripts = ["0001_script_bd_cotifly_sqlserver.sql", "0002_identidad_estado_agente.sql", "0003_metricas_reenviadas.sql"];

    /// <summary>IDROLES externo de prueba mapeado a <c>comercial</c>.</summary>
    public const int RolExternoComercial = 34;

    /// <summary>IDROLES externo de prueba mapeado a <c>gerente_comercial</c>.</summary>
    public const int RolExternoGerente = 35;

    /// <summary>IDROLES externo de prueba sin mapeo (no da acceso).</summary>
    public const int RolExternoSinMapeo = 40;

    private static long _siguienteIdExterno = 10_000;

    private readonly MsSqlContainer _contenedor = new MsSqlBuilder(Imagen).Build();
    private readonly SemaphoreSlim _candado = new(1, 1);
    private string? _cadenaSolo0001;

    /// <summary>Cadena a <c>dbTarifario_Nexo_Pruebas</c> con el esquema aplicado.</summary>
    public string CadenaCotifly { get; private set; } = string.Empty;

    /// <summary>Cadena a la <c>dbsolucionesti</c> de pruebas.</summary>
    public string CadenaIdentidad { get; private set; } = string.Empty;

    /// <summary>Cadena a <c>master</c> (para crear bases auxiliares).</summary>
    public string CadenaMaster { get; private set; } = string.Empty;

    public static string RutaScript(string nombre) => Path.Combine(AppContext.BaseDirectory, "migrations_mssql", nombre);

    public async ValueTask InitializeAsync()
    {
        await _contenedor.StartAsync();

        CadenaMaster = ConBase(_contenedor.GetConnectionString(), "master");
        CadenaCotifly = ConBase(CadenaMaster, NombreBase);
        CadenaIdentidad = ConBase(CadenaMaster, NombreBaseIdentidad);

        await EjecutarAsync(CadenaMaster, $"CREATE DATABASE [{NombreBase}];");
        foreach (var script in Scripts)
        {
            await EjecutarScriptAsync(CadenaCotifly, RutaScript(script));
        }

        await EjecutarAsync(CadenaMaster, $"CREATE DATABASE [{NombreBaseIdentidad}];");
        await EjecutarAsync(CadenaIdentidad, $"""
            CREATE TABLE dbo.tblSAPUsuariosApp (
                ID                  NUMERIC(18,0) NOT NULL PRIMARY KEY,
                NOMBREUSUARIO       VARCHAR(50)   NULL,
                DESCRIPCIONUSUARIO  VARCHAR(250)  NULL,
                EMAIL               VARCHAR(100)  NULL,
                CLAVE               VARCHAR(200)  NULL
            );
            CREATE TABLE dbo.tblSAPRoles (
                IDROLES             INT           NOT NULL PRIMARY KEY,
                DESCRIPCION         VARCHAR(50)   NULL
            );
            CREATE TABLE dbo.tblSAPUsuarioRoles (
                ID                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                IDUSUARIO           INT           NOT NULL,
                IDROLES             INT           NOT NULL
            );
            INSERT INTO dbo.tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL, CLAVE) VALUES
                (101, 'APEREZ', 'Ana Pérez', 'aperez@boterosoto.com.co', 'no-debe-leerse'),
                (102, 'LGOMEZ', 'Luis Gómez', 'lgomez@boterosoto.com.co', 'no-debe-leerse'),
                (3000000000, 'FUERARANGO', 'ID fuera de INT', NULL, 'no-debe-leerse');
            INSERT INTO dbo.tblSAPRoles (IDROLES, DESCRIPCION) VALUES
                ({RolExternoComercial}, 'COMERCIAL'), ({RolExternoGerente}, 'GERENTE COMERCIAL'), ({RolExternoSinMapeo}, 'CONSULTA');
            INSERT INTO dbo.tblSAPUsuarioRoles (IDUSUARIO, IDROLES) VALUES
                (101, {RolExternoComercial}), (102, {RolExternoGerente}), (102, {RolExternoSinMapeo});
            """);

        // Los IDROLES reales están pendientes (el 0002 siembra los roles sin mapeo): aquí se fijan los de prueba.
        await EjecutarAsync(CadenaCotifly, $"""
            UPDATE dbo_cotifly.Roles SET idRolExterno = {RolExternoComercial} WHERE codigo = 'comercial';
            UPDATE dbo_cotifly.Roles SET idRolExterno = {RolExternoGerente} WHERE codigo = 'gerente_comercial';
            """);
    }

    /// <summary>ID externo nuevo y único en este proceso de pruebas.</summary>
    public static long NuevoIdExterno() => Interlocked.Increment(ref _siguienteIdExterno);

    /// <summary>Agrega una persona a <c>tblSAPUsuariosApp</c> con sus roles externos; devuelve su ID.</summary>
    public async Task<long> CrearPersonaExternaAsync(string nombreUsuario, string? descripcion, string? email, params int[] rolesExternos)
    {
        ArgumentNullException.ThrowIfNull(rolesExternos);
        var id = NuevoIdExterno();
        await using var conexion = new SqlConnection(CadenaIdentidad);
        await conexion.OpenAsync();
        await using (var comando = new SqlCommand(
            "INSERT INTO dbo.tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL, CLAVE) VALUES (@id, @nombre, @descripcion, @email, 'no-debe-leerse');",
            conexion))
        {
            comando.Parameters.AddWithValue("@id", (decimal)id);
            comando.Parameters.AddWithValue("@nombre", nombreUsuario);
            comando.Parameters.AddWithValue("@descripcion", (object?)descripcion ?? DBNull.Value);
            comando.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
            await comando.ExecuteNonQueryAsync();
        }

        await CambiarRolesExternosAsync(id, rolesExternos);
        return id;
    }

    /// <summary>Reemplaza los roles externos de la persona en <c>tblSAPUsuarioRoles</c>.</summary>
    public async Task CambiarRolesExternosAsync(long idExterno, params int[] rolesExternos)
    {
        ArgumentNullException.ThrowIfNull(rolesExternos);
        var valores = string.Join(", ", rolesExternos.Select(r => $"({idExterno}, {r})"));
        await EjecutarAsync(CadenaIdentidad,
            $"DELETE FROM dbo.tblSAPUsuarioRoles WHERE IDUSUARIO = {idExterno};"
            + (rolesExternos.Length == 0 ? string.Empty : $" INSERT INTO dbo.tblSAPUsuarioRoles (IDUSUARIO, IDROLES) VALUES {valores};"));
    }

    /// <summary>Cambia el <c>NOMBREUSUARIO</c> de la persona en el almacén.</summary>
    public async Task RenombrarPersonaExternaAsync(long idExterno, string nuevoNombre)
    {
        await using var conexion = new SqlConnection(CadenaIdentidad);
        await conexion.OpenAsync();
        await using var comando = new SqlCommand("UPDATE dbo.tblSAPUsuariosApp SET NOMBREUSUARIO = @nombre WHERE ID = @id;", conexion);
        comando.Parameters.AddWithValue("@nombre", nuevoNombre);
        comando.Parameters.AddWithValue("@id", (decimal)idExterno);
        await comando.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        _candado.Dispose();
        return _contenedor.DisposeAsync();
    }

    /// <summary>
    /// Base auxiliar con SOLO el script 0001 aplicado (sin 0002), para probar que la readiness detecta
    /// el esquema incompleto. Se crea una vez por contenedor. El 0001 fija
    /// <c>USE dbTarifario_Nexo_Pruebas</c>; aquí ese <c>USE</c> se redirige a la base auxiliar.
    /// </summary>
    public async Task<string> CadenaSolo0001Async()
    {
        await _candado.WaitAsync();
        try
        {
            if (_cadenaSolo0001 is null)
            {
                const string NombreAuxiliar = "dbSolo0001_Pruebas";
                await EjecutarAsync(CadenaMaster, $"CREATE DATABASE [{NombreAuxiliar}];");
                var cadena = ConBase(CadenaMaster, NombreAuxiliar);
                var script = (await File.ReadAllTextAsync(RutaScript(Scripts[0])))
                    .Replace($"USE {NombreBase};", $"USE [{NombreAuxiliar}];", StringComparison.OrdinalIgnoreCase);
                await EjecutarLotesAsync(cadena, PartirPorGo(script));
                _cadenaSolo0001 = cadena;
            }

            return _cadenaSolo0001;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Límite de las órdenes de prueba (s). El SQL Server de Testcontainers corre sobre el disco virtual de Docker
    /// Desktop (WSL2) y a veces se detiene varios segundos en E/S (visto: <c>CREATE DATABASE</c> del fixture superando
    /// los 30 s por defecto con "Execution Timeout Expired (258)", sobre todo con otro contenedor recién destruido).
    /// Es un margen para la infraestructura de pruebas; la API usa el valor por defecto.
    /// </summary>
    public const int TiempoMaximoOrdenSeg = 120;

    /// <summary>Cadena a <paramref name="baseDatos"/> en el contenedor, con <see cref="TiempoMaximoOrdenSeg"/>.</summary>
    public static string ConBase(string cadena, string baseDatos) =>
        new SqlConnectionStringBuilder(cadena)
        {
            InitialCatalog = baseDatos,
            TrustServerCertificate = true,
            CommandTimeout = TiempoMaximoOrdenSeg,
        }.ConnectionString;

    /// <summary>Ejecuta un lote T-SQL.</summary>
    public static async Task EjecutarAsync(string cadena, string sql)
    {
        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = new SqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Ejecuta un escalar T-SQL.</summary>
    public static async Task<T?> EscalarAsync<T>(string cadena, string sql)
    {
        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = new SqlCommand(sql, conexion);
        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? default : (T)valor;
    }

    /// <summary>
    /// Ejecuta un script como lo haría sqlcmd: lo parte en lotes por las líneas <c>GO</c> y los corre en
    /// orden sobre una misma conexión (las opciones <c>SET</c> y <c>USE</c> persisten entre lotes).
    /// </summary>
    public static async Task EjecutarScriptAsync(string cadena, string ruta) =>
        await EjecutarLotesAsync(cadena, PartirPorGo(await File.ReadAllTextAsync(ruta)));

    private static async Task EjecutarLotesAsync(string cadena, IReadOnlyList<string> lotes)
    {
        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync();
        foreach (var lote in lotes)
        {
            await using var comando = new SqlCommand(lote, conexion) { CommandTimeout = TiempoMaximoOrdenSeg };
            await comando.ExecuteNonQueryAsync();
        }
    }

    public static IReadOnlyList<string> PartirPorGo(string script) =>
        [.. ExpresionGo().Split(script).Select(l => l.Trim()).Where(l => l.Length > 0)];

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionGo();
}
