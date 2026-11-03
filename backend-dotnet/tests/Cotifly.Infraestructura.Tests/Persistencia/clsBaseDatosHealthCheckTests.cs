using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>Readiness sin base disponible (no requiere Docker).</summary>
public sealed class clsBaseDatosHealthCheckTests
{
    /// <summary>Puerto 1 en loopback: la conexión se rechaza de inmediato.</summary>
    public const string CadenaInalcanzable =
        "Server=tcp:127.0.0.1,1;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=NoImporta.123;Encrypt=False;Connect Timeout=2";

    [Fact]
    public async Task SinCadenaDeConexionEsUnhealthyYLoDice()
    {
        var resultado = await EvaluarAsync(null);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionSinCadena);
    }

    [Fact]
    public async Task ConUnaBaseInalcanzableEsUnhealthySinExponerLaCadena()
    {
        var resultado = await EvaluarAsync(CadenaInalcanzable);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionNoDisponible);
        resultado.Description.Should().NotContain("127.0.0.1").And.NotContain("Password");
    }

    [Fact]
    public async Task UnServidorQueNoRespondeNoCuelgaLaSondaMasDeSuTiempoMaximo()
    {
        // 10.255.255.1 no es enrutable: sin límite propio la conexión esperaría los 15 s por defecto.
        const string CadenaSinRespuesta =
            "Server=tcp:10.255.255.1,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=NoImporta.123;Encrypt=False;Connect Timeout=30";
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var resultado = await EvaluarAsync(CadenaSinRespuesta);

        cronometro.Stop();
        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionNoDisponible);
        cronometro.Elapsed.Should().BeLessThan(clsBaseDatosHealthCheck.TiempoMaximo + TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData("CotizacionesSolicitud.scorePrioridad", clsBaseDatosHealthCheck.DescripcionEsquemaSin09)]
    // #191: el 06 va entre el 05 y el 09.
    [InlineData("CapturasIdempotentes.id|CapturasIdempotentes.operacion", clsBaseDatosHealthCheck.DescripcionEsquemaSin06)]
    [InlineData("CotizacionesSolicitud.scorePrioridad|CapturasIdempotentes.resultado", clsBaseDatosHealthCheck.DescripcionEsquemaSin06)]
    [InlineData("CapturasIdempotentes.resultado|CotizacionServiciosConexos.categoriaServicioConexo", clsBaseDatosHealthCheck.DescripcionEsquemaSin05)]
    [InlineData("CotizacionesSolicitud.scorePrioridad|CotizacionLineasSolicitud.estadoCubicacion", clsBaseDatosHealthCheck.DescripcionEsquemaSin04)]
    [InlineData("CotizacionesSolicitud.scorePrioridad|CotizacionLineasSolicitud.condicionada", clsBaseDatosHealthCheck.DescripcionEsquemaSin02)]
    // HU-014 (#188): el 03 y el 05, en su lugar de la cadena (02 → 03 → 04 → 05 → 09).
    [InlineData("CotizacionVehiculosLineaSolicitud.ordenVehiculo", clsBaseDatosHealthCheck.DescripcionEsquemaSin03)]
    [InlineData("CotizacionServiciosConexos.idServicioConexoCatalogo|CotizacionServiciosConexos.categoriaServicioConexo", clsBaseDatosHealthCheck.DescripcionEsquemaSin03)]
    [InlineData("CotizacionServiciosConexos.categoriaServicioConexo", clsBaseDatosHealthCheck.DescripcionEsquemaSin05)]
    [InlineData("CotizacionServiciosConexos.categoriaServicioConexo|CotizacionLineasSolicitud.estadoCubicacion", clsBaseDatosHealthCheck.DescripcionEsquemaSin04)]
    [InlineData("CotizacionesSolicitud.scorePrioridad|CotizacionServiciosConexos.categoriaServicioConexo", clsBaseDatosHealthCheck.DescripcionEsquemaSin05)]
    [InlineData("CotizacionVehiculosLineaSolicitud.ordenVehiculo|CotizacionLineasSolicitud.condicionada", clsBaseDatosHealthCheck.DescripcionEsquemaSin02)]
    public void HU0181_SinEl09LaSondaPideEl09SoloCuandoNoFaltaUnScriptAnterior(string faltantes, string descripcion)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar(faltantes.Split('|'), cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(descripcion);
    }

    [Theory]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg", clsBaseDatosHealthCheck.DescripcionEsquemaSin11)]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg|CotizacionesSolicitud.scorePrioridad", clsBaseDatosHealthCheck.DescripcionEsquemaSin09)]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg|CotizacionVehiculosLineaSolicitud.motivoRechazo", clsBaseDatosHealthCheck.DescripcionEsquemaSin10)]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg|CapturasIdempotentes.resultado", clsBaseDatosHealthCheck.DescripcionEsquemaSin06)]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg|CotizacionLineasSolicitud.condicionada", clsBaseDatosHealthCheck.DescripcionEsquemaSin02)]
    [InlineData("CotizacionLineasSolicitud.pesoConTaraKg|Otra.columna", "esquema incompleto: falta CotizacionLineasSolicitud.pesoConTaraKg, Otra.columna")]
    public void HU0301_SinEl11LaSondaPideEl11SoloCuandoNoFaltaUnScriptAnterior(string faltantes, string descripcion)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar(faltantes.Split('|'), cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(descripcion);
    }

    [Theory]
    [InlineData("CotizacionesSolicitud.finalizadaAt|CotizacionesSolicitud.finalizadaPor", clsBaseDatosHealthCheck.DescripcionEsquemaSin14)]
    [InlineData("CotizacionesSolicitud.finalizadaPor", clsBaseDatosHealthCheck.DescripcionEsquemaSin14)]
    [InlineData("CotizacionesSolicitud.finalizadaAt|CotizacionesSolicitud.resultadoSincronizadoAt", clsBaseDatosHealthCheck.DescripcionEsquemaSin13)]
    [InlineData("CotizacionesSolicitud.finalizadaAt|CotizacionLineasSolicitud.tipoVehiculoElegido", clsBaseDatosHealthCheck.DescripcionEsquemaSin12)]
    [InlineData("CotizacionesSolicitud.finalizadaAt|Otra.columna", "esquema incompleto: falta CotizacionesSolicitud.finalizadaAt, Otra.columna")]
    public void HU036_SinEl14LaSondaPideEl14SoloCuandoNoFaltaUnScriptAnterior(string faltantes, string descripcion)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar(faltantes.Split('|'), cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(descripcion);
    }

    [Theory]
    [InlineData("TokensRefresco.rolSesion", clsBaseDatosHealthCheck.DescripcionEsquemaSin16)]
    [InlineData("TokensRefresco.rolSesion|CotizacionesSolicitud.finalizadaAt", clsBaseDatosHealthCheck.DescripcionEsquemaSin14)]
    [InlineData("TokensRefresco.rolSesion|TokensRefresco.familiaId", clsBaseDatosHealthCheck.DescripcionEsquemaIncompleto)]
    [InlineData("TokensRefresco.rolSesion|Otra.columna", "esquema incompleto: falta Otra.columna, TokensRefresco.rolSesion")]
    public void HU0401_SinEl16LaSondaPideEl16SoloCuandoNoFaltaUnScriptAnterior(string faltantes, string descripcion)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar(faltantes.Split('|'), cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(descripcion);
    }

    internal static async Task<HealthCheckResult> EvaluarAsync(string? cadena)
    {
        await using var proveedor = clsServiciosPruebas.Crear(cadena, new clsRelojFalso());
        await using var ambito = proveedor.CreateAsyncScope();
        var check = ActivatorUtilities.CreateInstance<clsBaseDatosHealthCheck>(ambito.ServiceProvider);
        return await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }
}

/// <summary>Readiness con el SQL Server 2019 de Testcontainers.</summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsBaseDatosHealthCheckIntegracionTests(clsBaseDatosPruebas baseDatos)
{
    [Fact]
    public async Task ConLaBaseDisponibleEsHealthy()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(baseDatos.CadenaCotifly);

        resultado.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ConUnaBaseQueSoloTieneEl0001EsUnhealthyPorEsquemaIncompleto()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(await baseDatos.CadenaSolo0001Async());

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0002");
    }

    // HU-021: la sonda se rehízo desde el modelo EF. Una base con el esquema anterior a la reestructuración (cualquiera de 0004 a
    // 0008) tiene la tabla Cotizaciones y no CotizacionesSolicitud: le falta el 0009.
    [Fact]
    public async Task ConUnaBaseSinEl0004EsUnhealthyPorqueLeFaltaLaReestructuracion()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(await baseDatos.CadenaSin0004Async());

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionEsquemaSin0009);
    }

    [Fact]
    public async Task ConElEsquemaAnteriorAHu021EsUnhealthyPorqueLeFaltaEl0009()
    {
        await using var viejo = await baseDatos.CrearBaseConScriptsAsync("dbSin0009", clsBaseDatosPruebas.ScriptsAntesDeHu021);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(viejo.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0009", "el modelo EF mapea CotizacionesSolicitud, las rutas y los camiones");
    }

    [Fact]
    public async Task ConEl0009PeroSinEl0010EsUnhealthyPorqueLeFaltaEl0010()
    {
        await using var sin0010 = await baseDatos.CrearBaseConScriptsAsync("dbSin0010", clsBaseDatosPruebas.ScriptsHasta0009);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin0010.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0010", "el modelo EF mapea los nombres de municipio, solicitudNexoId y el nombre del conexo");
    }

    [Fact]
    public async Task ConEl0010PeroSinEl02EsUnhealthyPorqueLeFaltaEl02()
    {
        // HU-023.3 (#158): el modelo EF mapea las columnas del cubicador de db/scripts/02_alter_cubicador.sql.
        await using var sin02 = await baseDatos.CrearBaseConScriptsAsync("dbSin02", clsBaseDatosPruebas.ScriptsHasta0010);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin02.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 02");
    }

    [Fact]
    public async Task HU014_ConEl02PeroSinEl03EsUnhealthyPorqueLeFaltaEl03()
    {
        // HU-014 (#188): el modelo EF mapea ordenVehiculo e idServicioConexoCatalogo de db/scripts/03.
        await using var sin03 = await baseDatos.CrearBaseConScriptsAsync("dbSin03", clsBaseDatosPruebas.ScriptsHasta02);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin03.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 03");
    }

    [Fact]
    public async Task ConEl03PeroSinEl04EsUnhealthyPorqueLeFaltaEl04()
    {
        // HU-023.5 (#160): el modelo EF mapea las columnas de la cubicación en segundo plano de la propuesta 04.
        await using var sin04 = await baseDatos.CrearBaseConScriptsAsync("dbSin04", clsBaseDatosPruebas.ScriptsHasta03);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin04.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 04");
    }

    [Fact]
    public async Task HU014_ConEl04PeroSinEl05EsUnhealthyPorqueLeFaltaEl05()
    {
        // HU-014 (#188): el modelo EF mapea CotizacionServiciosConexos.categoriaServicioConexo de db/scripts/05.
        await using var sin05 = await baseDatos.CrearBaseConScriptsAsync("dbSin05", clsBaseDatosPruebas.ScriptsHasta04);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin05.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 05");
    }

    [Fact]
    public async Task HU0301_ConEl10PeroSinEl11EsUnhealthyPorqueLeFaltaEl11()
    {
        // HU-030.1 (#184): el modelo EF mapea CotizacionLineasSolicitud.pesoConTaraKg de db/scripts/11_peso_con_tara.sql. Integración 1021:
        // la base llega hasta el 10 (Scripts[ScriptsSin07 - 6], #183); hasta el 09 la sonda pide primero el 10.
        await using var sin11 = await baseDatos.CrearBaseConScriptsAsync("dbSin11", clsBaseDatosPruebas.ScriptsSin07 - 6);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin11.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 11");
    }

    [Fact]
    public async Task HU032_ConEl11PeroSinEl12EsUnhealthyPorqueLeFaltaEl12()
    {
        // HU-032 (#197): el modelo EF mapea tipoVehiculoElegido y carroceriaElegida de la ruta (db/scripts/12_vehiculo_elegido.sql).
        await using var sin12 = await baseDatos.CrearBaseConScriptsAsync("dbSin12", clsBaseDatosPruebas.ScriptsSin07 - 5);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin12.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 12");
    }

    [Fact]
    public async Task HU033_ConEl12PeroSinEl13EsUnhealthyPorqueLeFaltaEl13()
    {
        // HU-033 (#199): el modelo EF mapea el seguimiento de MailSender (Correos) y el resultado diferido a Nexo (cabecera) de
        // db/scripts/13_correo_mailsender.sql.
        await using var sin13 = await baseDatos.CrearBaseConScriptsAsync("dbSin13", clsBaseDatosPruebas.ScriptsSin07 - 4);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin13.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 13");
    }

    [Fact]
    public async Task HU036_ConEl13PeroSinEl14EsUnhealthyPorqueLeFaltaEl14()
    {
        // HU-036 (#205): el modelo EF mapea finalizadaAt y finalizadaPor de la cabecera (db/scripts/14_cotizacion_finalizada.sql).
        await using var sin14 = await baseDatos.CrearBaseConScriptsAsync("dbSin14", clsBaseDatosPruebas.ScriptsSin07 - 3);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin14.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 14");
    }

    [Fact]
    public async Task HU0401_ConEl14PeroSinEl16EsUnhealthyPorqueLeFaltaEl16()
    {
        // HU-040.1 (#229): el modelo EF mapea TokensRefresco.rolSesion (db/scripts/16_multiples_roles_activos.sql). No es "falta 0002"
        // aunque la columna sea de TokensRefresco.
        await using var sin16 = await baseDatos.CrearBaseConScriptsAsync("dbSin16", clsBaseDatosPruebas.ScriptsSin07 - 2);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin16.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 16");
    }

    [Fact]
    public async Task HU0181_ConEl06PeroSinEl09EsUnhealthyPorqueLeFaltaEl09()
    {
        // HU-018.1 (#155): el modelo EF mapea CotizacionesSolicitud.scorePrioridad de db/scripts/09_score_prioridad.sql.
        await using var sin09 = await baseDatos.CrearBaseConScriptsAsync("dbSin09", clsBaseDatosPruebas.ScriptsHasta06);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin09.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 09");
    }

    [Fact]
    public async Task HU0281_ConEl09PeroSinEl10EsUnhealthyPorqueLeFaltaEl10()
    {
        // HU-028.1 (#183): el modelo EF mapea motivoRechazo y rechazadaAt del camión de db/scripts/10_rechazo_linea.sql.
        await using var sin10 = await baseDatos.CrearBaseConScriptsAsync("dbSin10", clsBaseDatosPruebas.ScriptsHasta09);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin10.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 10");
    }

    [Fact]
    public async Task I191_ConEl05PeroSinEl06EsUnhealthyPorqueLeFaltaEl06()
    {
        // #191: la Idempotency-Key persistente usa dbo_cotifly.CapturasIdempotentes (db/scripts/06) con SQL propio: sin la tabla, la API
        // no degrada a memoria, la sonda lo dice.
        await using var sin06 = await baseDatos.CrearBaseConScriptsAsync("dbSin06", clsBaseDatosPruebas.ScriptsHasta05);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin06.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 06");
    }

    [Fact]
    public async Task I191_ConLaTablaDeLaPropuestaAnteriorEsUnhealthyPorqueLeFaltaEl06()
    {
        // La tabla de la propuesta de #173 (sin operacion, tomadaPor, enCursoHasta, resultado ni scorePrioridad) no basta.
        await using var vieja = await baseDatos.CrearBaseConScriptsAsync("dbSin06Vieja", clsBaseDatosPruebas.ScriptsHasta05);
        await clsBaseDatosPruebas.EjecutarAsync(vieja.Cadena, clsIdempotenciaSqlTests.TablaPropuestaAnterior);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(vieja.Cadena);

        resultado.Description.Should().Be("esquema incompleto: falta 06");
    }

    [Fact]
    public async Task UnaBaseInstaladaDesdeCeroConElScriptDeInstalacionEsHealthy()
    {
        await using var instalada = await baseDatos.CrearBaseInstaladaAsync("dbSaludInstalada", conSeeders: true);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(instalada.Cadena);

        resultado.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task SinElCheckDeEstadoEnvioEsUnhealthyPorqueLeFaltaEl0010()
    {
        await using var sinCheck = await baseDatos.CrearBaseInstaladaAsync("dbSaludSinCheck");
        await clsBaseDatosPruebas.EjecutarAsync(sinCheck.Cadena, "ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT ck_cotizaciones_estadoEnvio;");

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sinCheck.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionEsquemaSin0010, "el 0010 crea el CHECK si falta");
    }

    [Fact]
    public async Task SiFaltaOtraColumnaDelModeloLaNombra()
    {
        await using var incompleta = await baseDatos.CrearBaseInstaladaAsync("dbSaludIncompleta");
        await clsBaseDatosPruebas.EjecutarAsync(incompleta.Cadena, "ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP COLUMN idRecomendacionVehiculo;");

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(incompleta.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.PrefijoEsquemaIncompleto + "CotizacionVehiculosLineaSolicitud.idRecomendacionVehiculo");
    }

    [Fact]
    public async Task ElHealthServiceEjecutaElCheckEtiquetadoReady()
    {
        await using var proveedor = clsServiciosPruebas.Crear(baseDatos.CadenaCotifly, new clsRelojFalso());

        var reporte = await proveedor.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            r => r.Tags.Contains(clsDependenciasInfraestructura.EtiquetaReadiness), TestContext.Current.CancellationToken);

        reporte.Status.Should().Be(HealthStatus.Healthy);
        reporte.Entries.Should().ContainKey(clsBaseDatosHealthCheck.Nombre);
    }
}
