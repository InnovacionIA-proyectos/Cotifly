using System.Net;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// Selección del adaptador del costeo por <c>Nexo:UrlBase</c> (<see cref="clsFabricaCosteoNexo"/>), opciones <c>CosteoNexo</c> y
/// tubería de resiliencia del cliente <see cref="clsCosteoNexo.NombreCliente"/> (ADR-015): un POST nunca se reintenta.
/// </summary>
public sealed class clsRegistroCosteoNexoTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SinUrlBaseElCosteoEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url });
        using var ambito = proveedor.CreateScope();

        var costeo = ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>();

        costeo.Should().BeOfType<clsCosteoNexoNoDisponible>();
        costeo.EstaConfigurado.Should().BeFalse();
    }

    [Fact]
    public async Task ElNoDisponibleRespondeCosteoNexoNoDisponible()
    {
        var resultado = await new clsCosteoNexoNoDisponible().EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo("1", clsSolicitudCosteoMuestra.Crear(), null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
    }

    [Fact]
    public void ConUrlBaseElCosteoEsElDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>().Should().BeOfType<clsCosteoNexo>();
    }

    [Fact]
    public async Task UnPostQueRecibe503NoSeReintentaParaNoDuplicarLaSolicitudEnNexo()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.Created, """{"id":"x"}""");
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, s => UsarNexoFalso(s, nexo));
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>()
            .EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo("15927", clsSolicitudCosteoMuestra.Crear(), null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle("el alta no es idempotente");
    }

    [Fact]
    public void LaTuberiaDelCosteoEsDe30SegundosYNoLaDeLasBusquedas()
    {
        var opciones = new Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions();

        clsCosteoNexo.ConfigurarResiliencia(opciones);

        opciones.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        opciones.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        opciones.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(opciones.AttemptTimeout.Timeout * 2);
        clsCosteoNexo.NombreCliente.Should().NotBe(Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    public void UnIntervaloInicialFueraDeRangoImpideArrancar(string valor)
    {
        using var proveedor = Proveedor(new() { ["CosteoNexo:IntervaloInicialSegundos"] = valor });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCosteoNexo>>().Value;

        leer.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task UnaConsultaGetQueRecibe503SiSeReintenta()
    {
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.ServiceUnavailable)
            .Responder(HttpStatusCode.OK, """{"solicitud":{"id":"S","estado":"Descartada","lineas":[]}}""");
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, s => UsarNexoFalso(s, nexo));
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsCosteoNexo>().ConsultarCosteoAsync(_usuarioId, "S", Ct);

        resultado.Valor.EstadoSolicitud.Should().Be("Descartada");
        nexo.Peticiones.Should().HaveCount(2, "un GET sí es seguro de reintentar");
    }

    [Fact]
    public void ElIntervaloMaximoNoPuedeSerMenorQueElInicial()
    {
        using var proveedor = Proveedor(new() { ["CosteoNexo:IntervaloInicialSegundos"] = "120", ["CosteoNexo:IntervaloMaximoMinutos"] = "1" });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCosteoNexo>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("IntervaloMaximoMinutos");
    }

    [Fact]
    public void ElWorkerDelPollingQuedaRegistrado()
    {
        var servicios = new ServiceCollection();
        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        servicios.Should().ContainSingle(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                                              && d.ImplementationType == typeof(clsWorkerCosteoNexo));
    }

    [Theory]
    [InlineData(null, "true")]
    [InlineData(clsNexoClientesFalso.UrlBase, "false")]
    public async Task SinUrlBaseOApagadoElWorkerTerminaSinConsultarLaAgenda(string? url, string habilitado)
    {
        var polling = Substitute.For<IClsPollingCosteoService>();
        var servicios = new ServiceCollection();
        servicios.AddScoped(_ => polling);
        using var proveedor = servicios.BuildServiceProvider();
        using var worker = new clsWorkerCosteoNexo(
            proveedor.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new Cotifly.Infraestructura.Configuracion.clsOpcionesNexo { UrlBase = url }),
            Options.Create(new clsOpcionesCosteoNexo { Habilitado = bool.Parse(habilitado) }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<clsWorkerCosteoNexo>.Instance);

        await worker.StartAsync(Ct);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await worker.StopAsync(Ct);

        await polling.DidNotReceiveWithAnyArgs().PendientesAsync(Ct);
    }

    [Fact]
    public async Task UnaCotizacionQueFallaNoDetieneElLoteYCadaUnaVaEnSuPropioAmbito()
    {
        var primera = new clsConsultaCosteoPendiente(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var segunda = new clsConsultaCosteoPendiente(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var ambitos = new List<IClsPollingCosteoService>();
        var servicios = new ServiceCollection();
        servicios.AddScoped(_ =>
        {
            var polling = Substitute.For<IClsPollingCosteoService>();
            polling.PendientesAsync(Arg.Any<CancellationToken>()).Returns([primera, segunda]);
            polling.ProcesarAsync(primera, Arg.Any<CancellationToken>()).Returns<ResultadoConsultaCosteo>(_ => throw new InvalidOperationException("base caída"));
            polling.ProcesarAsync(segunda, Arg.Any<CancellationToken>()).Returns(ResultadoConsultaCosteo.Cambio);
            ambitos.Add(polling);
            return polling;
        });
        using var proveedor = servicios.BuildServiceProvider();
        var logger = new clsLoggerFalso<clsWorkerCosteoNexo>();

        var procesadas = await clsWorkerCosteoNexo.ProcesarLoteAsync(proveedor.GetRequiredService<IServiceScopeFactory>(), logger, Ct);

        procesadas.Should().Be(1);
        ambitos.Should().HaveCount(3, "un ámbito para la agenda y uno por cotización");
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Error && r.Excepcion is InvalidOperationException);
    }

    private static void UsarNexoFalso(IServiceCollection servicios, clsNexoClientesFalso nexo)
    {
        servicios.AddHttpClient(clsCosteoNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);

        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }
}
