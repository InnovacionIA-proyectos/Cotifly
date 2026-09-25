using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Configuracion;

public sealed class clsValidadorOpcionesConexionesTests
{
    private const string CadenaValida =
        "Server=servidor-prueba,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=contrasena-de-prueba-123456;Encrypt=True";

    [Fact]
    public void EnProductionAceptaUnaCadenaConSqlLoginYContrasenaReal()
    {
        var resultado = Validar("Production", CadenaValida);

        resultado.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Server=servidor-prueba;Database=x;Trusted_Connection=True")]
    [InlineData("Server=servidor-prueba;Database=x;Integrated Security=SSPI")]
    public void EnProductionRechazaLaAutenticacionIntegrada(string cadena)
    {
        var resultado = Validar("Production", cadena);

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("ConnectionStrings:Cotifly").And.Contain("integrada");
    }

    [Theory]
    [InlineData("Server=h;Database=x;User Id=cotifly_app;Password=<secreto>")]
    [InlineData("Server=h;Database=x;User Id=cotifly_app;Password=cambiar-esto")]
    [InlineData("Server=h;Database=x;User Id=cotifly_app")]
    public void EnProductionRechazaContrasenasDeEjemploOAusentesSinMostrarlas(string cadena)
    {
        var resultado = Validar("Production", cadena);

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("ConnectionStrings:Cotifly (Password)")
            .And.Contain("variable ConnectionStrings__Cotifly, parámetro Password")
            .And.NotContain("cambiar-esto");
    }

    [Fact]
    public void EnProductionRechazaUnaCadenaIlegible()
    {
        var resultado = Validar("Production", "esto=no=es;;una=cadena=\"");

        resultado.Failed.Should().BeTrue();
    }

    [Fact]
    public void FueraDeProductionNoValida()
    {
        var resultado = Validar("Development", "Server=h;Trusted_Connection=True;Password=<secreto>");

        resultado.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void LaCadenaVaciaLaReportaElValidadorDeSecretosEnProduction()
    {
        var validador = new clsValidadorSecretosOpciones<clsOpcionesConexiones>(new clsEntornoFalso("Production"), clsOpcionesConexiones.Seccion);

        var resultado = validador.Validate(null, new clsOpcionesConexiones { Cotifly = "", Identidad = CadenaValida });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("ConnectionStrings:Cotifly");
    }

    private static ValidateOptionsResult Validar(string entorno, string cadena) =>
        new clsValidadorOpcionesConexiones(new clsEntornoFalso(entorno))
            .Validate(null, new clsOpcionesConexiones { Cotifly = cadena });
}
