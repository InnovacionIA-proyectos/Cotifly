using Cotifly.Dominio.Correo;

namespace Cotifly.Dominio.Tests.Correo;

/// <summary>
/// DEF-1027B-2 (#228, E2E real del 2026-10-27B): reglas de lo que viaja como cabecera del correo (asunto y direcciones). Un asunto con
/// CR/LF llegó a MailSender y al relay SMTP ("Hola\r\nBcc: intruso@..."): riesgo de inyección de cabeceras.
/// </summary>
public sealed class clsCabecerasCorreoTests
{
    private static readonly Guid _cotizacion = Guid.NewGuid();

    [Theory]
    [InlineData("Hola\r\nBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\nBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\rBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\0mundo")]
    [InlineData("Hola\tmundo")]
    [InlineData("Hola\u0085Bcc: x@y.com")]
    [InlineData("Hola\u2028Bcc: x@y.com")]
    [InlineData("Hola\u2029Bcc: x@y.com")]
    [InlineData("Hola\u007Fmundo")]
    [InlineData("Hola\u001Bmundo")]
    [InlineData("Hola\r\n")]
    public void UnTextoConCaracteresDeControlNoVaEnUnaCabecera(string texto) =>
        clsCabecerasCorreo.TieneCaracteresDeControl(texto).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cotización PCN-001 — Botero Soto")]
    [InlineData("Ñandú, acción y café ☕ 👍🏽")]
    [InlineData("[QA] Cotización PCN-001")]
    public void UnTextoSinCaracteresDeControlSiVa(string? texto) =>
        clsCabecerasCorreo.TieneCaracteresDeControl(texto).Should().BeFalse();

    [Theory]
    [InlineData("cliente@empresa.com")]
    [InlineData("nombre.apellido+cotizaciones@sub.empresa.com.co")]
    [InlineData("qa@pruebas.local")]
    [InlineData("a@x.com")]
    [InlineData("o'brien@empresa.ie")]
    [InlineData("josé@empresa.co")]
    [InlineData("x_y-z@mi-empresa.com")]
    public void UnaDireccionValida(string email) => clsCabecerasCorreo.EsDireccionValida(email).Should().BeTrue();

    [Theory]
    [InlineData("cliente@empresa.com\r\nBcc: intruso@ejemplo.invalid")]
    [InlineData("cliente@empresa.com\r\n")]
    [InlineData("\r\ncliente@empresa.com")]
    [InlineData("cliente@empresa.com\0")]
    [InlineData("cliente@empresa.com\u2028")]
    [InlineData("cliente@empresa\u0085.com")]
    [InlineData("a@empresa.com,b@otra.com")]
    [InlineData("a@empresa.com;b@otra.com")]
    [InlineData("Cliente <a@empresa.com>")]
    [InlineData("\"a\"@empresa.com")]
    [InlineData("a@empresa.com@otra.com")]
    [InlineData("a(comentario)@empresa.com")]
    [InlineData("a@[127.0.0.1]")]
    [InlineData("a:b@empresa.com")]
    [InlineData("a\\b@empresa.com")]
    [InlineData("sin-arroba")]
    [InlineData("@empresa.com")]
    [InlineData("cliente@")]
    [InlineData("cliente@localhost")]
    [InlineData("cliente@empresa")]
    [InlineData(".cliente@empresa.com")]
    [InlineData("cliente.@empresa.com")]
    [InlineData("cli..ente@empresa.com")]
    [InlineData("cliente@-empresa.com")]
    [InlineData("cliente@empresa-.com")]
    [InlineData("cliente@empresa..com")]
    [InlineData("cliente@.empresa.com")]
    [InlineData("cliente@empresa.com.")]
    [InlineData("cliente@emp_resa.com")]
    [InlineData("con espacio@empresa.com")]
    [InlineData(" cliente@empresa.com")]
    [InlineData("cliente\u200B@empresa.com")]
    [InlineData("cliente\u202E@empresa.com")]
    [InlineData("")]
    [InlineData(null)]
    public void UnaDireccionInvalida(string? email) => clsCabecerasCorreo.EsDireccionValida(email).Should().BeFalse();

    [Fact]
    public void LaParteLocalAdmiteHasta64CaracteresYElTotalHasta320()
    {
        clsCabecerasCorreo.EsDireccionValida(new string('a', 64) + "@empresa.com").Should().BeTrue();
        clsCabecerasCorreo.EsDireccionValida(new string('a', 65) + "@empresa.com").Should().BeFalse();
        clsCabecerasCorreo.EsDireccionValida("a@" + new string('b', 64) + ".com").Should().BeFalse("una etiqueta del dominio tiene hasta 63");
        var largo = "a@" + string.Join('.', Enumerable.Repeat(new string('b', 63), 5)) + ".com";
        clsCabecerasCorreo.EsDireccionValida(largo).Should().BeFalse("el dominio tiene hasta 255");
    }

    [Theory]
    [InlineData("cliente@empresa.com\r\nBcc: intruso@ejemplo.invalid")]
    [InlineData("cliente@empresa.com\r\n")]
    [InlineData("cliente@empresa.com\0")]
    [InlineData("cliente@localhost")]
    public void ElDestinatarioDelDominioRechazaLaDireccionSinRecortarla(string email) =>
        clsCorreoDestinatario.Crear(_cotizacion, email, TipoDestinatario.Para).EsFallo.Should().BeTrue();

    [Fact]
    public void ElDestinatarioDelDominioSigueRecortandoEspaciosYPasandoAMinusculas() =>
        clsCorreoDestinatario.Crear(_cotizacion, " Cliente@Empresa.com ", TipoDestinatario.Para).Valor.Email.Should().Be("cliente@empresa.com");
}
