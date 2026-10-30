using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Configuracion;

/// <summary>
/// HU-032 (#197, M1 de la auditoría): <see cref="clsAvisosArranqueCaptura"/> avisa al arrancar, sin impedirlo, cuando
/// <c>Captura:ClasesSinVehiculoObligatorio</c> no incluye Contenedor (la lista configurada reemplaza a la de por defecto).
/// </summary>
public sealed class clsAvisosArranqueCapturaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnaListaSinContenedorDejaUnWarningConCodigoEstable()
    {
        var logger = await ArrancarAsync(["Carga Suelta"]);

        var registro = logger.Registros.Should().ContainSingle().Subject;
        registro.Nivel.Should().Be(LogLevel.Warning);
        registro.Evento.Id.Should().Be(clsAvisosArranqueCaptura.EventoContenedorExigeVehiculo);
        registro.Evento.Name.Should().Be(clsAvisosArranqueCaptura.CodigoContenedorExigeVehiculo);
        registro.Mensaje.Should().StartWith("CAPTURA_CONTENEDOR_EXIGE_VEHICULO:").And.Contain("[Carga Suelta]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Contenedor|Carga Suelta")]
    [InlineData("contenedor")]
    [InlineData(" |")]
    public async Task SinConfigurarConContenedorOSoloVaciosNoAvisa(string? clases) =>
        (await ArrancarAsync(clases?.Split('|'))).Registros.Should().BeEmpty();

    [Fact]
    public void SeRegistraComoServicioDeArranque()
    {
        var servicios = new ServiceCollection();

        servicios.AddInfraestructura(new ConfigurationBuilder().Build());

        servicios.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(clsAvisosArranqueCaptura));
    }

    private static async Task<clsLoggerFalso<clsAvisosArranqueCaptura>> ArrancarAsync(string[]? clases)
    {
        var logger = new clsLoggerFalso<clsAvisosArranqueCaptura>();
        var avisos = new clsAvisosArranqueCaptura(
            Options.Create(new clsOpcionesCaptura { ClasesSinVehiculoObligatorio = clases?.ToList() }), logger);

        await avisos.StartAsync(Ct);
        await avisos.StopAsync(Ct);
        return logger;
    }
}
