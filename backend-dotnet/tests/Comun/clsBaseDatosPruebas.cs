using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SQL Server 2019 en Testcontainers con el esquema real (ADR-008, ADR-010):
/// crea <c>dbTarifario_Nexo_Pruebas</c>, aplica <c>db/migrations_mssql/0001</c> a <c>0010</c>, <c>db/scripts/02_alter_cubicador.sql</c>, el <c>03</c> (HU-014, #188), la propuesta <c>db/scripts/04</c> (HU-023.5), el <c>05</c> (#188), el <c>06</c> (#191, Idempotency-Key), el <c>07</c>, el <c>08</c> (HU-014.3), el <c>09</c> (HU-018.1), el <c>10</c> (HU-028.1, #183), el <c>11</c> (HU-030.1, #184) y los siguientes hasta el <c>18</c> (HU-042, #242, #247) partidos por <c>GO</c> (HU-021: el 0009
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
        // HU-014 (#188): ordenVehiculo, idServicioConexoCatalogo y el CHECK del motivo con BUSQUEDA_AGOTADA (db/scripts/03; el backend
        // mapea sus columnas). OJO: deja el mismo CHECK que el 07, así que una base con ScriptsSin07 YA admite BUSQUEDA_AGOTADA; las
        // pruebas "sin el 07" usan CrearBaseSin07Async, que vuelve al CHECK del 02.
        Script03,
        // HU-023.5 (#160): cola de la cubicación de la licitación en segundo plano (db/scripts/04, pendiente de aprobación;
        // el backend mapea sus columnas, así que las pruebas lo aplican en las dos cadenas).
        Script04,
        // HU-014 (#188): categoriaServicioConexo (db/scripts/05; el backend la mapea).
        Script05,
        // #191: CapturasIdempotentes (db/scripts/06; la Idempotency-Key persistente: el backend la exige en /health/ready).
        Script06,
        // HU-018.1 (#155): scorePrioridad en la cabecera (db/scripts/09). Va ANTES del 07 en esta lista (el ejecutador corre 07, 08 y 09
        // por nombre; son independientes) porque el backend mapea scorePrioridad: `ScriptsSin07` (todo hasta el 09) es la base de las
        // pruebas del 07 y la incluye.
        Script09,
        // HU-028.1 (#183): el rechazo de la línea por la Mesa de Costeo (db/scripts/10; el backend mapea motivoRechazo y rechazadaAt). Va
        // antes del 07, como el 09, para que `ScriptsSin07` lo incluya.
        Script10,
        // HU-030.1 (#184): pesoConTaraKg en la ruta (db/scripts/11; el backend lo mapea). Como el 09 y el 10, va antes del 07 en esta lista.
        Script11,
        // HU-032 (#197): el vehículo elegido de la ruta y las medidas NULL de la mercancía (db/scripts/12; el backend lo mapea). Como el 11,
        // va antes del 07 en esta lista.
        Script12,
        // HU-033 (#199): seguimiento de MailSender en Correos y resultado diferido a Nexo (db/scripts/13; el backend lo mapea). Como el 12,
        // va antes del 07 en esta lista.
        Script13,
        // HU-036 (#205, #206): finalizadaAt y finalizadaPor en la cabecera (db/scripts/14; el backend los mapea). Como el 13, va antes del
        // 07 en esta lista.
        Script14,
        // HU-040.1 (#229): varias asignaciones activas por usuario y TokensRefresco.rolSesion (db/scripts/16; el backend mapea rolSesion).
        // Como el 14, va antes del 07 en esta lista.
        Script16,
        // HU-042.1/HU-042.2 (#242, #243): la copia local del margen de SAP TM y el log del job (db/scripts/17; el backend mapea sus tablas).
        // Como el 16, va antes del 07 en esta lista.
        Script17,
        // HU-042.4 (#247): SapMapeoTrqType.lineaNegocioId con su FK a MargenesPolitica (db/scripts/18; el backend resuelve por ese id). Su
        // backfill cruza por el texto las filas que sembró el 0001.
        Script18,
        // HU-023 (#176): BUSQUEDA_AGOTADA en el CHECK del motivo (db/scripts/07, lo aplica @scalderon-bs).
        Script07,
        // HU-014.3 (#152): retiro de las vistas del borrador de métricas (db/scripts/08, lo aplica el DBA).
        Script08,
        // HU-037.1 (#209): el rol interno director (db/scripts/15: CHECK de Roles y la fila director sin rol externo). Va al final: no
        // agrega columnas, así que no mueve los índices ScriptsHasta*/ScriptsSin07.
        Script15,
    ];

    /// <summary>HU-040.1 (#229): el 16 de las asignaciones activas múltiples y el rol de la sesión (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script16 = "../scripts_bd/16_multiples_roles_activos.sql";

    /// <summary>HU-042.1 (#242): el 17 de la copia local del margen de SAP TM y su log (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script17 = "../scripts_bd/17_sap_margen_sincronizacion.sql";

    /// <summary>HU-042.4 (#247): el 18 de <c>SapMapeoTrqType.lineaNegocioId</c> (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script18 = "../scripts_bd/18_sap_mapeo_fk_margenes_politica.sql";

    /// <summary>
    /// HU-042 (#243, #247): scripts hasta el 16 incluido (sin el 17, el 18, el 07, el 08 ni el 15). <c>Scripts[ScriptsHasta16 - 1]</c> es el
    /// 16 (lo verifica <c>clsEsquemaTests</c>): la base de la prueba "falta 17". <c>ScriptsHasta16 - k</c> son las bases "sin el 16", "sin el
    /// 14"... que antes se escribían como <c>ScriptsSin07 - k</c>.
    /// </summary>
    public const int ScriptsHasta16 = 22;

    /// <summary>HU-037.1 (#209): el 15 del rol director (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script15 = "../scripts_bd/15_rol_director.sql";

    /// <summary>HU-014.3 (#152): el 08 que retira las vistas del borrador de métricas (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script08 = "../scripts_bd/08_retirar_vistas_actividad.sql";

    /// <summary>HU-018.1 (#155): el 09 de <c>scorePrioridad</c> (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script09 = "../scripts_bd/09_score_prioridad.sql";

    /// <summary>HU-028.1 (#183, HU-028.2 #182): el 10 del rechazo de la línea (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script10 = "../scripts_bd/10_rechazo_linea.sql";

    /// <summary>HU-030.1 (#184): el 11 del peso con tara de la ruta (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script11 = "../scripts_bd/11_peso_con_tara.sql";

    /// <summary>HU-032 (#197): el 12 del vehículo elegido y las mercancías sin medidas (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script12 = "../scripts_bd/12_vehiculo_elegido.sql";

    /// <summary>HU-033 (#199): el 13 del correo por MailSender y el resultado diferido a Nexo (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script13 = "../scripts_bd/13_correo_mailsender.sql";

    /// <summary>HU-036 (#205, #206): el 14 de la cotización finalizada (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script14 = "../scripts_bd/14_cotizacion_finalizada.sql";

    /// <summary>
    /// Scripts hasta el 09 (sin el 10, el 11, el 07 ni el 08). <c>Scripts[ScriptsHasta09 - 1]</c> es el 09 (lo verifica
    /// <c>clsEsquemaTests</c>): la base de las pruebas "falta 10" (HU-028.1, #183) y "falta 11" (HU-030.1, #184).
    /// </summary>
    public const int ScriptsHasta09 = 16;

    /// <summary>HU-014 (#188): el 03 del orden del camión y el id del catálogo del conexo (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script03 = "../scripts_bd/03_orden_vehiculo_y_conexo_catalogo.sql";

    /// <summary>HU-014 (#188): el 05 de la categoría del conexo (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script05 = "../scripts_bd/05_categoria_conexo.sql";

    /// <summary>#191: el 06 de la Idempotency-Key persistente (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script06 = "../scripts_bd/06_idempotencia_captura.sql";

    /// <summary>Scripts hasta el 03 (sin el 04, el 05, el 09, el 10, el 11, el 07 ni el 08). <c>Scripts[ScriptsHasta03 - 1]</c> es el 03.</summary>
    public const int ScriptsHasta03 = 12;

    /// <summary>Scripts hasta el 04 (sin el 05, el 09, el 07 ni el 08). <c>Scripts[ScriptsHasta04 - 1]</c> es el 04 (lo verifica <c>clsEsquemaTests</c>).</summary>
    public const int ScriptsHasta04 = 13;

    /// <summary>Scripts hasta el 05 (sin el 06, el 09, el 07 ni el 08). <c>Scripts[ScriptsHasta05 - 1]</c> es el 05.</summary>
    public const int ScriptsHasta05 = 14;

    /// <summary>#191: scripts hasta el 06 (sin el 09, el 07 ni el 08). <c>Scripts[ScriptsHasta06 - 1]</c> es el 06.</summary>
    public const int ScriptsHasta06 = 15;

    /// <summary>HU-023 (#176): el 07 del quinto motivo (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script07 = "../scripts_bd/07_motivo_busqueda_agotada.sql";

    /// <summary>HU-023.5 (#160): la propuesta 04 de la cubicación en segundo plano (relativa a <c>migrations_mssql</c>).</summary>
    public const string Script04 = "../scripts_bd/04_cubicacion_segundo_plano.sql";

    /// <summary>
    /// Scripts hasta el 07 incluido (con el 09, el 10, el 11... y el 18, sin el 08): la base migrada que todavía tiene las vistas del borrador de
    /// métricas. <c>Scripts[ScriptsHasta07 - 1]</c> es el 07 (lo verifica <c>clsEsquemaTests</c>).
    /// </summary>
    public const int ScriptsHasta07 = 25;

    /// <summary>
    /// Todos los scripts anteriores al 07 en <see cref="Scripts"/> (sin el 07 ni los que van después). <c>Scripts[ScriptsSin07]</c> es el 07
    /// (lo verifica <c>clsEsquemaTests</c>). Si otra rama agrega un script que el backend mapea, va antes del 07 en la lista y este número
    /// sube (hoy llega hasta el 18: <c>Scripts[ScriptsSin07 - 1]</c> es el 18, HU-042.4 #247, y <c>Scripts[ScriptsSin07 - 2]</c> el 17, HU-042.1 #242;
    /// antes el 16, HU-040.1 #229; el 14, HU-036 #205; el 13, HU-033 #199; el 12, HU-032 #197, antes el 11, HU-030.1 #184, y antes el 10, HU-028.1 #183).
    /// HU-014 (#188): incluye el 03, que deja el CHECK del 07; para una base que de verdad no admite BUSQUEDA_AGOTADA,
    /// <see cref="CrearBaseSin07Async"/>.
    /// </summary>
    public const int ScriptsSin07 = 24;

    /// <summary>
    /// HU-014 (#188): el CHECK del motivo tal como lo deja el <c>02</c> (4 motivos, sin <c>BUSQUEDA_AGOTADA</c> ni <c>ISNULL</c>). Con el 03
    /// en la lista, es la única forma de tener una base con las columnas que mapea el backend y SIN el CHECK del 07 (el estado de una base
    /// que aplicó una versión del 03 anterior a #176, o del 02 sin 03 ni 07).
    /// </summary>
    public const string CheckDelMotivoDel02 = """
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
            motivoSinSeleccion IS NULL
            OR (motivoSinSeleccion IN ('EXTRADIMENSIONADO', 'SIN_CANDIDATOS_DIMENSIONALES', 'SUPERA_K_MAX', 'SIN_CARROCERIA_COMPATIBLE')
                AND resultadoCubicacion = 'SIN_SELECCION_AUTOMATICA'));
        """;

    /// <summary>
    /// HU-014 (#188): base aislada con <see cref="ScriptsSin07"/> y el CHECK del motivo vuelto al del <c>02</c> (<see cref="CheckDelMotivoDel02"/>):
    /// tiene todas las columnas que mapea el backend, pero NO admite <c>BUSQUEDA_AGOTADA</c>. Es la base de las pruebas "sin el 07".
    /// </summary>
    public async Task<clsBaseAuxiliarPruebas> CrearBaseSin07Async(string prefijo)
    {
        var aislada = await CrearBaseConScriptsAsync(prefijo, ScriptsSin07);
        try
        {
            await EjecutarAsync(aislada.Cadena, CheckDelMotivoDel02);
            return aislada;
        }
        catch
        {
            await aislada.DisposeAsync();
            throw;
        }
    }

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

    /// <summary>
    /// HU-042.2 (#243): collation con la que el ejecutador crea la base real (<c>db/ejecutar-bd.*</c>), distinta de la del servidor del
    /// contenedor (<c>SQL_Latin1_General_CP1_CI_AS</c>): con ella, una tabla temporal sin <c>COLLATE DATABASE_DEFAULT</c> choca con las tablas.
    /// </summary>
    public const string CollationCotifly = "Latin1_General_100_CI_AS_SC_UTF8";

    /// <summary>HU-042.2 (#243): nombre de la base falsa del DW con <c>dbo.DimBPSolicitante</c>.</summary>
    public const string NombreBaseDwOperaciones = "DWCubosOperacionesEBS";

    /// <summary>Script de instalación desde cero (<c>db/scripts/01_crear_esquema.sql</c>).</summary>
    public const string ScriptInstalacion = "01_crear_esquema.sql";

    /// <summary>IDROLES externo de prueba mapeado a <c>comercial</c>.</summary>
    public const int RolExternoComercial = 32;

    /// <summary>IDROLES externo de prueba mapeado a <c>gerente_comercial</c>.</summary>
    public const int RolExternoGerente = 31;

    /// <summary>
    /// HU-037 (#210): IDROLES externo de prueba mapeado a <c>director</c>. En la base compartida es el 31 (#209, provisional); aquí es
    /// otro número para no mover el 31 del gerente que usan las demás pruebas.
    /// </summary>
    public const int RolExternoDirector = 33;

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
    private string? _cadenaDwOperaciones;
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
                // HU-014 (#188): el 03 va después del 02 por nombre, como en el ejecutador.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("03_orden_vehiculo_y_conexo_catalogo.sql"));
                // HU-023.5: la propuesta 04 (pendiente de aprobación) también, porque el backend mapea sus columnas.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("04_cubicacion_segundo_plano.sql"));
                // HU-014 (#188): el 05 (categoría del conexo).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("05_categoria_conexo.sql"));
                // #191: el 06 (Idempotency-Key persistente) va después del 05 por nombre, como en el ejecutador.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("06_idempotencia_captura.sql"));
                // HU-023 (#176): el 07 corre después del 02 (el ejecutador lo toma solo).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("07_motivo_busqueda_agotada.sql"));
                // HU-014.3 (#152): el 08 va después del 07; sobre el 01 actual no hace nada (ya no crea las vistas).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("08_retirar_vistas_actividad.sql"));
                // HU-018.1 (#155): el 09 (scorePrioridad) va después del 08 por nombre, como en el ejecutador.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("09_score_prioridad.sql"));
                // HU-028.1 (#183) y HU-030.1 (#184): el 10 (rechazo de la línea) y el 11 (pesoConTaraKg) van después del 09 por nombre,
                // como en el ejecutador.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("10_rechazo_linea.sql"));
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("11_peso_con_tara.sql"));
                // HU-032 (#197): el 12 (vehículo elegido y medidas NULL) va después del 11 por nombre.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("12_vehiculo_elegido.sql"));
                // HU-033 (#199): el 13 (MailSender y resultado diferido) va después del 12 por nombre.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("13_correo_mailsender.sql"));
                // HU-036 (#205): el 14 (cotización finalizada) va después del 13 por nombre.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("14_cotizacion_finalizada.sql"));
                // HU-037.1 (#209): el 15 (rol director) va después del 14 por nombre.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("15_rol_director.sql"));
                // HU-040.1 (#229): el 16 (multi-rol) va después del 15 por nombre.
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("16_multiples_roles_activos.sql"));
                // HU-042 (#242, #247): el 17 (copia local del margen de SAP) y el 18 (lineaNegocioId del mapeo) van después del 16 por nombre;
                // los seeders corren después (el 02 llena lineaNegocioId).
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("17_sap_margen_sincronizacion.sql"));
                await EjecutarScriptAsync(auxiliar.Cadena, RutaScriptInstalacion("18_sap_mapeo_fk_margenes_politica.sql"));
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
            // HU-014 (#188): el 03, la propuesta pendiente 04 (HU-023.5, #160) y el 05: el backend mapea sus columnas.
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("03_orden_vehiculo_y_conexo_catalogo.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("04_cubicacion_segundo_plano.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("05_categoria_conexo.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("06_idempotencia_captura.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("07_motivo_busqueda_agotada.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("08_retirar_vistas_actividad.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("09_score_prioridad.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("10_rechazo_linea.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("11_peso_con_tara.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("12_vehiculo_elegido.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("13_correo_mailsender.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("14_cotizacion_finalizada.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("15_rol_director.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("16_multiples_roles_activos.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("17_sap_margen_sincronizacion.sql"));
            await EjecutarScriptAsync(CadenaCotifly, RutaScriptInstalacion("18_sap_mapeo_fk_margenes_politica.sql"));
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
                ({RolExternoComercial}, 'COMERCIAL'), ({RolExternoGerente}, 'GERENTE COMERCIAL'), ({RolExternoDirector}, 'DIRECTOR'),
                ({RolExternoSinMapeo}, 'CONSULTA');
            INSERT INTO dbo.tblSAPUsuarioRoles (IDUSUARIO, IDROLES) VALUES
                (101, {RolExternoComercial}), (102, {RolExternoGerente}), (102, {RolExternoSinMapeo});
            """);

        // Los IDROLES reales están pendientes (el 0002 siembra los roles sin mapeo): aquí se fijan los de prueba.
        await EjecutarAsync(CadenaCotifly, $"""
            UPDATE dbo_cotifly.Roles SET idRolExterno = {RolExternoComercial} WHERE codigo = 'comercial';
            UPDATE dbo_cotifly.Roles SET idRolExterno = {RolExternoGerente} WHERE codigo = 'gerente_comercial';
            UPDATE dbo_cotifly.Roles SET idRolExterno = {RolExternoDirector} WHERE codigo = 'director';
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
    /// HU-042.2 (#243): <c>DWCubosOperacionesEBS</c> falsa con <c>dbo.DimBPSolicitante</c> (<c>BP</c> y <c>NIT</c>, las columnas que lee la
    /// sincronización del margen de SAP TM; los tipos reales no se conocen: VARCHAR, como en un cubo). Se crea una vez por contenedor; cada
    /// prueba inserta sus BP con un prefijo único (<see cref="AgregarBpDwAsync"/>).
    /// </summary>
    public async Task<string> CadenaDwOperacionesAsync()
    {
        await _candado.WaitAsync();
        try
        {
            if (_cadenaDwOperaciones is null)
            {
                await CrearBaseAsync(CadenaMaster, NombreBaseDwOperaciones);
                var cadena = ConBase(CadenaMaster, NombreBaseDwOperaciones);
                await EjecutarAsync(cadena, """
                    CREATE TABLE dbo.DimBPSolicitante (
                        ID      INT IDENTITY(1, 1) NOT NULL PRIMARY KEY,
                        BP      VARCHAR(20)  NULL,
                        NIT     VARCHAR(30)  NULL,
                        NOMBRE  VARCHAR(200) NULL
                    );
                    """);
                _cadenaDwOperaciones = cadena;
            }

            return _cadenaDwOperaciones;
        }
        finally
        {
            _candado.Release();
        }
    }

    /// <summary>HU-042.2 (#243): agrega pares (BP, NIT) a la <c>DimBPSolicitante</c> falsa.</summary>
    public async Task AgregarBpDwAsync(params (string Bp, string? Nit)[] filas)
    {
        ArgumentNullException.ThrowIfNull(filas);
        var cadena = await CadenaDwOperacionesAsync();
        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync();
        foreach (var (bp, nit) in filas)
        {
            await using var comando = new SqlCommand("INSERT INTO dbo.DimBPSolicitante (BP, NIT, NOMBRE) VALUES (@bp, @nit, N'Cliente de prueba');", conexion);
            comando.Parameters.AddWithValue("@bp", bp);
            comando.Parameters.AddWithValue("@nit", (object?)nit ?? DBNull.Value);
            await comando.ExecuteNonQueryAsync();
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
    /// prueba (pase o falle) la base se borra (#91). Con <paramref name="collation"/> la base se crea con esa collation (HU-042.2, #243: la
    /// de la base real, <see cref="CollationCotifly"/>, distinta de la del servidor del contenedor).
    /// </summary>
    public async Task<clsBaseAuxiliarPruebas> CrearBaseConScriptsAsync(string prefijo, int scripts, string? collation = null)
    {
        await _candado.WaitAsync();
        try
        {
            var nombre = $"{prefijo}_{Guid.NewGuid():N}"[..40];
            try
            {
                return new clsBaseAuxiliarPruebas(CadenaMaster, nombre, await CrearBaseSinCandadoAsync(nombre, scripts, collation));
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

    private async Task<string> CrearBaseSinCandadoAsync(string nombre, int scripts, string? collation = null)
    {
        await CrearBaseAsync(CadenaMaster, nombre, collation);
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
    public static async Task CrearBaseAsync(string cadenaMaster, string nombre, string? collation = null)
    {
        for (var intento = 1; ; intento++)
        {
            try
            {
                await EjecutarAsync(cadenaMaster, collation is null ? $"CREATE DATABASE [{nombre}];" : $"CREATE DATABASE [{nombre}] COLLATE {collation};");
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
