using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Infraestructura.Tests.Configuracion;

public sealed class clsValidadorSecretosOpcionesTests
{
    private const string ValorInseguro = "cambiar-Qw3Er5";

    [Fact]
    public void EnProduccionFallaYNombraLaClaveCompletaSinElValor()
    {
        var validador = new clsValidadorSecretosOpciones<clsOpcionesPrueba>(new clsEntornoFalso(Environments.Production), "Prueba");

        var resultado = validador.Validate(null, new clsOpcionesPrueba { Clave = ValorInseguro, Texto = ValorInseguro });

        resultado.Failed.Should().BeTrue();
        resultado.Failures.Should().ContainSingle()
            .Which.Should().Contain("Prueba:Clave").And.NotContain(ValorInseguro);
    }

    [Fact]
    public void EnDevelopmentNoFalla()
    {
        var validador = new clsValidadorSecretosOpciones<clsOpcionesPrueba>(new clsEntornoFalso(Environments.Development), "Prueba");

        var resultado = validador.Validate(null, new clsOpcionesPrueba { Clave = "" });

        resultado.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EnProduccionConSecretoValidoNoFalla()
    {
        var validador = new clsValidadorSecretosOpciones<clsOpcionesPrueba>(new clsEntornoFalso(Environments.Production), "Prueba");

        var resultado = validador.Validate(null, new clsOpcionesPrueba { Clave = "u8Rk2Vq9Lm4Zp7Xw" });

        resultado.Succeeded.Should().BeTrue();
    }

    internal sealed class clsOpcionesPrueba
    {
        [clsSecreto(LargoMinimo = 16)]
        public string Clave { get; set; } = string.Empty;

        public string Texto { get; set; } = string.Empty;
    }
}
