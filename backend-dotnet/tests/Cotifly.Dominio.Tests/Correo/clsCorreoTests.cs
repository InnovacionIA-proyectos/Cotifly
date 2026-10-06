using Cotifly.Dominio.Correo;

namespace Cotifly.Dominio.Tests.Correo;

public sealed class clsCorreoTests
{
    private static readonly Guid _cotizacion = Guid.NewGuid();

    [Fact]
    public void UnCorreoGuardaSusDestinatariosParaYCcNormalizados()
    {
        var correo = clsCorreo.Crear(_cotizacion, [" Cliente@Empresa.com "], EstadoCorreo.Enviado, "Cotización", ["jefe@empresa.com"]).Valor;

        correo.CotizacionId.Should().Be(_cotizacion);
        correo.Para.Should().Equal("cliente@empresa.com");
        correo.Cc.Should().Equal("jefe@empresa.com");
        correo.Destinatarios.Should().OnlyContain(d => d.CorreoId == correo.Id);
    }

    [Fact]
    public void UnCorreoExigeAlMenosUnDestinatarioPara()
    {
        var resultado = clsCorreo.Crear(_cotizacion, [], EstadoCorreo.Enviado, cc: ["jefe@empresa.com"]);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCorreo.CodigoError);
        resultado.Error.Mensaje.Should().Contain("para");
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("@empresa.com")]
    [InlineData("cliente@")]
    [InlineData("con espacio@empresa.com")]
    [InlineData("")]
    public void UnEmailInvalidoSeRechaza(string email) =>
        clsCorreo.Crear(_cotizacion, [email], EstadoCorreo.Enviado).EsFallo.Should().BeTrue();

    [Fact]
    public void UnEmailDeMasDe320CaracteresSeRechaza() =>
        clsCorreo.Crear(_cotizacion, [new string('a', 310) + "@empresa.com"], EstadoCorreo.Enviado).EsFallo.Should().BeTrue();

    [Fact]
    public void LosEmailsRepetidosDelMismoTipoSeGuardanUnaVez()
    {
        var correo = clsCorreo.Crear(_cotizacion, ["a@x.com", "A@X.COM"], EstadoCorreo.Enviado, cc: ["a@x.com"]).Valor;

        correo.Para.Should().Equal("a@x.com");
        correo.Cc.Should().Equal("a@x.com");
    }

    [Fact]
    public void UnCorreoDebePertenecerAUnaCotizacion() =>
        clsCorreo.Crear(Guid.Empty, ["a@x.com"], EstadoCorreo.Enviado).EsFallo.Should().BeTrue();

    [Fact]
    public void TieneMismosParaIgnoraMayusculasOrdenYVacios()
    {
        var correo = clsCorreo.Crear(_cotizacion, ["a@x.com", "b@x.com"], EstadoCorreo.Enviado).Valor;

        correo.TieneMismosPara(["B@x.com", "a@X.com", " "]).Should().BeTrue();
        correo.TieneMismosPara(["a@x.com"]).Should().BeFalse();
        correo.TieneMismosPara(["a@x.com", "b@x.com", "c@x.com"]).Should().BeFalse();
    }

    [Fact]
    public void ElDestinatarioSoloAceptaTiposParaOCc()
    {
        clsCorreoDestinatario.Crear(Guid.NewGuid(), "a@x.com", TipoDestinatario.Cc).EsExitoso.Should().BeTrue();
        clsCorreoDestinatario.Crear(Guid.NewGuid(), "a@x.com", (TipoDestinatario)7).EsFallo.Should().BeTrue();
    }
}
