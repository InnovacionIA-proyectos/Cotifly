using Cotifly.Infraestructura.Configuracion;

namespace Cotifly.Infraestructura.Tests.Configuracion;

public sealed class clsValidadorOpcionesZonaTests
{
    private readonly clsValidadorOpcionesZona _validador = new();

    [Fact]
    public void LaZonaPorDefectoEsAmericaBogota()
    {
        new clsOpcionesZona().ZonaHoraria.Should().Be("America/Bogota");
    }

    [Theory]
    [InlineData("America/Bogota")]
    [InlineData("UTC")]
    public void UnaZonaExistentePasa(string zona)
    {
        _validador.Validate(null, new clsOpcionesZona { ZonaHoraria = zona }).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Marte/Olimpo")]
    [InlineData("")]
    public void UnaZonaInexistenteFallaNombrandoLaClave(string zona)
    {
        var resultado = _validador.Validate(null, new clsOpcionesZona { ZonaHoraria = zona });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("Zona:ZonaHoraria");
    }
}
