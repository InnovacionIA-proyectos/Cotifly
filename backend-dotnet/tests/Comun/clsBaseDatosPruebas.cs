using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SQL Server 2019 en Testcontainers con el esquema real (ADR-008, ADR-010):
/// crea <c>dbTarifario_Nexo_Pruebas</c>, aplica <c>db/migrations_mssql/0001</c> a <c>0010</c>, <c>db/scripts/02_alter_cubicador.sql</c> y la propuesta <c>db/scripts/pendientes/04</c> (HU-023.5) partidos por <c>GO</c> (HU-021: el 0009
/// reestructura las cotizaciones en rutas y camiones y el 0010 agrega los nombres de municipio; las pruebas de esquema verifican además
/// que <c>db/scripts/01_crear_esquema.sql</c> deja el mismo esquema, <see cref="CrearBaseInstaladaAsync"/>), y crea una <c>dbsolucionesti</c> falsa con las tres tablas del almacén corporativo y sus tipos reales:
/// <c>dbo.tblSAPUsuariosApp</c> (ID NUMERIC, incluida la columna CLAVE, que el backend nunca debe leer),
/// <c>dbo.tblSAPUsuarioRoles</c> (IDUSUARIO e IDROLES INT, sin columna ID: Cotifly no la lee, #172) y <c>dbo.tblSAPRoles</c>, los tipos que
/// Nexo verificó en <c>dbSolucionesTI_Prueba</c> (la base de identidad de QA, #172; aquí conserva el nombre <c>dbsolucionesti</c>). Mapea los roles internos de
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
    public static readonly string[] Scripts =
    [
        "0001_script_bd_cotifly_sqlserver.sql", "0002_identidad_estado_agente.sql", "0003_metricas_reenviadas.sql", "0004_costeo_nexo.sql",
        "0005_lineas_solicitud.sql", "0006_textos_y_concurrencia.sql", "0007_captura_nexo_campos.sql",
        "0008_horas_por_linea.sql", "0009_reestructuracion_cotizaciones.sql", "0010_nombres_municipio.sql",
        // HU-023.3 (#158): db/scripts/02_alter_cubicador.sql (de @scalderon-bs, sin cambios) va después del 0010.
        Script02,
        // HU-023.5 (#160): cola de la cubicación de la licitación en segundo plano (db/scripts/pendientes/04, pendiente de aprobación;
        // el backend mapea sus columnas, así que las pruebas lo aplican en las dos cadenas).
        Script04,
        // HU-023 (#176): BUSQUEDA_AGOTADA en el CHECK del motivo (db/scripts/07, lo aplica @scalderon-bs).
        Script07,
    ];

    /// <summary>HU-023 (#176): el 07 del quinto motivo (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script07 = "../scripts_bd/07_motivo_busqueda_agotada.sql";

    /// <summary>HU-023.5 (#160): la propuesta 04 de la cubicación en segundo plano (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script04 = "../scripts_bd/pendientes/04_cubicacion_segundo_plano.sql";

    /// <summary>Scripts hasta el 02 del cubicador (sin el 04).</summary>
    public const int ScriptsHasta02 = 11;

    /// <summary>Ruta (relativa a <c>migrations_mssql</c>) del script <c>02</c> del cubicador, copiado a <c>scripts_bd</c>.</summary>
    public const string Script02 = "../scripts_bd/02_alter_cubicador.sql";

    /// <summary>Scripts hasta el 0009 (sin el 0010 ni el 02).</summary>
    public const int ScriptsHasta0009 = 9;

    /// <summary>Scripts hasta el 0010 (sin el 02 del cubicador).</summary>
    public const int ScriptsHasta0010 = 10;

    /// <summary>Scripts hasta el esquema anterior a HU-021 (0001 a 0008): para probar el 0009 sobre filas del esquema viejo.</summary>
    public const int ScriptsAntesDeHu021 = 8;

    /// <summary>Script de instalación desde cero (<c>db/scripts/01_crear_esquema.sql</c>).</summary>
    public const string ScriptInstalacion = "01_crear_esquema.sql";

    /// <summary>IDROLES externo de prueba mapeado a <c>comercial</c>.</summary>
    public const int RolExternoComercial = 32;

    /// <summary>IDROLES externo de prueba mapeado a <c>gerente_comercial</c>.</summary>
    public const int RolExternoGerente = 31;

    /// <summary>IDROLES externo de prueba sin mapeo (no da acceso).</summary>
    public const int RolExternoSinMapeo = 40;

    /// <summary>
    /// Contador de IDs externos de las personas de prueba. Empieza en 1.000.000 (sigue cabiendo en INT) para no chocar con los IDs fijos
    /// de las pruebas, como el 10123 del portal (#172).
    /// </summary>
    private static long _siguienteIdExterno = 1_000_000;

    private readonly MsSqlContainer _contenedor = new MsSqlBuilder(Imagen).Build();
    private readonly SemaphoreSlim _candado = new(1, 1);
    private string? _cadenaSolo0001;
    private string? _cadenaConCiudades;
    private string? _cadenaDirectorio;
    private string? _cadenaSin0004;
    private string? _cadenaSin0005;
    private string? _cadenaSin0006;

    /// <summary>
    /// Si es <c>true</c>, la base principal se arma con <c>db/scripts/01_crear_esquema.sql</c> y los seeders de <c>db/seeder/</c> (instalación
    /// desde cero, como la base compartida desde el 2026-10-08) en vez de las migraciones 0001 a 0010 (HU-021.12, #139).
    /// </summary>
    public bool InstalacionDesdeCero { get; init; }

    /// <summary>Cadena a <c>dbTarifario_Nexo_Pruebas</c> con el esquema aplicado.</summary>
    public string CadenaCotifly { get; private set; } = string.Empty;

    /// <summary>Cadena a la <c>dbsolucionesti</c> de pruebas.</summary>
    public string CadenaIdentidad { get; private set; } = string.Empty;

    /// <summary>Cadena a <c>master</c> (para crear bases auxiliares).</summary>
    public string CadenaMaster { get; private set; } = string.Empty;

    public static string RutaScript(string nombre) => Path.Combine(AppContext.BaseDirectory, "migrations_mssql", nombre);

    /// <summary>Ruta de un script de <c>db/scripts/</c> (o de <c>db/seeder/</c> con <c>seeder/</c> delante) copiado a la salida.</summary>
    public static string RutaScriptInstalacion(string nombre) => Path.Combine(AppContext.BaseDirectory, "scripts_bd", nombre);

    /// <summary>
    /// Base auxiliar nueva instalada desde cero con <c>db/scripts/01_crear_esquema.sql</c> (sin migraciones) y, si
    /// <paramref name="conSeeders"/>, los seeders de <c>db/seeder/</c>. Usarla con <c>await using</c>.
    /// </summary>
    public async Task<clsBaseAuxiliarPruebas> CrearBaseInstaladaAsync(string prefijo, bool conSeeders = false)
    {
        await _candado.WaitAsync();
        try
        {
            var nombre = $"{prefijo}_{Guid.NewGuid():N}"[..40];
            var auxiliar = new clsBaseAuxiliarPruebas(CadenaMaster, nombre, ConBase(CadenaMaster, nombre));
            try
            {
                await CrearBaseAsync(CadenaMaster, nombre);
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion(ScriptInstalacion));
                // HU-023.3: la instalación desde cero corre el 02 después del 01 (como el ejecutador).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("02_alter_cubicador.sql"));
                // HU-023.5: la propuesta 04 (pendiente de aprobación) también, porque el backend mapea sus columnas.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("pendientes/04_cubicacion_segundo_plano.sql"));
                // HU-023 (#176): el 07 corre después del 02 (el ejecutador lo toma solo).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("07_motivo_busqueda_agotada.sql"));
                if (conSeeders)
                {
                    foreach (var seeder in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scripts_bd", "seeder"), "*.sql").Order(StringComparer.Ordinal))
                    {
                        await EjecutarScriptAsync(auxiliar.Cadena, seeder);
                    }
                }

                return auxiliar;
            }
            catch
            {
                await auxiliar.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _candado.Release();
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _contenedor.StartAsync();

        CadenaMaster = ConBase(_contenedor.GetConnectionString(), "master");
        CadenaCotifly = ConBase(CadenaMaster, NombreBase);
        CadenaIdentidad = ConBase(CadenaMaster, NombreBaseIdentidad);

        await CrearBaseAsync(CadenaMaster, NombreBase);
        if (InstalacionDesdeCero)
        {
            // HU-021.12 (#139, regla de las pruebas de esquema): el esquema vigente de la base compartida, sin migraciones.
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion(ScriptInstalacion));
            // HU-023.3 (#158): el 02 del cubicador va después del 01, como lo corre el ejecutador.
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("02_alter_cubicador.sql"));
            // HU-023.5 (#160): la propuesta pendiente 04 (el backend mapea sus columnas; no fusionar sin aplicarla).
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("pendientes/04_cubicacion_segundo_plano.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("07_motivo_busqueda_agotada.sql"));
            foreach (var seeder in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "scripts_bd", "seeder"), "*.sql").Order(StringComparer.Ordinal))
            {
                await EjecutarScriptAsync(CadenaCotifly, seeder);
            }
        }
        else
        {
            foreach (var script in Scripts)
            {
                await EjecutarScriptAsync(CadenaCotifly, RutaScript(script));
            }
        }

        await CrearBaseAsync(CadenaMaster, NombreBaseIdentidad);
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
    public Task<long> CrearPersonaExternaAsync(string nombreUsuario, string? descripcion, string? email, params int[] rolesExternos) =>
        CrearPersonaExternaConIdAsync(NuevoIdExterno(), nombreUsuario, descripcion, email, rolesExternos);

    /// <summary>Agrega una persona con un <c>ID</c> fijo (NUMERIC) a <c>tblSAPUsuariosApp</c> con sus roles externos; devuelve su ID.</summary>
    public async Task<long> CrearPersonaExternaConIdAsync(long id, string nombreUsuario, string? descripcion, string? email, params int[] rolesExternos)
    {
        ArgumentNullException.ThrowIfNull(rolesExternos);
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
                await CrearBaseAsync(CadenaMaster, NombreAuxiliar);
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
    /// Base auxiliar con todos los scripts (HU-021: las ciudades son <c>origenNombre</c>/<c>destinoNombre</c> de la ruta, del 0010): para
    /// probar la concentración por ciudad de HU-010.1 con filas propias, sin ensuciar la base compartida. Se crea una vez por contenedor.
    /// </summary>
    public async Task<string> CadenaConCiudadesAsync()
    {
        await _candado.WaitAsync();
        try
        {
            if (_cadenaConCiudades is null)
            {
                const string NombreAuxiliar = "dbCiudades_Pruebas";
                await CrearBaseAsync(CadenaMaster, NombreAuxiliar);
                var cadena = ConBase(CadenaMaster, NombreAuxiliar);
                foreach (var nombre in Scripts)
                {
                    var script = (await File.ReadAllTextAsync(RutaScript(nombre)))
                        .Replace($"USE {NombreBase};", $"USE [{NombreAuxiliar}];", StringComparison.OrdinalIgnoreCase);
                    await EjecutarLotesAsync(cadena, PartirPorGo(script));
                }

                _cadenaConCiudades = cadena;
            }

            return _cadenaConCiudades;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// <c>DWCubosEBSSAP</c> falsa con <c>dbo.DimEjecutivo</c> y las columnas reales que verificó #81 (<c>ID, NOMBRE, REGIONAL, CORREO,
    /// USUARIO, FECHACREACION, FECHAMODIFICACION</c>; <c>USUARIO</c> trae el dominio pegado, <c>boterosoto\usuario</c>). Los tipos
    /// reales no se conocen: texto VARCHAR, como en un cubo. Se crea una vez por contenedor; cada prueba inserta sus propias filas
    /// con nombres únicos.
    /// </summary>
    public async Task<string> CadenaDirectorioEjecutivosAsync()
    {
        await _candado.WaitAsync();
        try
        {
            if (_cadenaDirectorio is null)
            {
                const string NombreAuxiliar = "DWCubosEBSSAP";
                await CrearBaseAsync(CadenaMaster, NombreAuxiliar);
                var cadena = ConBase(CadenaMaster, NombreAuxiliar);
                await EjecutarAsync(cadena, """
                    CREATE TABLE dbo.DimEjecutivo (
                        ID                  INT IDENTITY(1, 1) NOT NULL PRIMARY KEY,
                        NOMBRE              VARCHAR(200) NULL,
                        REGIONAL            VARCHAR(100) NULL,
                        CORREO              VARCHAR(200) NULL,
                        USUARIO             VARCHAR(100) NULL,
                        FECHACREACION       DATETIME     NULL DEFAULT GETDATE(),
                        FECHAMODIFICACION   DATETIME     NULL
                    );
                    """);
                _cadenaDirectorio = cadena;
            }

            return _cadenaDirectorio;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Base auxiliar con 0001, 0002 y 0003 pero SIN el 0004 (costeo en Nexo), para probar que la readiness lo detecta. Se crea
    /// una vez por contenedor; igual que en <see cref="CadenaSolo0001Async"/>, el <c>USE</c> de cada script se redirige.
    /// </summary>
    public async Task<string> CadenaSin0004Async()
    {
        await _candado.WaitAsync();
        try
        {
            if (_cadenaSin0004 is null)
            {
                const string NombreAuxiliar = "dbSin0004_Pruebas";
                await CrearBaseAsync(CadenaMaster, NombreAuxiliar);
                var cadena = ConBase(CadenaMaster, NombreAuxiliar);
                foreach (var nombre in Scripts.Take(3))
                {
                    var script = (await File.ReadAllTextAsync(RutaScript(nombre)))
                        .Replace($"USE {NombreBase};", $"USE [{NombreAuxiliar}];", StringComparison.OrdinalIgnoreCase);
                    await EjecutarLotesAsync(cadena, PartirPorGo(script));
                }

                _cadenaSin0004 = cadena;
            }

            return _cadenaSin0004;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>Base auxiliar con 0001 a 0004 pero SIN el 0005 (líneas de la solicitud, ADR-017), para la readiness. Una vez por contenedor.</summary>
    public async Task<string> CadenaSin0005Async()
    {
        await _candado.WaitAsync();
        try
        {
            _cadenaSin0005 ??= await CrearBaseSinCandadoAsync("dbSin0005_Pruebas", 4);
            return _cadenaSin0005;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>Base auxiliar con 0001 a 0005 pero SIN el 0006 (concurrencia y regla del PDF, ADR-018), para la readiness. Una vez por contenedor.</summary>
    public async Task<string> CadenaSin0006Async()
    {
        await _candado.WaitAsync();
        try
        {
            _cadenaSin0006 ??= await CrearBaseSinCandadoAsync("dbSin0006_Pruebas", 5);
            return _cadenaSin0006;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>
    /// Base auxiliar nueva (nombre único) con los primeros <paramref name="scripts"/> scripts aplicados: para probar el backfill del
    /// 0005 sobre filas creadas con el esquema anterior sin tocar la base compartida. Usarla con <c>await using</c>: al salir la
    /// prueba (pase o falle) la base se borra (#91).
    /// </summary>
    public async Task<clsBaseAuxiliarPruebas> CrearBaseConScriptsAsync(string prefijo, int scripts)
    {
        await _candado.WaitAsync();
        try
        {
            var nombre = $"{prefijo}_{Guid.NewGuid():N}"[..40];
            try
            {
                return new clsBaseAuxiliarPruebas(CadenaMaster, nombre, await CrearBaseSinCandadoAsync(nombre, scripts));
            }
            catch
            {
                // Si un script falla a medias, la base no debe quedar huérfana.
                await new clsBaseAuxiliarPruebas(CadenaMaster, nombre, ConBase(CadenaMaster, nombre)).DisposeAsync();
                throw;
            }
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>Aplica un script de <c>db/migrations_mssql</c> a una base auxiliar (redirige su <c>USE</c>).</summary>
    public static async Task AplicarScriptAsync(string cadena, string nombreScript)
    {
        var baseDestino = new SqlConnectionStringBuilder(cadena).InitialCatalog;
        var script = (await File.ReadAllTextAsync(RutaScript(nombreScript)))
            .Replace($"USE {NombreBase};", $"USE [{baseDestino}];", StringComparison.OrdinalIgnoreCase);
        await EjecutarLotesAsync(cadena, PartirPorGo(script));
    }

    private async Task<string> CrearBaseSinCandadoAsync(string nombre, int scripts)
    {
        await CrearBaseAsync(CadenaMaster, nombre);
        var cadena = ConBase(CadenaMaster, nombre);
        foreach (var script in Scripts.Take(scripts))
        {
            await AplicarScriptAsync(cadena, script);
        }

        return cadena;
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

    /// <summary>Intentos de <see cref="CrearBaseAsync"/> ante el bloqueo de <c>model</c>.</summary>
    public const int IntentosCrearBase = 5;

    /// <summary>
    /// <c>CREATE DATABASE</c> con reintentos. SQL Server copia <c>model</c> para crear cada base y toma un candado exclusivo
    /// sobre ella: si varios fixtures (o bases auxiliares de clases que corren en paralelo) crean bases a la vez en el mismo
    /// contenedor, alguno falla con el error 1807 ("Could not obtain exclusive lock on database 'model'"). Visto en el CI
    /// (#147). Reintenta solo ese error, con espera creciente y algo de azar; cualquier otro error se propaga.
    /// </summary>
    public static async Task CrearBaseAsync(string cadenaMaster, string nombre)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await EjecutarAsync(cadenaMaster, $"CREATE DATABASE [{nombre}];");
                return;
            }
            catch (SqlException ex) when (intento < IntentosCrearBase && EsBloqueoDeModel(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds((500 * (1 << (intento - 1))) + Random.Shared.Next(0, 500)));
            }
        }
    }

    private static bool EsBloqueoDeModel(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number == 1807)
        || ex.Message.Contains("exclusive lock on database 'model'", StringComparison.OrdinalIgnoreCase);

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
