using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Scripts <c>db/migrations_mssql</c> aplicados en SQL Server 2019 y verificación de deriva entre el modelo
/// EF (Database First) y el esquema real (ADR-008): tablas, columnas, tipos y nulabilidad. Desde HU-021 la base compartida tiene
/// 0001 a 0010; las pruebas de los scripts anteriores (0002 a 0008) corren en una base AISLADA con el esquema viejo
/// (<see cref="clsBaseDatosPruebas.ScriptsAntesDeHu021"/>), y <c>db/scripts/01_crear_esquema.sql</c> debe dejar el mismo esquema que
/// la cadena de migraciones.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEsquemaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly string[] _tablasEsperadas =
    [
        "CorreoDestinatarios", "Correos", "CotizacionArchivosLicitacion", "CotizacionLineasSolicitud", "CotizacionMercancias", "CotizacionPdfsHistorial",
        "CotizacionServiciosConexos", "CotizacionVehiculosLineaSolicitud", "CotizacionesSolicitud", "EventosConversacion", "EventosHerramienta",
        "MargenAjustes", "MargenesPolitica", "Roles", "SapMapeoTrqType", "Sesiones",
        "TokensRefresco", "TokensRevocados", "UsuariosRoles", "UsuariosWeb",

        // #191: la Idempotency-Key persistente (db/scripts/06; la base de las pruebas aplica toda la cadena de scripts).
        "CapturasIdempotentes",

        // HU-042.1/HU-042.2 (#242, #243): la copia local del margen de SAP TM y el log del job (db/scripts/17).
        "SapMargenCliente", "SapMargenSyncLog",

        // HU-045.1/HU-045.2 (#263, #264): la tasa del seguro de la mercancía (db/scripts/20).
        "TarifasSeguro",
    ];

    /// <summary>Filtro de las tablas de respaldo que deja el 0009 (<c>Respaldo0009_*</c>): no son del modelo.</summary>
    private const string SinRespaldos = "TABLE_NAME NOT LIKE 'Respaldo0009[_]%'";

    [Fact]
    public async Task LosScripts0001A0010CreanLasVeinteTablasMasLosRespaldosDel0009()
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0));

        tablas.Where(t => !t.StartsWith("Respaldo0009_", StringComparison.Ordinal)).Should().BeEquivalentTo(_tablasEsperadas);
        tablas.Should().Contain("Respaldo0009_Cotizaciones", "el 0009 respalda las tablas que modifica");
    }

    [Fact]
    public async Task LaInstalacionDesdeCeroDejaElMismoEsquemaQueLasMigraciones()
    {
        // HU-021: db/scripts/01_crear_esquema.sql (instalación de la base compartida) y 0001 → 0010 deben coincidir en columnas (tipo,
        // largo, nulabilidad, collation y default), PK, UQ, FK, CHECK (con su definición), defaults e índices. El orden de las columnas
        // puede variar (las migraciones agregan con ALTER) y los respaldos del 0009 solo existen en la base migrada.
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbInstalada");

        var migrada = (await FotoEsquemaAsync(BaseDatos.CadenaCotifly)).Where(l => !l.StartsWith("Respaldo0009_", StringComparison.Ordinal)).ToList();
        var desdeCero = await FotoEsquemaAsync(instalada.Cadena);

        desdeCero.Except(migrada).Should().BeEmpty("lo que trae la instalación desde cero debe estar en la base migrada");
        migrada.Except(desdeCero).Should().BeEmpty("lo que deja la cadena de migraciones debe estar en la instalación desde cero");
        (await VistasAsync(instalada.Cadena)).Should().BeEmpty("el 01 ya no crea las vistas del borrador de métricas (HU-014.3, #152)");
        (await VistasAsync(BaseDatos.CadenaCotifly)).Should().BeEmpty("el 08 las retira de la base migrada");
    }

    [Fact]
    public async Task ElScript08RetiraLasVistasDelBorradorYEsIdempotente()
    {
        // HU-014.3 (#152): sobre una base migrada que todavía tiene las vistas (0001 a 0010, 02, 04 y 07) las borra; repetirlo, o
        // correrlo sobre una instalación desde cero (que ya no las tiene), no cambia nada.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbRetiro08", clsBaseDatosPruebas.ScriptsHasta07);
        (await VistasAsync(migrada.Cadena)).Should().BeEquivalentTo(["vActividadPorComercial", "vAjustesMargenPorComercial"], "la cadena hasta el 07 las tiene");

        await clsBaseDatosPruebas.AplicarScriptAsync(migrada.Cadena, clsBaseDatosPruebas.Script08);

        (await VistasAsync(migrada.Cadena)).Should().BeEmpty();
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script08);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem08Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script08);
    }

    [Fact]
    public void LosCortesDeLaListaDeScriptsApuntanAlScriptQueNombran()
    {
        // HU-014.3 (#152): al fusionar ramas que agregan scripts a clsBaseDatosPruebas.Scripts (08, 09, ...), git puede unir la lista sin
        // conflicto y dejar una constante de corte apuntando a otro script; las pruebas que la usan siguen pasando sobre una base equivocada.
        var scripts = clsBaseDatosPruebas.Scripts;

        scripts.Should().OnlyHaveUniqueItems();
        scripts[clsBaseDatosPruebas.ScriptsAntesDeHu021 - 1].Should().Be("0008_horas_por_linea.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta0009 - 1].Should().Be("0009_reestructuracion_cotizaciones.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta0010 - 1].Should().Be("0010_nombres_municipio.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta02 - 1].Should().Be(clsBaseDatosPruebas.Script02);
        scripts[clsBaseDatosPruebas.ScriptsHasta03 - 1].Should().Be(clsBaseDatosPruebas.Script03, "ScriptsHasta03 incluye el 03 y no el 04");
        scripts[clsBaseDatosPruebas.ScriptsHasta04 - 1].Should().Be(clsBaseDatosPruebas.Script04, "ScriptsHasta04 incluye el 04 y no el 05");
        scripts[clsBaseDatosPruebas.ScriptsHasta05 - 1].Should().Be(clsBaseDatosPruebas.Script05, "ScriptsHasta05 incluye el 05 y no el 06");
        scripts[clsBaseDatosPruebas.ScriptsHasta06 - 1].Should().Be(clsBaseDatosPruebas.Script06, "ScriptsHasta06 incluye el 06 y no el 09");
        scripts[clsBaseDatosPruebas.ScriptsHasta09 - 1].Should().Be(clsBaseDatosPruebas.Script09, "ScriptsHasta09 incluye el 09 y no el 10 ni el 11");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 6].Should().Be(clsBaseDatosPruebas.Script10, "ScriptsHasta16 incluye el 10 (el backend mapea el rechazo)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 5].Should().Be(clsBaseDatosPruebas.Script11, "ScriptsHasta16 incluye el 11 (el backend mapea pesoConTaraKg)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 4].Should().Be(clsBaseDatosPruebas.Script12, "ScriptsHasta16 incluye el 12 (el backend mapea el vehículo elegido)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 3].Should().Be(clsBaseDatosPruebas.Script13, "ScriptsHasta16 incluye el 13 (el backend mapea MailSender y el resultado diferido)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 2].Should().Be(clsBaseDatosPruebas.Script14, "ScriptsHasta16 incluye el 14 (el backend mapea la cotización finalizada)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 - 1].Should().Be(clsBaseDatosPruebas.Script16, "ScriptsHasta16 llega hasta el 16 (el backend mapea TokensRefresco.rolSesion)");
        // HU-042 (#242, #243, #247) y #261 punto 4: el 17, el 18 y el 19 van después del 16 y antes del 07 (el backend mapea sus tablas,
        // lineaNegocioId y precioVenta, en ese orden).
        scripts[clsBaseDatosPruebas.ScriptsHasta16].Should().Be(clsBaseDatosPruebas.Script17, "después del 16 va el 17 (copia local del margen de SAP)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 + 1].Should().Be(clsBaseDatosPruebas.Script18, "después del 17 va el 18 (lineaNegocioId del mapeo)");
        scripts[clsBaseDatosPruebas.ScriptsHasta16 + 2].Should().Be(clsBaseDatosPruebas.Script19, "después del 18 va el 19 (precioVenta del conexo, #261)");
        // HU-045 (#263, #264): el 20 (TarifasSeguro y el seguro de la ruta) va después del 19.
        scripts[clsBaseDatosPruebas.ScriptsHasta16 + 3].Should().Be(clsBaseDatosPruebas.Script20, "después del 19 va el 20 (seguro de la mercancía, #264)");
        scripts[clsBaseDatosPruebas.ScriptsSin07 - 1].Should().Be(clsBaseDatosPruebas.Script20, "ScriptsSin07 llega hasta el 20");
        scripts[clsBaseDatosPruebas.ScriptsSin07].Should().Be(clsBaseDatosPruebas.Script07, "ScriptsSin07 corta justo antes del 07");
        scripts[clsBaseDatosPruebas.ScriptsHasta07 - 1].Should().Be(clsBaseDatosPruebas.Script07, "ScriptsHasta07 incluye el 07");
        // HU-037.1 (#209): el 15 (rol director) va al final, después del 08: no agrega columnas y clsRolDirectorScriptTests usa
        // Scripts.Length - 1 como "la base sin el 15".
        scripts[clsBaseDatosPruebas.ScriptsHasta07..].Should().Equal([clsBaseDatosPruebas.Script08, clsBaseDatosPruebas.Script15],
            "después del 07 van el 08 y el 15");
    }

    [Fact]
    public async Task ElScript0010EsIdempotenteYNoHaceNadaSobreUnaInstalacionDesdeCero()
    {
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem0010");

        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, "0010_nombres_municipio.sql");
    }

    [Fact]
    public async Task ElScript02DelCubicadorEsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-023.3 (#158): db/scripts/02_alter_cubicador.sql (de @scalderon-bs) va después del 0010 o del 01 y se puede repetir.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem02", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script02);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem02Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script02);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
            "SELECT CASE WHEN COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'idTipoMercancia') IS NULL "
            + "AND COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'codigoMercancia') IS NOT NULL THEN 1 ELSE 0 END"))
            .Should().Be(1, "el 02 cambia idTipoMercancia por codigoMercancia");
    }

    [Fact]
    public async Task HU014_ElScript05EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-014 (#188): db/scripts/05 (categoría del conexo; el 01 no la trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem05", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script05);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem05Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script05);
    }

    [Fact]
    public async Task HU014_El03NumeraLosCamionesExistentesPorCreatedAtYElIndiceFiltradoRechazaUnOrdenRepetido()
    {
        // HU-014 (#188): sobre una base con camiones escritos SIN ordenVehiculo (02 sin el 03, como la base compartida antes de aplicarlo),
        // el 03 los numera 1..n por ruta en el orden de createdAt (el desfase de 1 ms de antes de #188); después el índice único filtrado
        // rechaza un orden repetido en la misma ruta y admite varios NULL.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbOrden03", clsBaseDatosPruebas.ScriptsHasta02);
        var cadena = baseAislada.Cadena;
        Guid cotizacion = Guid.NewGuid(), ruta = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio) VALUES "
            + $"('{cotizacion}', 'PCN-ORD-03'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (id, cotizacionSolicitudId, orden) VALUES "
            + $"('{ruta}', '{cotizacion}', 1); "
            + "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, createdAt) VALUES "
            + $"(NEWID(), '{ruta}', N'TERCERO', '2026-10-01T10:00:00.002+00:00'), "
            + $"(NEWID(), '{ruta}', N'PRIMERO', '2026-10-01T10:00:00.000+00:00'), "
            + $"(NEWID(), '{ruta}', N'SEGUNDO', '2026-10-01T10:00:00.001+00:00');");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, clsBaseDatosPruebas.Script03);

        (await LeerAsync(
                $"SELECT CONCAT(ordenVehiculo, '=', tipoVehiculo) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud WHERE lineaSolicitudId = '{ruta}' ORDER BY ordenVehiculo",
                r => r.GetString(0), cadena))
            .Should().Equal("1=PRIMERO", "2=SEGUNDO", "3=TERCERO");
        var repetido = () => clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, ordenVehiculo) VALUES (NEWID(), '{ruta}', 2)");
        (await repetido.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo");
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId) VALUES (NEWID(), '{ruta}'), (NEWID(), '{ruta}')");
    }

    [Fact]
    public async Task ElScript04EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-023.5 (#160): la cola de la cubicación en segundo plano (propuesta pendiente 04; el 01 no la trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem04", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script04);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem04Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script04);
    }

    [Fact]
    public async Task I191_ElScript06EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // #191: CapturasIdempotentes (db/scripts/06; el 01 no la trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem06", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script06);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem06Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script06);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes') "
                + "AND name IN (N'ix_capturasIdempotentes_expiraAt', N'ix_capturasIdempotentes_folio', N'uq_capturasIdempotentes_usuarioId_operacion_clave')"))
            .Should().Be(3);
    }

    [Fact]
    public async Task I191_ElScript06RecreaLaTablaVaciaDeLaPropuestaAnteriorYSeDetieneSiTieneFilas()
    {
        // La propuesta de #173 (sin operacion) nunca la usó el backend: vacía se recrea; con filas, el 06 se detiene sin tocarla.
        await using var vacia = await BaseDatos.CrearBaseConScriptsAsync("dbIdem06Vieja", clsBaseDatosPruebas.ScriptsHasta05);
        await clsBaseDatosPruebas.EjecutarAsync(vacia.Cadena, clsIdempotenciaSqlTests.TablaPropuestaAnterior);

        await clsBaseDatosPruebas.AplicarScriptAsync(vacia.Cadena, clsBaseDatosPruebas.Script06);

        (await clsBaseDatosPruebas.EscalarAsync<int>(vacia.Cadena,
            "SELECT CASE WHEN COL_LENGTH(N'dbo_cotifly.CapturasIdempotentes', N'operacion') IS NULL THEN 0 ELSE 1 END")).Should().Be(1);

        await using var conFilas = await BaseDatos.CrearBaseConScriptsAsync("dbIdem06Filas", clsBaseDatosPruebas.ScriptsHasta05);
        await clsBaseDatosPruebas.EjecutarAsync(conFilas.Cadena, clsIdempotenciaSqlTests.TablaPropuestaAnterior
            + "INSERT INTO dbo_cotifly.CapturasIdempotentes (usuarioId, clave, huella, estado, expiraAt) VALUES (NEWID(), N'clave-vieja', REPLICATE('A', 64), N'reservada', SYSDATETIMEOFFSET());");

        var aplicar = () => clsBaseDatosPruebas.AplicarScriptAsync(conFilas.Cadena, clsBaseDatosPruebas.Script06);

        (await aplicar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("propuesta de #173");
        (await clsBaseDatosPruebas.EscalarAsync<int>(conFilas.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.CapturasIdempotentes")).Should().Be(1);
    }

    [Fact]
    public async Task HU0181_ElScript09EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCeroYDejaDecimal94Null()
    {
        // HU-018.1 (#155): scorePrioridad en la cabecera (db/scripts/09; el 01 no lo trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem09", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script09);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem09Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script09);
        (await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena,
                "SELECT CONCAT(ty.name, '(', c.precision, ',', c.scale, ')|', c.is_nullable) FROM sys.columns c "
                + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
                + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name = N'scorePrioridad'"))
            .Should().Be("decimal(9,4)|1");
    }

    [Fact]
    public async Task HU0301_ElScript11EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCeroYDejaDecimal122NullConSuCheck()
    {
        // HU-030.1 (#184): pesoConTaraKg en la ruta (db/scripts/11; el 01 no lo trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem11", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script11);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem11Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script11);
        (await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena,
                "SELECT CONCAT(ty.name, '(', c.precision, ',', c.scale, ')|', c.is_nullable) FROM sys.columns c "
                + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
                + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND c.name = N'pesoConTaraKg'"))
            .Should().Be("decimal(12,2)|1");
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
                "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_pesoConTaraKg' "
                + "AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud')"))
            .Should().Be(1);
    }

    [Fact]
    public async Task HU032_ElScript12EsIdempotenteDejaElVehiculoElegidoYLasMedidasNullYElCheckAdmiteSoloTresNullOTresPositivas()
    {
        // HU-032 (#197): tipoVehiculoElegido/carroceriaElegida en la ruta y medidas NULL (las tres o ninguna) en la mercancía.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem12", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script12);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem12Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script12);
        (await LeerAsync(
                "SELECT CONCAT(c.name, '|', TYPE_NAME(c.user_type_id), '|', c.max_length, '|', c.is_nullable) FROM sys.columns c "
                + "WHERE (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND c.name IN (N'tipoVehiculoElegido', N'carroceriaElegida')) "
                + "OR (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias') AND c.name IN (N'largoCm', N'anchoCm', N'altoCm'))",
                r => r.GetString(0), instalada.Cadena))
            .Should().BeEquivalentTo(["tipoVehiculoElegido|nvarchar|200|1", "carroceriaElegida|nvarchar|200|1",
                "largoCm|decimal|9|1", "anchoCm|decimal|9|1", "altoCm|decimal|9|1"]);
        var definicion = await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena,
            "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionPosiciones_medidas' "
            + "AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias')");
        definicion.Should().Contain("IS NULL").And.Contain("[largoCm]>(0)");
    }

    [Fact]
    public async Task HU033_ElScript13EsIdempotenteYDejaLasColumnasDeMailSenderYDelResultadoDiferidoConSusCheck()
    {
        // HU-033 (#199): Correos con el seguimiento de MailSender (y el CHECK del estado) y la cabecera con la sincronización del resultado.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem13", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script13);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem13Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script13);
        (await LeerAsync(
                "SELECT CONCAT(OBJECT_NAME(c.object_id), '.', c.name, '|', TYPE_NAME(c.user_type_id), '|', c.is_nullable) FROM sys.columns c "
                + "WHERE (c.object_id = OBJECT_ID(N'dbo_cotifly.Correos') AND c.name IN (N'idMailSender', N'redirigidoA', N'intentosEntrega', "
                + "N'enviadoAt', N'estadoActualizadoAt', N'siguienteConsultaAt', N'encoladoAt')) "
                + "OR (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name IN (N'sincronizacionResultadoNexo', "
                + "N'comentarioResultadoPendiente', N'resultadoSincronizadoAt', N'errorSincronizacionResultado', N'intentosSincronizacionResultado'))",
                r => r.GetString(0), instalada.Cadena))
            .Should().BeEquivalentTo([
                "Correos.idMailSender|uniqueidentifier|1", "Correos.redirigidoA|nvarchar|1", "Correos.intentosEntrega|int|1",
                "Correos.enviadoAt|datetimeoffset|1", "Correos.estadoActualizadoAt|datetimeoffset|1", "Correos.siguienteConsultaAt|datetimeoffset|1",
                "Correos.encoladoAt|datetimeoffset|1",
                "CotizacionesSolicitud.sincronizacionResultadoNexo|nvarchar|1", "CotizacionesSolicitud.comentarioResultadoPendiente|nvarchar|1",
                "CotizacionesSolicitud.resultadoSincronizadoAt|datetimeoffset|1", "CotizacionesSolicitud.errorSincronizacionResultado|nvarchar|1",
                "CotizacionesSolicitud.intentosSincronizacionResultado|int|0"]);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
                "SELECT COUNT(*) FROM sys.check_constraints WHERE name IN (N'ck_correos_estado', "
                + "N'ck_cotizaciones_sincronizacionResultadoNexo', N'ck_cotizaciones_intentosSincronizacionResultado')"))
            .Should().Be(3);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
                "SELECT COUNT(*) FROM sys.indexes WHERE name IN (N'ix_correos_siguienteConsultaAt', N'ux_correos_idMailSender') AND has_filter = 1"))
            .Should().Be(2);
    }

    [Fact]
    public async Task HU036_ElScript14EsIdempotenteYDejaLaFinalizacionConSuFkAUsuariosWebYSuCheck()
    {
        // HU-036 (#205): finalizadaAt y finalizadaPor (UNIQUEIDENTIFIER, el tipo de UsuariosWeb.id) en la cabecera, la FK y el CHECK.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem14", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script14);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem14Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script14);
        foreach (var cadena in new[] { migrada.Cadena, instalada.Cadena })
        {
            (await LeerAsync(
                    "SELECT CONCAT(c.name, '|', TYPE_NAME(c.user_type_id), '|', c.is_nullable) FROM sys.columns c "
                    + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name IN (N'finalizadaAt', N'finalizadaPor')",
                    r => r.GetString(0), cadena))
                .Should().BeEquivalentTo(["finalizadaAt|datetimeoffset|1", "finalizadaPor|uniqueidentifier|1"]);
            (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
                    "SELECT CONCAT(OBJECT_NAME(fk.referenced_object_id), '.', COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id)) "
                    + "FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id "
                    + "WHERE fk.name = N'fk_cotizaciones_finalizadaPor'"))
                .Should().Be("UsuariosWeb.id");
            (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
                    "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'ck_cotizaciones_finalizada' AND is_not_trusted = 0"))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task HU036_ElScript14SeAplicaSobreUnaBaseConDatosSinTocarlos()
    {
        // HU-036 (#205): sin backfill. Sobre una base hasta el 13 con una cotización ya escrita, el 14 la deja sin finalizar.
        await using var baseConDatos = await BaseDatos.CrearBaseConScriptsAsync("dbDatos14", clsBaseDatosPruebas.ScriptsHasta16 - 2);
        var cotizacion = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, estadoCosteo) VALUES ('{cotizacion}', 'COT-F14', 'autocosteo');");

        await clsBaseDatosPruebas.AplicarScriptAsync(baseConDatos.Cadena, clsBaseDatosPruebas.Script14);

        (await clsBaseDatosPruebas.EscalarAsync<string>(baseConDatos.Cadena,
                "SELECT CONCAT(estadoCosteo, '|', IIF(finalizadaAt IS NULL, 'null', 'valor'), '|', IIF(finalizadaPor IS NULL, 'null', 'valor')) "
                + $"FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{cotizacion}'"))
            .Should().Be("autocosteo|null|null");
    }

    [Fact]
    public async Task HU0401_ElScript16EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-040.1 (#229): sin uq_usuariosRoles_usuarioId_activo, con uq_usuariosRoles_usuarioId_rolId y TokensRefresco.rolSesion con su CHECK.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem16", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script16);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem16Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script16);
        foreach (var cadena in new[] { migrada.Cadena, instalada.Cadena })
        {
            (await LeerAsync(
                    "SELECT CONCAT(name, '|', is_unique) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles') AND name LIKE N'uq[_]%'",
                    r => r.GetString(0), cadena))
                .Should().BeEquivalentTo(["uq_usuariosRoles_usuarioId_rolId|1"]);
            (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
                    "SELECT CONCAT(TYPE_NAME(c.user_type_id), '|', c.max_length, '|', c.is_nullable) FROM sys.columns c "
                    + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.TokensRefresco') AND c.name = N'rolSesion'"))
                .Should().Be("nvarchar|60|1");
            (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
                    "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'ck_tokensRefresco_rolSesion' AND is_not_trusted = 0"))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task HU0401_ElScript16SeAplicaSobreUnaBaseConDatosSinTocarlosYDespuesAdmiteDosAsignacionesActivas()
    {
        // HU-040.1 (#229): no destructivo. Sobre una base hasta el 14 (sin el 16) con un usuario, su asignación activa y un refresh, el 16
        // no cambia ninguna fila; el refresh queda con rolSesion NULL. Antes del 16 la segunda asignación activa choca con el índice; después
        // entra, y el mismo rol dos veces sigue prohibido.
        await using var baseConDatos = await BaseDatos.CrearBaseConScriptsAsync("dbDatos16", clsBaseDatosPruebas.ScriptsHasta16 - 1);
        var usuario = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena, $"""
            IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'comercial')
                INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'comercial', N'Comercial');
            IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = 'gerente_comercial')
                INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'gerente_comercial', N'Gerente comercial');
            INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario) VALUES ('{usuario}', N'multi.rol.16');
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId, activo)
                SELECT '{usuario}', id, 1 FROM dbo_cotifly.Roles WHERE codigo = N'comercial';
            INSERT INTO dbo_cotifly.TokensRefresco (usuarioId, tokenHash, familiaId, expiraAt)
                VALUES ('{usuario}', REPLICATE('e', 64), NEWID(), DATEADD(DAY, 30, SYSDATETIMEOFFSET()));
            """);
        const string SegundaActiva = """
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId, activo)
                SELECT u.id, r.id, 1 FROM dbo_cotifly.UsuariosWeb u CROSS JOIN dbo_cotifly.Roles r
                WHERE u.nombreUsuario = N'multi.rol.16' AND r.codigo = N'gerente_comercial';
            """;
        var antesDel16 = () => clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena, SegundaActiva);
        (await antesDel16.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("uq_usuariosRoles_usuarioId_activo");
        var huellaAntes = await HuellaIdentidadAsync(baseConDatos.Cadena);

        await clsBaseDatosPruebas.AplicarScriptAsync(baseConDatos.Cadena, clsBaseDatosPruebas.Script16);

        (await HuellaIdentidadAsync(baseConDatos.Cadena)).Should().Equal(huellaAntes, "el 16 no cambia ninguna fila existente");
        (await clsBaseDatosPruebas.EscalarAsync<int>(baseConDatos.Cadena,
                $"SELECT COUNT(*) FROM dbo_cotifly.TokensRefresco WHERE usuarioId = '{usuario}' AND rolSesion IS NULL"))
            .Should().Be(1);
        await clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena, SegundaActiva);
        (await clsBaseDatosPruebas.EscalarAsync<int>(baseConDatos.Cadena,
                $"SELECT COUNT(*) FROM dbo_cotifly.UsuariosRoles WHERE usuarioId = '{usuario}' AND activo = 1"))
            .Should().Be(2);
        var mismoRol = () => clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena, SegundaActiva);
        (await mismoRol.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("uq_usuariosRoles_usuarioId_rolId");
    }

    [Fact]
    public async Task HU0401_ElScript16NoAflojaNadaSiFaltaLaUnicidadDeUsuarioYRol()
    {
        // HU-040.1 (#229): sin uq_usuariosRoles_usuarioId_rolId se podría duplicar el mismo rol activo: el 16 se detiene sin cambiar nada.
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbPre16", clsBaseDatosPruebas.ScriptsHasta16 - 1);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena,
            "ALTER TABLE dbo_cotifly.UsuariosRoles DROP CONSTRAINT uq_usuariosRoles_usuarioId_rolId;");
        var esquemaAntes = await FotoEsquemaAsync(aislada.Cadena);

        var aplicar = () => clsBaseDatosPruebas.AplicarScriptAsync(aislada.Cadena, clsBaseDatosPruebas.Script16);

        (await aplicar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("uq_usuariosRoles_usuarioId_rolId");
        (await FotoEsquemaAsync(aislada.Cadena)).Should().Equal(esquemaAntes);
    }

    [Fact]
    public async Task HU042_LosScripts17Y18SonIdempotentesSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-042.1 y HU-042.4 (#242, #247): repetir el 17 (tablas, SP) y el 18 (lineaNegocioId con su FK) no cambia el esquema.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem17", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script17);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script18);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem17Inst", conSeeders: true);
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script17);
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script18);
        foreach (var cadena in new[] { migrada.Cadena, instalada.Cadena })
        {
            (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
                    "SELECT CONCAT(TYPE_NAME(c.user_type_id), '|', c.is_nullable) FROM sys.columns c "
                    + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType') AND c.name = N'lineaNegocioId'"))
                .Should().Be("int|0");
            (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
                    "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType s JOIN dbo_cotifly.MargenesPolitica m ON m.id = s.lineaNegocioId "
                    + "WHERE m.lineaNegocio <> s.lineaNegocio"))
                .Should().Be(0, "el backfill del 18 (o el seeder 02) deja el id de la misma línea que dice el texto");
        }

        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType"))
            .Should().Be(5, "el seeder 02 llena lineaNegocioId cuando el 18 ya está aplicado");
    }

    [Fact]
    public async Task HU0424_ElScript18SeDetieneSinTocarNadaSiUnMapeoNoCruzaConUnaLinea()
    {
        // HU-042.4 (#247): una fila de SapMapeoTrqType con un texto que no existe en MargenesPolitica no se fuerza a NOT NULL.
        await using var aislada = await BaseDatos.CrearBaseConScriptsAsync("dbPre18", clsBaseDatosPruebas.ScriptsHasta16 + 1);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena,
            "INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo) VALUES (N'OHUB', N'', N'Almacenamiento', 1);");
        var esquemaAntes = await FotoEsquemaAsync(aislada.Cadena);

        var aplicar = () => clsBaseDatosPruebas.AplicarScriptAsync(aislada.Cadena, clsBaseDatosPruebas.Script18);

        (await aplicar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("no cruza");
        (await FotoEsquemaAsync(aislada.Cadena)).Should().Equal(esquemaAntes);
    }

    [Fact]
    public async Task HU032_ElCheckDelScript12RechazaLasMedidasParcialesConNull()
    {
        // Validación de HU-032: "NULL > 0" es UNKNOWN y un CHECK solo rechaza FALSE. Sin "IS NOT NULL", (NULL, 10, 10) pasaba el CHECK
        // "las tres NULL o las tres > 0". Se prueba la definición que deja el 12 sobre una tabla temporal con las mismas columnas.
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbCheck12");

        var resultados = await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena, """
            DECLARE @d NVARCHAR(MAX) = (SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionPosiciones_medidas'
                                        AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias'));
            CREATE TABLE #m (largoCm DECIMAL(10,2) NULL, anchoCm DECIMAL(10,2) NULL, altoCm DECIMAL(10,2) NULL, pesoKg DECIMAL(12,2) NULL);
            EXEC (N'ALTER TABLE #m ADD CONSTRAINT ck_m CHECK ' + @d);
            DECLARE @r NVARCHAR(200) = N'';
            BEGIN TRY INSERT #m VALUES (NULL, NULL, NULL, 5); SET @r += N'ninguna=ok;'; END TRY BEGIN CATCH SET @r += N'ninguna=error;'; END CATCH;
            BEGIN TRY INSERT #m VALUES (10, 10, 10, 5); SET @r += N'tres=ok;'; END TRY BEGIN CATCH SET @r += N'tres=error;'; END CATCH;
            BEGIN TRY INSERT #m VALUES (NULL, 10, 10, 5); SET @r += N'sinLargo=ok;'; END TRY BEGIN CATCH SET @r += N'sinLargo=error;'; END CATCH;
            BEGIN TRY INSERT #m VALUES (10, NULL, 10, 5); SET @r += N'sinAncho=ok;'; END TRY BEGIN CATCH SET @r += N'sinAncho=error;'; END CATCH;
            BEGIN TRY INSERT #m VALUES (10, 10, NULL, 5); SET @r += N'sinAlto=ok;'; END TRY BEGIN CATCH SET @r += N'sinAlto=error;'; END CATCH;
            BEGIN TRY INSERT #m VALUES (0, 10, 10, 5); SET @r += N'cero=ok;'; END TRY BEGIN CATCH SET @r += N'cero=error;'; END CATCH;
            SELECT @r;
            """);

        resultados.Should().Be("ninguna=ok;tres=ok;sinLargo=error;sinAncho=error;sinAlto=error;cero=error;");
    }

    [Fact]
    public async Task HU032_ElScript12ReemplazaElCheckDeUnaVersionPreviaSinIsNotNull()
    {
        // Validación de HU-032: un ambiente que aplicó la primera versión del 12 (CHECK sin "IS NOT NULL") queda con el CHECK corregido al
        // volver a correrlo.
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbCheck12Previo");
        await clsBaseDatosPruebas.EjecutarAsync(instalada.Cadena, """
            ALTER TABLE dbo_cotifly.CotizacionMercancias DROP CONSTRAINT ck_cotizacionPosiciones_medidas;
            ALTER TABLE dbo_cotifly.CotizacionMercancias WITH CHECK ADD CONSTRAINT ck_cotizacionPosiciones_medidas
                CHECK (((largoCm IS NULL AND anchoCm IS NULL AND altoCm IS NULL) OR (largoCm > 0 AND anchoCm > 0 AND altoCm > 0))
                       AND (pesoKg IS NULL OR pesoKg >= 0));
            """);

        await clsBaseDatosPruebas.AplicarScriptAsync(instalada.Cadena, clsBaseDatosPruebas.Script12);

        (await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena,
                "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionPosiciones_medidas' "
                + "AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias')"))
            .Should().Contain("[altoCm] IS NOT NULL");
    }

    [Fact]
    public async Task HU0281_ElScript10EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-028.2 (#182) / HU-028.1 (#183): motivoRechazo y rechazadaAt en el camión y 'rechazada' en los tres CHECK de estadoCosteo.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem10", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script10);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem10Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script10);
        (await LeerAsync(
                "SELECT CONCAT(c.name, '|', TYPE_NAME(c.user_type_id), '|', c.max_length, '|', c.is_nullable) FROM sys.columns c "
                + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud') AND c.name IN (N'motivoRechazo', N'rechazadaAt')",
                r => r.GetString(0), instalada.Cadena))
            .Should().BeEquivalentTo(["motivoRechazo|nvarchar|1000|1", "rechazadaAt|datetimeoffset|10|1"]);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
                "SELECT COUNT(*) FROM sys.check_constraints WHERE name IN (N'ck_cotizaciones_estadoCosteo', N'ck_cotizacionLineasSolicitud_estadoCosteo', "
                + "N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo') AND definition LIKE N'%''rechazada''%'"))
            .Should().Be(3, "las tres tablas admiten 'rechazada'");
    }

    [Fact]
    public async Task HU0281_ElScript10SeAplicaSobreUnaBaseConDatosYElCheckDelRechazoExigeElEstado()
    {
        // Sobre una base hasta el 09 con un camión ya escrito: el 10 se aplica sin tocar las filas; después el CHECK del rechazo solo admite
        // motivo y fecha con el camión en 'rechazada' (también con estadoCosteo NULL: el ISNULL del CHECK) y el de estadoCosteo admite 'rechazada'.
        await using var baseConDatos = await BaseDatos.CrearBaseConScriptsAsync("dbDatos10", clsBaseDatosPruebas.ScriptsHasta09);
        var cotizacion = Guid.NewGuid();
        var ruta = Guid.NewGuid();
        var camion = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, estadoCosteo) VALUES ('{cotizacion}', 'COT-R10', 'pendiente_costeo'); "
            + $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (id, cotizacionSolicitudId, orden, estadoCosteo) VALUES ('{ruta}', '{cotizacion}', 1, 'pendiente_costeo'); "
            + $"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, estadoCosteo, ordenVehiculo) VALUES ('{camion}', '{ruta}', 'pendiente_costeo', 1);");

        await clsBaseDatosPruebas.AplicarScriptAsync(baseConDatos.Cadena, clsBaseDatosPruebas.Script10);

        (await clsBaseDatosPruebas.EscalarAsync<string>(baseConDatos.Cadena,
            $"SELECT estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud WHERE id = '{camion}'")).Should().Be("pendiente_costeo");
        var motivoSinRechazo = () => clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"UPDATE dbo_cotifly.CotizacionVehiculosLineaSolicitud SET motivoRechazo = N'x' WHERE id = '{camion}'");
        (await motivoSinRechazo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_rechazo");
        var fechaSinEstado = () => clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"UPDATE dbo_cotifly.CotizacionVehiculosLineaSolicitud SET estadoCosteo = NULL, rechazadaAt = SYSDATETIMEOFFSET() WHERE id = '{camion}'");
        (await fechaSinEstado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_rechazo");

        await clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"UPDATE dbo_cotifly.CotizacionVehiculosLineaSolicitud SET estadoCosteo = 'rechazada', motivoRechazo = N'Ruta sin tarifa', rechazadaAt = SYSDATETIMEOFFSET() WHERE id = '{camion}'; "
            + $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET estadoCosteo = 'rechazada' WHERE id = '{ruta}'; "
            + $"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoCosteo = 'rechazada' WHERE id = '{cotizacion}';");
        (await clsBaseDatosPruebas.EscalarAsync<string>(baseConDatos.Cadena,
            $"SELECT motivoRechazo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud WHERE id = '{camion}'")).Should().Be("Ruta sin tarifa");
        var estadoInvalido = () => clsBaseDatosPruebas.EjecutarAsync(baseConDatos.Cadena,
            $"UPDATE dbo_cotifly.CotizacionVehiculosLineaSolicitud SET estadoCosteo = 'rechazado' WHERE id = '{camion}'");
        (await estadoInvalido.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_estadoCosteo");
    }

    [Fact]
    public async Task LosCheckDel04AcotanElEstadoLosIntentosYExigenAvisosEnJson()
    {
        var definiciones = await LeerAsync(
            "SELECT name + '=' + definition FROM sys.check_constraints WHERE name IN (N'ck_cotizacionLineasSolicitud_estadoCubicacion', "
            + "N'ck_cotizacionLineasSolicitud_avisosCubicacion', N'ck_cotizacionLineasSolicitud_cubicacionIntentos')",
            r => r.GetString(0));

        definiciones.Should().HaveCount(3);
        string.Join(" | ", definiciones).Should().Contain("'pendiente'").And.Contain("'fallida'").And.Contain("isjson").And.Contain(">=(0)");
    }

    [Fact]
    public async Task ElScript03SeAplicaSobreEl02EsIdempotenteYCorrigeElCheckDelMotivo()
    {
        // HU-023.3 (#158); desde HU-014 (#188) está en db/scripts/ y el backend mapea sus columnas. En una base aislada (01 + 02 + 03 + ...),
        // se vuelve a correr.
        const string Script03 = clsBaseDatosPruebas.Script03;
        await using var baseAislada = await BaseDatos.CrearBaseInstaladaAsync("dbScript03");

        await clsBaseDatosPruebas.AplicarScriptAsync(baseAislada.Cadena, Script03);
        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, Script03);

        (await clsBaseDatosPruebas.EscalarAsync<string>(baseAislada.Cadena,
            "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'"))
            .Should().Contain("isnull", "un motivo con resultadoCubicacion NULL ya no pasa el CHECK");
        (await clsBaseDatosPruebas.EscalarAsync<int>(baseAislada.Cadena,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo' AND has_filter = 1"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ElScript0009EsIdempotenteSobreUnaBaseMigrada()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0009", clsBaseDatosPruebas.Scripts.Length);

        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, "0009_reestructuracion_cotizaciones.sql");
        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, "0010_nombres_municipio.sql");
    }

    [Fact]
    public async Task El0009MigraLasCotizacionesViejasARutasYCamionesYEl0010RecuperaLosNombres()
    {
        // HU-021 (#131): una base con el esquema viejo (0001 a 0008) y datos de prueba pasa por el 0009 y el 0010.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbMigra0009", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid formulario = Guid.NewGuid(), chat = Guid.NewGuid();
        var conexos = """[{"id":"CX-1","nombre":"Cargue","categoria":"logistico","cantidad":2,"costo":100000,"precio":117647,"incluido":true},{"id":"CX-2","nombre":"Seguro","categoria":"adicional","cantidad":1,"costo":5000,"precio":5882,"incluido":false}]""";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, tipoSolicitud, costeoId, estadoCosteo, costo, margenAplicado, origenMargen, metodoCalculo, "
            + "margenPolitica, margenSugerido, origenMargenSugerido, precioFinal, serviciosConexos, horasCargue) VALUES "
            + $"('{formulario}', 'PCN-MIG-1', 3, 'spot', 'SOL-77', 'autocosteo', 1000000, 0.15, 'politica_linea', 'margen', 0.15, 0.15, 'politica_linea', 1176471, N'{conexos}', 4); "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costeoId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, origen, destino) VALUES "
            + $"('{chat}', 'PCN-MIG-2', 1, 'TARIFA-9', 2000000, 0.15, 'politica_linea', 'margen', 2352941, N'Cali', N'Yumbo'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId, destinoMunicipioId, tipoVehiculo, pesoKg, "
            + "ciudadDevolucionContenedor, valorAprovisionamiento, estadoCosteo, lineaSolicitudNexoId, costo, precioFinal) VALUES "
            + $"('{formulario}', 1, N'Buenaventura', N'Bogotá', 76109, 11001, '3S3', 9000, N'Buenaventura', 150000, 'autocosteo', 'L-1', 1000000, 1176471); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, lineaSolicitudId, orden, descripcion, cantidad, largoCm, anchoCm, altoCm, pesoKg) "
            + $"SELECT '{formulario}', id, 1, N'Café', 20, 120, 100, 150, 450 FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{formulario}';");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0009_reestructuracion_cotizaciones.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0010_nombres_municipio.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(l.orden, '|', l.lineaNegocioId, '|', l.origenNombre, '|', l.destinoNombre, '|', l.ciudadDevolucionNombre, '|', l.margenAplicado, '|', "
            + "l.costoFletesTotal, '|', l.costoLevanteContenedorTotal, '|', l.costoServiciosConexos, '|', l.costoFinal, '|', l.precioFinalLinea, '|', l.horasCargue, '|', "
            + "v.tipoVehiculo, '|', v.costoFlete, '|', v.costoLevanteContenedor, '|', v.lineaSolicitudNexoId, '|', c.solicitudNexoId) "
            + "FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id "
            + $"JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id WHERE c.id = '{formulario}'"))
            .Should().Be("1|3|Buenaventura|Bogotá|Buenaventura|0.1500|850000.00|150000.00|100000.00|1100000.00|1176471.00|4.00|3S3|850000.00|150000.00|L-1|SOL-77",
                "el costo viejo se reparte en flete y levante, el conexo incluido pasa a la ruta y el 0010 recupera nombres e id de la solicitud");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(COUNT(*), '|', MAX(s.nombreServicioConexo), '|', MAX(s.precio), '|', MAX(s.cantidad)) FROM dbo_cotifly.CotizacionServiciosConexos s "
            + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId WHERE l.cotizacionSolicitudId = '{formulario}'"))
            .Should().Be("1|Cargue|50000.00|2.00", "solo el incluido; precio unitario = costo ÷ cantidad");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(l.orden, '|', l.origenNombre, '|', l.destinoNombre, '|', v.costoFlete, '|', ISNULL(c.solicitudNexoId, '-')) FROM dbo_cotifly.CotizacionesSolicitud c "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id "
            + $"JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id WHERE c.id = '{chat}'"))
            .Should().Be("1|Cali|Yumbo|2000000.00|-", "la cotización del chat recibe su ruta y su camión, y el 0010 le devuelve la ubicación (hueco 1 del 0009)");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(m.nombreMercancia, '|', m.orden) FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"WHERE l.cotizacionSolicitudId = '{formulario}'"))
            .Should().Be("Café|1");
    }

    [Fact]
    public async Task El0009ConservaAjustesCorreosYAuditoriaYRespaldaLaCotizacionDeItrQueBorra()
    {
        // HU-021.12 (#139): migración de datos de las tablas que cuelgan de la cotización (MargenAjustes, Correos, CorreoDestinatarios) y de
        // la auditoría; la cotización de prueba de ITR se borra con todo lo suyo y queda en los Respaldo0009_*.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbMigraDatos0009", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid usuario = Guid.NewGuid(), cotizacion = Guid.NewGuid(), itr = Guid.NewGuid(), correo = Guid.NewGuid(), correoItr = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario) VALUES ('{usuario}', N'mig0009'); "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costeoId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, origen, destino, usuarioId, estadoEnvio) "
            + $"SELECT '{cotizacion}', 'PCN-MIG-AJ', id, 'SOL-1', 1000000, 0.20, 'negociado', 'margen', 1250000, N'Cali', N'Yumbo', '{usuario}', 'enviada' "
            + "FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = 'Importacion'; "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, usuarioId) "
            + $"SELECT '{itr}', 'PCN-MIG-ITR', id, 500000, 0.15, 'politica_linea', 'margen', 588235, '{usuario}' FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = 'ITR'; "
            + "INSERT INTO dbo_cotifly.MargenAjustes (cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId) VALUES "
            + $"('{cotizacion}', 'PCN-MIG-AJ', 0.15, 0.20, 0.05, N'Cliente estratégico', '{usuario}'), ('{itr}', 'PCN-MIG-ITR', 0.15, 0.10, -0.05, N'ITR', '{usuario}'); "
            + "INSERT INTO dbo_cotifly.Correos (id, cotizacionId, asunto, estado, confirmadoPor) VALUES "
            + $"('{correo}', '{cotizacion}', N'Cotización PCN-MIG-AJ', 'enviado', '{usuario}'), ('{correoItr}', '{itr}', N'ITR', 'enviado', '{usuario}'); "
            + $"INSERT INTO dbo_cotifly.CorreoDestinatarios (correoId, email, tipo) VALUES ('{correo}', N'compras@cliente.com', 'para'), ('{correo}', N'jefe@cliente.com', 'cc'), "
            + $"('{correoItr}', N'itr@cliente.com', 'para'); "
            + "INSERT INTO dbo_cotifly.EventosHerramienta (herramienta, input, exito) VALUES (N'enviar_correo', N'{\"folio\":\"PCN-MIG-AJ\"}', 1);");
        var eventosAntes = await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0009_reestructuracion_cotizaciones.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0010_nombres_municipio.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(a.folio, '|', a.margenAplicado, '|', a.diferencia, '|', a.motivo, '|', l.orden, '|', m.lineaNegocio) FROM dbo_cotifly.MargenAjustes a "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = a.lineaSolicitudId JOIN dbo_cotifly.MargenesPolitica m ON m.id = l.lineaNegocioId "
            + $"WHERE a.cotizacionId = '{cotizacion}'"))
            .Should().Be("PCN-MIG-AJ|0.2000|0.0500|Cliente estratégico|1|Importacion", "el ajuste queda en la ruta 1, que hereda la línea de negocio de la cabecera");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(c.estado, '|', c.confirmadoPor, '|', (SELECT STRING_AGG(CONCAT(d.tipo, '=', d.email), ',') WITHIN GROUP (ORDER BY d.tipo DESC) "
            + $"FROM dbo_cotifly.CorreoDestinatarios d WHERE d.correoId = c.id)) FROM dbo_cotifly.Correos c WHERE c.cotizacionId = '{cotizacion}'"))
            .Should().Be($"enviado|{usuario.ToString().ToUpperInvariant()}|para=compras@cliente.com,cc=jefe@cliente.com");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT STRING_AGG(OBJECT_NAME(fk.parent_object_id), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(fk.parent_object_id)) FROM sys.foreign_keys fk "
            + "WHERE fk.referenced_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud')"))
            .Should().Be("Correos,CotizacionArchivosLicitacion,CotizacionLineasSolicitud,CotizacionPdfsHistorial,MargenAjustes",
                "las FK de las tablas hijas siguen a la tabla renombrada");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(e.estadoEnvio, '|', e.precioFinal) FROM dbo_cotifly.CotizacionesSolicitud e WHERE e.folio = 'PCN-MIG-AJ'")).Should().Be("enviada|1250000.00");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT((SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = 'PCN-MIG-ITR'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Correos WHERE id = '{correoItr}'), '|', (SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = 'PCN-MIG-ITR'), '|', "
            + "(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_Cotizaciones WHERE folio = 'PCN-MIG-ITR'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_Correos WHERE id = '{correoItr}'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_CorreoDestinatarios WHERE correoId = '{correoItr}'), '|', "
            + "(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_MargenAjustes WHERE folio = 'PCN-MIG-ITR'))"))
            .Should().Be("0|0|0|1|1|1|1", "la cotización de ITR se borra con lo suyo y queda respaldada");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta")).Should().Be(eventosAntes, "la auditoría no se toca");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(lineaNegocio, '|', numAjustes) FROM dbo_cotifly.vAjustesMargenPorComercial WHERE usuarioId = '{usuario}'"))
            .Should().Be("Importacion|1", "la vista adaptada lee la línea de negocio de la ruta del ajuste");
    }

    // Idempotencia (#91): cada prueba re-aplica el script en una base AISLADA (nombre único, 0001 a 0005 aplicados) y no en la
    // compartida. En la compartida, otras pruebas de la colección dejan cotizaciones del formulario ampliado sin línea (columnas de
    // línea en Cotizaciones) y el backfill del 0005 les agrega su línea legítimamente al re-aplicarlo: el conteo global "crecía" sin
    // que el script duplicara nada. En la base aislada nadie más escribe, así que se compara el esquema y el conteo de filas de
    // CADA tabla, y además las filas propias de la prueba.
    [Fact]
    public async Task ElScript0002EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0002", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0002_identidad_estado_agente.sql");
    }

    [Fact]
    public async Task LaBaseAisladaSeBorraAlTerminarAunqueQuedenConexionesAbiertas()
    {
        var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBorrar", 1);
        await using var abierta = new SqlConnection(baseAislada.Cadena);
        await abierta.OpenAsync(Ct);

        await baseAislada.DisposeAsync();

        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaMaster, $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{baseAislada.Nombre}'"))
            .Should().Be(0, "la base auxiliar no debe quedar en el SQL Server compartido");
    }

    [Fact]
    public async Task ElScript0004EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0004", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0004_costeo_nexo.sql");
    }

    [Fact]
    public async Task ElScript0005EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0005", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid spot = Guid.NewGuid(), licitacion = Guid.NewGuid(), chat = Guid.NewGuid();
        var propias = $"'{spot}', '{licitacion}', '{chat}'";
        var marca = Guid.NewGuid().ToString("N")[..12];

        // Cotizaciones con el esquema anterior (línea en las columnas de Cotizaciones, sin CotizacionLineasSolicitud): las que el
        // backfill debe convertir. La del chat no cumple el criterio (sin tipoSolicitud/origen/destino) y no recibe línea.
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, origen, destino, tipoVehiculo, pesoKg, estadoCosteo, costo, precioFinal) VALUES "
            + $"('{spot}', 'PCN-ID-{marca}-1', 'spot', 'YUMBO', 'BOGOTA, D.C.', '3S3', 9000, 'autocosteo', 1000000, 1176471), "
            + $"('{licitacion}', 'PCN-ID-{marca}-2', 'licitacion', 'CALI', 'CARTAGENA', NULL, 500, 'pendiente_costeo', NULL, NULL); "
            + $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, costo, precioFinal) VALUES ('{chat}', 'PCN-ID-{marca}-3', 2000000, 2352941); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm, pesoKg) VALUES "
            + $"('{spot}', 1, 20, 120, 100, 150, 450), ('{spot}', 2, 5, 80, 60, 40, 30), ('{licitacion}', 1, 1, 100, 100, 100, 500);");

        // Primera aplicación: hace el backfill de las filas propias.
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");
        var lineasPropias = $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId IN ({propias})";
        var posicionesPropias = $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones WHERE cotizacionId IN ({propias})";
        var posicionesEnlazadas =
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.CotizacionLineasSolicitud l "
            + $"ON l.id = p.lineaSolicitudId AND l.cotizacionId = p.cotizacionId AND l.orden = 1 WHERE p.cotizacionId IN ({propias})";
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, lineasPropias)).Should().Be(2, "una línea por cotización del formulario; el chat no tiene");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesEnlazadas)).Should().Be(3, "las tres posiciones quedan en la línea 1 de su cotización");
        var huellaPropia = await HuellaFilasPropiasAsync(cadena, propias);

        // Segunda aplicación: esquema, filas por tabla y filas propias (con sus enlaces) quedan iguales.
        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0005_lineas_solicitud.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, lineasPropias)).Should().Be(2, "re-aplicar el 0005 no duplica el backfill");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesPropias)).Should().Be(3, "re-aplicar el 0005 no agrega ni quita posiciones");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesEnlazadas)).Should().Be(3, "re-aplicar no desenlaza las posiciones");
        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huellaPropia, "re-aplicar el 0005 no modifica las líneas ni los enlaces ya creados");
    }

    [Fact]
    public async Task ElScript0005NoBorraLasColumnasDeLineaDeCotizacionesYEnlazaLasPosiciones()
    {
        await using var viejo = await EsquemaViejoAsync();
        var columnas = await LeerColumnasAsync(viejo.Cadena);

        foreach (var obsoleta in new[] { "origen", "destino", "tipoVehiculo", "pesoKg", "valorMercancia" })
        {
            columnas.Should().ContainKey(("Cotizaciones", obsoleta), "el 0005 no es destructivo: las columnas de línea quedan obsoletas, no se borran");
        }

        columnas[("CotizacionPosiciones", "lineaSolicitudId")].Nula.Should().BeTrue("las posiciones anteriores siguen válidas");
        columnas[("CotizacionLineasSolicitud", "origen")].Nula.Should().BeFalse();
        columnas[("CotizacionArchivosLicitacion", "contenido")].Tipo.Should().Be("varbinary(max)");
    }

    [Fact]
    public async Task LosCheckDel0005RechazanOrdenEstadoMontosYArchivoInvalidos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");
        string Linea(string columnas, string valores) =>
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, origen, destino, {columnas}) VALUES ('{id}', 'A', 'B', {valores})";

        var orden = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden", "0"));
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, estadoCosteo", "1, 'costeado'"));
        var montos = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, precioFinal", "1, -1"));
        var fila = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, filaOrigen", "1, -1"));
        var archivo = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionArchivosLicitacion (cotizacionId, nombreArchivo, tipoContenido, tamanoBytes, sha256, hoja, contenido) VALUES ('{id}', 'a.xlsx', 'x', 5, 'h', 'Hoja', 0x01)");

        (await orden.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_orden");
        (await estado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_estadoCosteo");
        (await montos.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_montos");
        (await fila.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_filaOrigen");
        (await archivo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionArchivosLicitacion_tamano");
    }

    [Fact]
    public async Task ElScript0006EsIdempotenteYNoTocaLosDatos()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0006", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, pdfPath) VALUES ('{id}', '{FolioUnico()}', 'PCN-X.pdf')");
        var huella = $"SELECT CONCAT(versionNegocio, '|', pdfDesactualizado, '|', pdfPath, '|', ISNULL(origenMargenSugerido, '-')) FROM dbo_cotifly.Cotizaciones WHERE id = '{id}'";
        var antes = await clsBaseDatosPruebas.EscalarAsync<string>(cadena, huella);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0006_textos_y_concurrencia.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, huella)).Should().Be(antes, "re-aplicar el 0006 no cambia las filas");
        antes.Should().Be("0|0|PCN-X.pdf|-", "una fila existente queda en la versión 0 y con su PDF vigente");
    }

    [Fact]
    public async Task ElBackfillDel0006CopiaElMargenSugeridoQueHaya()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBackfill0006", 5);
        var cadena = baseAislada.Cadena;
        Guid sap = Guid.NewGuid(), negociada = Guid.NewGuid(), chat = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, margenPolitica, margenAplicado, origenMargen) VALUES "
            + $"('{sap}', '{FolioUnico()}', 0.15, 0.21, 'sap_tm'), ('{negociada}', '{FolioUnico()}', 0.15, 0.12, 'negociado'), "
            + $"('{chat}', '{FolioUnico()}', NULL, NULL, NULL)");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");

        string Fila(Guid id) => $"SELECT CONCAT(ISNULL(CAST(margenSugerido AS nvarchar(20)), '-'), '|', ISNULL(origenMargenSugerido, '-'), '|', margenPolitica) FROM dbo_cotifly.Cotizaciones WHERE id = '{id}'";
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(sap))).Should().Be("0.2100|sap_tm|0.1500");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(negociada))).Should().Be("0.1500|politica_linea|0.1500", "negociada: el sugerido era la política");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(chat))).Should().Be("-|-|");
    }

    [Fact]
    public async Task LaReversaDel0006DejaElEsquemaDel0005YSePuedeVolverAAplicar()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbReversa0006", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var con0006 = await FotoEsquemaAsync(cadena);

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "rollback/0006_textos_y_concurrencia_reversa.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "rollback/0006_textos_y_concurrencia_reversa.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones') AND name IN "
                + "(N'versionNegocio', N'textosCotizacion', N'pdfDesactualizado', N'margenSugerido', N'origenMargenSugerido')"))
            .Should().Be(0);
        (await clsBaseDatosPruebas.EscalarAsync<int?>(cadena, "SELECT OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial', N'U')")).Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<int?>(cadena, "SELECT COL_LENGTH(N'dbo_cotifly.vAjustesMargenPorComercial', N'numAjustesBajoSugerido')")).Should().BeNull();

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");
        (await FotoEsquemaAsync(cadena)).Should().Equal(con0006, "re-aplicar el 0006 después de la reversa deja el mismo esquema");
    }

    [Fact]
    public async Task LosCheckDel0006RechazanTextosVersionOrigenYMotivosInvalidos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");
        Func<Task> Sql(string sql) => () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, sql);

        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET textosCotizacion = N'no es json' WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_textosCotizacion_json");
        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET versionNegocio = -1 WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_versionNegocio");
        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET origenMargenSugerido = 'negociado' WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_origenMargenSugerido");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionPdfsHistorial (cotizacionId, pdfPath, motivo) VALUES ('{id}', 'PCN-X.pdf', 'otro')").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionPdfsHistorial_motivo");
        await Sql($"INSERT INTO dbo_cotifly.CotizacionPdfsHistorial (cotizacionId, pdfPath, motivo) VALUES ('{id}', 'PCN-X.pdf', 're_solicitud')")();
    }

    [Fact]
    public async Task ElScript0007EsIdempotenteYAgregaNotasYMunicipios()
    {
        // Mismo patrón que #91: base AISLADA, filas propias antes de re-aplicar y verificación de que siguen iguales.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0007", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        var propias = $"'{id}'";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, notas) VALUES ('{id}', 'PCN-ID7-{Guid.NewGuid().ToString("N")[..12]}', 'spot', N'Cita previa'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId, destinoMunicipioId) "
            + $"VALUES ('{id}', 1, 'YUMBO', 'BOGOTA', 76892, 11001);");
        var huella = await HuellaFilasPropiasAsync(cadena, propias);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0007_captura_nexo_campos.sql");

        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huella, "re-aplicar el 0007 no cambia las filas existentes");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(c.notas, '|', l.origenMunicipioId, '|', l.destinoMunicipioId) FROM dbo_cotifly.Cotizaciones c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionId = c.id WHERE c.id = '{id}'"))
            .Should().Be("Cita previa|76892|11001");
        var columnas = await LeerColumnasAsync(cadena);
        columnas[("Cotizaciones", "notas")].Should().Be(("nvarchar(2000)", true));
        columnas[("CotizacionLineasSolicitud", "origenMunicipioId")].Should().Be(("int", true));
        columnas[("CotizacionLineasSolicitud", "destinoMunicipioId")].Should().Be(("int", true));
    }

    [Fact]
    public async Task ElScript0008EsIdempotenteYAgregaLasHorasPorLinea()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0008", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        var propias = $"'{id}'";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud) VALUES ('{id}', 'PCN-ID8-{Guid.NewGuid().ToString("N")[..12]}', 'licitacion'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, horasCargue, horasDescargue) "
            + $"VALUES ('{id}', 1, 'YUMBO', 'BOGOTA', 4.5, 240);");
        var huella = await HuellaFilasPropiasAsync(cadena, propias);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0008_horas_por_linea.sql");

        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huella, "re-aplicar el 0008 no cambia las filas existentes");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(horasCargue, '|', horasDescargue) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{id}'"))
            .Should().Be("4.50|240.00");
        var columnas = await LeerColumnasAsync(cadena);
        columnas[("CotizacionLineasSolicitud", "horasCargue")].Should().Be(("decimal(6,2)", true));
        columnas[("CotizacionLineasSolicitud", "horasDescargue")].Should().Be(("decimal(6,2)", true));
    }

    [Fact]
    public async Task ElCheckDel0008RechazaHorasFueraDelRangoDeNexo()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var excedida = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, horasDescargue) VALUES ('{id}', 1, 'A', 'B', 240.01)");

        (await excedida.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_horas");
    }

    [Fact]
    public async Task ElCheckDel0007RechazaIdsDeMunicipioNoPositivos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var cero = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId) VALUES ('{id}', 1, 'A', 'B', 0)");

        (await cero.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_municipios");
    }

    [Fact]
    public async Task ElBackfillDel0005CreaUnaLineaPorCotizacionDelFormularioYEnlazaSusPosiciones()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBackfill0005", 4);
        var cadena = baseAislada.Cadena;
        Guid conCosteo = Guid.NewGuid(), pendiente = Guid.NewGuid(), chat = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, origen, destino, tipoVehiculo, pesoKg, estadoCosteo, costo, precioFinal) VALUES "
            + $"('{conCosteo}', 'PCN-BF-01', 'spot', 'YUMBO', 'BOGOTA, D.C.', '3S3', 9000, 'autocosteo', 1000000, 1176471), "
            + $"('{pendiente}', 'PCN-BF-02', 'licitacion', 'CALI', 'CARTAGENA', NULL, 500, 'pendiente_costeo', NULL, NULL); "
            + $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, costo, precioFinal) VALUES ('{chat}', 'PCN-BF-03', 2000000, 2352941); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm, pesoKg) "
            + $"VALUES ('{conCosteo}', 1, 20, 120, 100, 150, 450);");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud"))
            .Should().Be(2, "una línea por cotización del formulario; el chat no tiene línea y re-aplicar no duplica");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{conCosteo}' AND orden = 1 AND origen = 'YUMBO' AND tipoVehiculo = '3S3' AND pesoKg = 9000 AND estadoCosteo = 'autocosteo' AND costo = 1000000 AND precioFinal = 1176471"))
            .Should().Be(1);
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{pendiente}' AND estadoCosteo = 'pendiente_costeo' AND costo IS NULL AND precioFinal IS NULL"))
            .Should().Be(1, "sin costeo completo la línea no hereda costo ni precio");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = p.lineaSolicitudId AND l.cotizacionId = p.cotizacionId"))
            .Should().Be(1, "la posición queda enlazada a la línea 1 de su cotización");
    }

    [Fact]
    public async Task ElScript0004AgregaVeintinueveColumnasNullablesACotizacionesSalvoLasConsultasFallidas()
    {
        await using var viejo = await EsquemaViejoAsync();
        var columnas = await LeerColumnasAsync(viejo.Cadena);
        string[] nuevas =
        [
            "tipoSolicitud", "deadline", "valorEstimado", "referencia", "horasCargue", "horasDescargue", "origen", "destino",
            "tipoVehiculo", "carroceria", "tipologia", "claseViaje", "claseContenedor", "exclusivo", "consolidado", "retornaContenedor",
            "ciudadDevolucionContenedor", "levanteContenedorDiferente", "ciudadLevanteContenedor", "valorAprovisionamiento", "pesoKg",
            "valorMercancia", "cotizacionNexoId", "motivoEstadoCosteo", "costeoEnviadoAt", "costeoUltimaConsultaAt",
            "costeoSiguienteConsultaAt", "costeoConsultasFallidas", "serviciosConexos",
        ];

        nuevas.Where(c => !columnas.ContainsKey(("Cotizaciones", c))).Should().BeEmpty();
        nuevas.Where(c => c != "costeoConsultasFallidas" && !columnas[("Cotizaciones", c)].Nula).Should().BeEmpty(
            "las cotizaciones existentes (chat con costo manual) no traen estos datos");
        columnas[("Cotizaciones", "costeoConsultasFallidas")].Nula.Should().BeFalse();
        columnas.Keys.Count(k => k.Tabla == "Cotizaciones").Should().Be(28 + nuevas.Length + 5 + 1,
            "0001 crea 28 columnas, el 0004 agrega 29, el 0006 cinco (versionNegocio, textosCotizacion, pdfDesactualizado, margenSugerido y origenMargenSugerido) y el 0007 agrega notas");
    }

    [Fact]
    public async Task LosCheckDel0004RechazanTipoDeSolicitudJsonYMedidasInvalidas()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var tipo = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET tipoSolicitud = 'urgente' WHERE id = '{id}'");
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoCosteo = 'costeado' WHERE id = '{id}'");
        var json = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET serviciosConexos = 'no es json' WHERE id = '{id}'");
        var cantidad = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm) VALUES ('{id}', 1, 0, 10, 10, 10)");
        var medidas = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm) VALUES ('{id}', 1, 1, 0, 10, 10)");

        (await tipo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_tipoSolicitud");
        (await estado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_estadoCosteo");
        (await json.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_serviciosConexos_json");
        (await cantidad.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionPosiciones_cantidad");
        (await medidas.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionPosiciones_medidas");
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
                    // EF guarda "todas descendentes" como lista vacía (HU-042.2: idx_sapMargenSyncLog_iniciado, una columna DESC).
                    p.GetColumnName(objeto) + (i.IsDescending is { } d && (d.Count == 0 || d[n]) ? " DESC" : string.Empty));
                var incluidas = (i.GetIncludeProperties() ?? [])
                    .Select(nombre => e.FindProperty(nombre)!.GetColumnName(objeto)!)
                    .Order(StringComparer.Ordinal);
                return Indice(tabla, i.GetDatabaseName()!, i.IsUnique, string.Join(',', claves), string.Join(',', incluidas), i.GetFilter());
            });
        });

        ef.Should().BeEquivalentTo(bd);
        bd.Where(x => x.Contains(":ix_", StringComparison.Ordinal)).Should().HaveCount(18,
            "el 0002 crea 10 índices ix_, el 0004 uno, el 0006 uno (historial de PDF), el 0009 quita el de posiciones por línea y agrega el de camiones y el de ajustes por ruta, la propuesta 04 el de la cola del cubicador, el script 06 (#191) el de vencimiento y el filtrado por folio de las Idempotency-Key y el 13 (#199) la agenda del worker de correos");
        bd.Should().Contain("Correos:ix_correos_siguienteConsultaAt unico=False claves=(siguienteConsultaAt) include=() filtro=siguienteconsultaatisnotnull");
        bd.Should().Contain(x => x.StartsWith("Correos:ux_correos_idMailSender unico=True claves=(idMailSender)", StringComparison.Ordinal)
            && x.EndsWith("filtro=idmailsenderisnotnull", StringComparison.Ordinal));
        bd.Should().Contain("CapturasIdempotentes:ix_capturasIdempotentes_folio unico=False claves=(folio) include=(expiraAt,scorePrioridad,solicitudNexoId) filtro=folioisnotnull");
        bd.Should().Contain(x => x.StartsWith("CotizacionLineasSolicitud:ix_cotizacionLineasSolicitud_cubicacionPendiente unico=False", StringComparison.Ordinal)
            && x.Contains("claves=(cubicacionDisponibleAt,createdAt)", StringComparison.Ordinal)
            && x.EndsWith("filtro=estadocubicacion=n'pendiente'", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("CotizacionesSolicitud:ix_cotizaciones_costeoSiguienteConsultaAt unico=False", StringComparison.Ordinal)
            && x.EndsWith("filtro=costeosiguienteconsultaatisnotnull", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("UsuariosWeb:uq_usuariosWeb_nombreUsuario", StringComparison.Ordinal) && x.EndsWith("filtro=nombreusuarioisnotnull", StringComparison.Ordinal));
        // HU-040.1 (#229): el 16 retira la unicidad de la asignación activa (multi-rol) y conserva la de usuario + rol.
        bd.Should().NotContain(x => x.Contains("uq_usuariosRoles_usuarioId_activo", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("UsuariosRoles:uq_usuariosRoles_usuarioId_rolId unico=True claves=(usuarioId,rolId)", StringComparison.Ordinal));
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
            // HU-042.2 (#243): las tablas del 17 (de Datos) no fijan COLLATE: heredan la de la base y el modelo no les asigna ninguna.
            if (clsCotiflyDbContext.TablasConCollationDeLaBase.Contains(tabla))
            {
                return e.GetProperties().Where(p => p.GetCollation() is not null).Select(p => $"{tabla}.{p.GetColumnName(objeto)}: EF fija collation '{p.GetCollation()}'");
            }

            return e.GetProperties()
                .Where(p => porColumna.ContainsKey((tabla, p.GetColumnName(objeto)!)) || p.GetCollation() is not null)
                .Select(p => (Columna: $"{tabla}.{p.GetColumnName(objeto)}", Ef: p.GetCollation(),
                    Bd: porColumna.GetValueOrDefault((tabla, p.GetColumnName(objeto)!))))
                .Where(x => x.Ef != x.Bd)
                .Select(x => $"{x.Columna}: EF '{x.Ef}' vs base '{x.Bd}'");
        });

        diferencias.Should().BeEmpty();
        porColumna[("TokensRefresco", "tokenHash")].Should().Be(clsCotiflyDbContext.CollationBinaria);
        porColumna[("CotizacionesSolicitud", "folio")].Should().Be(clsCotiflyDbContext.Collation);
    }

    [Fact]
    public async Task ElSeedDel0001ConElCambioDeCatalogoDel0009DejaLasSeisLineasDeNexo()
    {
        // HU-021 §10: el 0009 retira Almacenamiento, Almacenamiento3PL e ITR (sin equivalente en Nexo) y agrega DevolucionContenedor.
        // Base propia con todas las migraciones: la compartida recibe en paralelo líneas y mapeos temporales de otras pruebas
        // (p. ej. clsMargenRepositoryTests), y aquí se verifica el catálogo exacto que deja el seed (#147, CI inestable).
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbSeedLineas", clsBaseDatosPruebas.Scripts.Length);
        var lineas = await LeerAsync("SELECT lineaNegocio FROM dbo_cotifly.MargenesPolitica", r => r.GetString(0), baseAislada.Cadena);
        var mapeos = await clsBaseDatosPruebas.EscalarAsync<int>(baseAislada.Cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType WHERE salesOrg = ''");

        lineas.Should().BeEquivalentTo(["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "DevolucionContenedor"]);
        mapeos.Should().Be(5, "los mapeos de las tres líneas retiradas se borran; el de DevolucionContenedor queda pendiente del código de SAP");
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
            await c.CotizacionesSolicitud.Include(x => x.Rutas).ThenInclude(r => r.Vehiculos).Take(1).ToListAsync(Ct);
            await c.CotizacionLineasSolicitud.Take(1).ToListAsync(Ct);
            await c.CotizacionVehiculosLineaSolicitud.Take(1).ToListAsync(Ct);
            await c.CotizacionMercancias.Take(1).ToListAsync(Ct);
            await c.CotizacionServiciosConexos.Take(1).ToListAsync(Ct);
            await c.ArchivosLicitacion.Take(1).ToListAsync(Ct);
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
            await c.SapMargenCliente.Take(1).ToListAsync(Ct);
            await c.SapMargenSyncLog.Take(1).ToListAsync(Ct);
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
        // El 0002 se re-aplica sobre el esquema en que nació (antes de HU-021: crea un índice en la tabla Cotizaciones).
        await using var viejo = await EsquemaViejoAsync();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoComercial} WHERE codigo = 'comercial'");
        await clsBaseDatosPruebas.AplicarScriptAsync(viejo.Cadena, "0002_identidad_estado_agente.sql");

        var comercial = await clsBaseDatosPruebas.EscalarAsync<int>(viejo.Cadena,
            "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'");
        var filas = await clsBaseDatosPruebas.EscalarAsync<int>(viejo.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.Roles");

        comercial.Should().Be(clsBaseDatosPruebas.RolExternoComercial);
        filas.Should().Be(2);
    }

    [Fact]
    public async Task LosCheckDel0009RechazanEstadosMontosYCantidadesInvalidosEnRutasCamionesYConexos()
    {
        var cotizacion = Guid.NewGuid();
        var ruta = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio) VALUES ('{cotizacion}', '{FolioUnico()}'); "
            + $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (id, cotizacionSolicitudId, orden) VALUES ('{ruta}', '{cotizacion}', 1);");
        Func<Task> Sql(string sql) => () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, sql);

        (await Sql($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = -1 WHERE id = '{ruta}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_costos");
        (await Sql($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET origenMargen = 'otro' WHERE id = '{ruta}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_origenMargen");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, estadoCosteo) VALUES ('{ruta}', 'costeado')").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_estadoCosteo");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, costoFlete) VALUES ('{ruta}', -1)").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_montos");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, precio, cantidad, precioVenta) VALUES ('{ruta}', 'CX', 1, 0, 1)").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionServiciosConexos_montos");
        (await Sql($"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoEnvio = 'perdida' WHERE id = '{cotizacion}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_estadoEnvio");
    }

    /// <summary>Base aislada con el esquema anterior a HU-021 (0001 a 0008), para las pruebas de esos scripts.</summary>
    private Task<clsBaseAuxiliarPruebas> EsquemaViejoAsync() =>
        BaseDatos.CrearBaseConScriptsAsync("dbViejo", clsBaseDatosPruebas.ScriptsAntesDeHu021);

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

    /// <summary>Índices y restricciones UNIQUE (no las PK) de dbo_cotifly (en la base compartida o en <paramref name="cadena"/>).</summary>
    private Task<List<string>> LeerIndicesAsync(string? cadena = null) =>
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
                r.IsDBNull(5) ? string.Empty : r.GetString(5), r.IsDBNull(3) ? null : r.GetString(3)),
            cadena);

    /// <summary>
    /// Descripción ordenada de todo el esquema (columnas con tipo/nulabilidad/collation/default,
    /// claves, FK, índices y CHECK), para comparar antes y después de re-aplicar un script.
    /// </summary>
    private async Task<List<string>> FotoEsquemaAsync(string cadena)
    {
        var columnas = await LeerAsync(
            """
            SELECT CONCAT(TABLE_NAME COLLATE DATABASE_DEFAULT, '.', COLUMN_NAME COLLATE DATABASE_DEFAULT, ' ', DATA_TYPE COLLATE DATABASE_DEFAULT,
                          '(', CHARACTER_MAXIMUM_LENGTH, ',', NUMERIC_PRECISION, ',', NUMERIC_SCALE, ') ', IS_NULLABLE COLLATE DATABASE_DEFAULT, ' ',
                          COLLATION_NAME COLLATE DATABASE_DEFAULT, ' ', COLUMN_DEFAULT COLLATE DATABASE_DEFAULT)
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo_cotifly'
            """,
            r => r.GetString(0),
            cadena);
        var restricciones = await LeerAsync(
            """
            SELECT CONCAT(OBJECT_NAME(o.parent_object_id) COLLATE DATABASE_DEFAULT, ':', o.name COLLATE DATABASE_DEFAULT, ' ',
                          o.type_desc COLLATE DATABASE_DEFAULT, ' ', OBJECT_DEFINITION(o.object_id) COLLATE DATABASE_DEFAULT)
            FROM sys.objects o
            WHERE SCHEMA_NAME(o.schema_id) = 'dbo_cotifly' AND o.type IN ('PK', 'UQ', 'F', 'C', 'D')
            """,
            r => r.GetString(0),
            cadena);
        var indices = await LeerIndicesAsync(cadena);

        return [.. columnas.Concat(restricciones).Concat(indices).Order(StringComparer.Ordinal)];
    }

    /// <summary>Vistas de <c>dbo_cotifly</c> en <paramref name="cadena"/>.</summary>
    private Task<List<string>> VistasAsync(string cadena) =>
        LeerAsync("SELECT name FROM sys.views WHERE SCHEMA_NAME(schema_id) = 'dbo_cotifly'", r => r.GetString(0), cadena);

    /// <summary>
    /// Re-aplica <paramref name="script"/> sobre una base aislada y verifica que no cambie el esquema ni el número de filas de
    /// ninguna tabla de dbo_cotifly (la base es de la prueba: nadie más escribe en ella).
    /// </summary>
    private async Task ReaplicarYVerificarIdempotenciaAsync(string cadena, string script)
    {
        var esquemaAntes = await FotoEsquemaAsync(cadena);
        var filasAntes = await FilasPorTablaAsync(cadena);

        var reaplicar = () => clsBaseDatosPruebas.AplicarScriptAsync(cadena, script);

        await reaplicar.Should().NotThrowAsync();
        (await FotoEsquemaAsync(cadena)).Should().Equal(esquemaAntes, $"re-aplicar el {script} no debe cambiar columnas, claves, índices, CHECK ni defaults");
        (await FilasPorTablaAsync(cadena)).Should().Equal(filasAntes, $"re-aplicar el {script} no debe agregar ni quitar filas en ninguna tabla");
    }

    /// <summary>HU-040.1 (#229): filas de UsuariosWeb, UsuariosRoles y TokensRefresco (sin rolSesion, que el 16 agrega), ordenadas.</summary>
    private Task<List<string>> HuellaIdentidadAsync(string cadena) =>
        LeerAsync(
            """
            SELECT CONCAT('U|', id, '|', nombreUsuario) COLLATE Latin1_General_100_BIN2 FROM dbo_cotifly.UsuariosWeb
            UNION ALL SELECT CONCAT('R|', id, '|', usuarioId, '|', rolId, '|', activo, '|', asignadoAt) COLLATE Latin1_General_100_BIN2 FROM dbo_cotifly.UsuariosRoles
            UNION ALL SELECT CONCAT('T|', id, '|', usuarioId, '|', tokenHash, '|', familiaId, '|', expiraAt, '|', revocadoAt) COLLATE Latin1_General_100_BIN2 FROM dbo_cotifly.TokensRefresco
            ORDER BY 1
            """,
            r => r.GetString(0),
            cadena);

    /// <summary>"Tabla=filas" de cada tabla de dbo_cotifly, ordenado (COUNT exacto, no la estimación de sys.partitions).</summary>
    private async Task<List<string>> FilasPorTablaAsync(string cadena)
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0),
            cadena);
        var conteo = string.Join(" UNION ALL ", tablas.Select(t => $"SELECT CONCAT('{t}', '=', COUNT_BIG(*)) FROM dbo_cotifly.[{t}]"));

        return [.. (await LeerAsync(conteo, r => r.GetString(0), cadena)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Huella de las líneas y posiciones de las cotizaciones <paramref name="propias"/>: ids, enlaces y datos copiados.</summary>
    private Task<List<string>> HuellaFilasPropiasAsync(string cadena, string propias) =>
        LeerAsync(
            $"""
            SELECT CONCAT('L|', l.cotizacionId, '|', l.id, '|', l.orden, '|', l.origen, '|', l.destino, '|', l.estadoCosteo, '|', l.costo, '|', l.precioFinal)
            FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionId IN ({propias})
            UNION ALL
            SELECT CONCAT('P|', p.cotizacionId, '|', p.id, '|', p.orden, '|', p.lineaSolicitudId)
            FROM dbo_cotifly.CotizacionPosiciones p WHERE p.cotizacionId IN ({propias})
            ORDER BY 1
            """,
            r => r.GetString(0),
            cadena);

    private async Task<Dictionary<(string Tabla, string Columna), (string Tipo, bool Nula)>> LeerColumnasAsync(string? cadena = null)
    {
        var filas = await LeerAsync(
            """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA = 'dbo_cotifly' AND t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_NAME NOT LIKE 'Respaldo0009[_]%'
            """,
            r =>
            {
                var tipo = r.GetString(2);
                int? largo = r.IsDBNull(3) ? null : r.GetInt32(3);
                var tipoCompleto = tipo switch
                {
                    "nvarchar" or "varchar" or "nchar" or "char" or "varbinary" => $"{tipo}({(largo == -1 ? "max" : largo)})",
                    "decimal" or "numeric" => $"{tipo}({r.GetByte(4)},{r.GetInt32(5)})",
                    _ => tipo,
                };
                return (Tabla: r.GetString(0), Columna: r.GetString(1), Tipo: tipoCompleto, Nula: r.GetString(6) == "YES");
            },
            cadena);

        return filas.ToDictionary(f => (f.Tabla, f.Columna), f => (f.Tipo, f.Nula));
    }

    private async Task<List<T>> LeerAsync<T>(string sql, Func<SqlDataReader, T> mapear, string? cadena = null)
    {
        await using var conexion = new SqlConnection(cadena ?? BaseDatos.CadenaCotifly);
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
