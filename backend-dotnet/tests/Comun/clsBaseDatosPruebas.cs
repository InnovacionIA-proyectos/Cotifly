using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SQL Server 2019 en Testcontainers con el esquema real (ADR-008, ADR-010):
/// crea <c>dbTarifario_Nexo_Pruebas</c>, aplica <c>migrations_mssql/0001</c> y <c>0002</c> partidos por
/// <c>GO</c>, y crea una <c>dbsolucionesti</c> mínima con <c>dbo.tblSAPUsuariosApp</c> (incluida la columna
/// CLAVE, que el backend nunca debe leer). Un contenedor por ensamblado de pruebas.
/// </summary>
/// <remarks>
/// Dentro del contenedor del SDK se necesita el socket de Docker montado y
/// <c>TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal</c> (ver README §8).
/// </remarks>
public sealed partial class clsBaseDatosPruebas : IAsyncLifetime
{
    public const string Imagen = "mcr.microsoft.com/mssql/server:2019-latest";
    public const string NombreBase = "dbTarifario_Nexo_Pruebas";
    public const string NombreBaseIdentidad = "dbsolucionesti";
    public static readonly string[] Scripts = ["0001_script_bd_cotifly_sqlserver.sql", "0002_identidad_estado_agente.sql"];

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

        CadenaMaster = _contenedor.GetConnectionString();
        CadenaCotifly = ConBase(CadenaMaster, NombreBase);
        CadenaIdentidad = ConBase(CadenaMaster, NombreBaseIdentidad);

        await EjecutarAsync(CadenaMaster, $"CREATE DATABASE [{NombreBase}];");
        foreach (var script in Scripts)
        {
            await EjecutarScriptAsync(CadenaCotifly, RutaScript(script));
        }

        await EjecutarAsync(CadenaMaster, $"CREATE DATABASE [{NombreBaseIdentidad}];");
        await EjecutarAsync(CadenaIdentidad, """
            CREATE TABLE dbo.tblSAPUsuariosApp (
                ID                  INT           NOT NULL PRIMARY KEY,
                NOMBREUSUARIO       VARCHAR(50)   NULL,
                DESCRIPCIONUSUARIO  VARCHAR(200)  NULL,
                EMAIL               VARCHAR(200)  NULL,
                CLAVE               VARCHAR(200)  NULL
            );
            INSERT INTO dbo.tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL, CLAVE) VALUES
                (101, 'APEREZ', 'Ana Pérez', 'aperez@boterosoto.com.co', 'no-debe-leerse'),
                (102, 'LGOMEZ', 'Luis Gómez', 'lgomez@boterosoto.com.co', 'no-debe-leerse');
            """);
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

    public static string ConBase(string cadena, string baseDatos) =>
        new SqlConnectionStringBuilder(cadena) { InitialCatalog = baseDatos, TrustServerCertificate = true }.ConnectionString;

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
            await using var comando = new SqlCommand(lote, conexion) { CommandTimeout = 120 };
            await comando.ExecuteNonQueryAsync();
        }
    }

    public static IReadOnlyList<string> PartirPorGo(string script) =>
        [.. ExpresionGo().Split(script).Select(l => l.Trim()).Where(l => l.Length > 0)];

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionGo();
}
