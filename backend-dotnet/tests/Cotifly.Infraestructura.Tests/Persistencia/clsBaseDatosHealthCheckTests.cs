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
        await using var sin0010 = await baseDatos.CrearBaseConScriptsAsync("dbSin0010", clsBaseDatosPruebas.Scripts.Length - 1);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(sin0010.Cadena);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0010", "el modelo EF mapea los nombres de municipio, solicitudNexoId y el nombre del conexo");
    }

    [Fact]
    public async Task UnaBaseInstaladaDesdeCeroConElScriptDeInstalacionEsHealthy()
    {
        await using var instalada = await baseDatos.CrearBaseInstaladaAsync("dbSaludInstalada", conSeeders: true);

        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(instalada.Cadena);

        resultado.Status.Should().Be(HealthStatus.Healthy);
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
