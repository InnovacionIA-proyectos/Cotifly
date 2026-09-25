using System.Text;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Tests.Identidad;

/// <summary>Emisión del access JWT HS256 y opciones <c>Jwt</c> (ADR-009).</summary>
public sealed class clsEmisorTokensJwtTests
{
    private const string Clave = "clave-de-pruebas-de-al-menos-32-bytes-0123456789";
    private readonly clsRelojFalso _reloj = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task ElAccessEsHs256ConLosClaimsDelContratoYVenceEnSesentaMinutos()
    {
        var emisor = new clsEmisorTokensJwt(Configuracion(), _reloj);
        var usuario = Guid.NewGuid();

        var acceso = emisor.EmitirAcceso(usuario, "APEREZ", RolInterno.GerenteComercial);
        var validacion = await new JsonWebTokenHandler().ValidateTokenAsync(acceso.Token, new TokenValidationParameters
        {
            ValidIssuer = "cotifly-api",
            ValidAudience = "clientepro",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Clave)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });

        validacion.IsValid.Should().BeTrue(validacion.Exception?.Message);
        var jwt = (JsonWebToken)validacion.SecurityToken;
        jwt.Alg.Should().Be("HS256");
        jwt.Subject.Should().Be(usuario.ToString());
        jwt.Id.Should().Be(acceso.Jti);
        jwt.GetClaim("username").Value.Should().Be("APEREZ");
        jwt.GetClaim("rol").Value.Should().Be("gerente_comercial");
        jwt.ValidTo.Should().Be(acceso.ExpiraAt.UtcDateTime);
        acceso.ExpiraAt.Should().BeCloseTo(_reloj.AhoraUtc.AddMinutes(60), TimeSpan.FromSeconds(1));
        jwt.Claims.Select(c => c.Type).Should().NotContain(["password", "clave"]);
    }

    [Fact]
    public void CadaAccessTieneUnJtiDistinto()
    {
        var emisor = new clsEmisorTokensJwt(Configuracion(), _reloj);

        var jtis = Enumerable.Range(0, 20).Select(_ => emisor.EmitirAcceso(Guid.NewGuid(), "APEREZ", RolInterno.Comercial).Jti);

        jtis.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void SinClaveFueraDeProductionSeUsaUnaClaveEfimeraDe32BytesYSeAvisa()
    {
        var logger = new clsLoggerFalso<clsConfiguracionJwt>();

        var configuracion = new clsConfiguracionJwt(Options.Create(new clsOpcionesJwt()), logger);

        configuracion.ObtenerClaveFirma().Should().HaveCount(32);
        configuracion.VigenciaAcceso.Should().Be(TimeSpan.FromMinutes(60));
        configuracion.VigenciaRefresh.Should().Be(TimeSpan.FromDays(30));
        logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("Jwt:SigningKey"));
    }

    [Theory]
    [InlineData("corta")]
    [InlineData("0123456789012345678901234567890")]
    public void UnaClaveDeMenosDe32BytesNoArrancaEnNingunEntorno(string clave)
    {
        var resultado = new clsValidadorOpcionesJwt().Validate(null, new clsOpcionesJwt { SigningKey = clave });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("Jwt:SigningKey").And.NotContain(clave);
    }

    [Fact]
    public void UnaClaveDe32BytesOVaciaEsValidaParaElValidadorPropio()
    {
        var validador = new clsValidadorOpcionesJwt();

        validador.Validate(null, new clsOpcionesJwt { SigningKey = Clave }).Succeeded.Should().BeTrue();
        validador.Validate(null, new clsOpcionesJwt()).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("<secreto-aleatorio-de-al-menos-32-bytes>")]
    [InlineData("cambiar-esta-clave-por-una-de-32-bytes-o-mas")]
    public void EnProductionLaClaveEsObligatoriaYNoPuedeSerUnPlaceholder(string? clave)
    {
        var validador = new clsValidadorSecretosOpciones<clsOpcionesJwt>(new clsEntornoFalso("Production"), clsOpcionesJwt.Seccion);

        var resultado = validador.Validate(null, new clsOpcionesJwt { SigningKey = clave });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("Jwt:SigningKey");
    }

    private clsConfiguracionJwt Configuracion() =>
        new(Options.Create(new clsOpcionesJwt { SigningKey = Clave }), new clsLoggerFalso<clsConfiguracionJwt>());
}
