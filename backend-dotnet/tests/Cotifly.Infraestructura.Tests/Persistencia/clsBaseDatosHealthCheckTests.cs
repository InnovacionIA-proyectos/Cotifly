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

    [Fact]
    public async Task ConUnaBaseSinEl0005EsUnhealthyPorEsquemaIncompleto()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(await baseDatos.CadenaSin0005Async());

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0005", "el modelo EF ya mapea CotizacionLineasSolicitud y el Excel de licitaciones");
    }

    [Fact]
    public async Task ConUnaBaseSinEl0006EsUnhealthyPorEsquemaIncompleto()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(await baseDatos.CadenaSin0006Async());

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0006", "el modelo EF ya mapea rowVersion, pdfDesactualizado y el historial de PDF");
    }

    [Fact]
    public async Task ConUnaBaseSinEl0004EsUnhealthyPorEsquemaIncompleto()
    {
        var resultado = await clsBaseDatosHealthCheckTests.EvaluarAsync(await baseDatos.CadenaSin0004Async());

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be("esquema incompleto: falta 0004", "el modelo EF de Cotizaciones ya mapea las columnas del 0004");
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
