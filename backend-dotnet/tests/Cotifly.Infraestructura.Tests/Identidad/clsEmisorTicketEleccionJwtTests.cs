using System.Text;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Tests.Identidad;

/// <summary>
/// HU-040.2 (#230, ADR-025): el ticket de elección del rol. Vida de 5 minutos con el reloj de la aplicación, firma con una clave derivada
/// (ni el access sirve como ticket ni el ticket como access), audiencia y <c>typ</c> propios, y cualquier alteración lo invalida.
/// </summary>
public sealed class clsEmisorTicketEleccionJwtTests
{
    private const string Clave = "clave-de-pruebas-de-al-menos-32-bytes-0123456789";
    private readonly clsRelojFalso _reloj = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task UnTicketEmitidoSeValidaConSuUsuarioSuJtiYLosRolesOfrecidos()
    {
        var emisor = Emisor();
        var usuario = Guid.NewGuid();

        var emitido = emisor.Emitir(usuario, "JEFE", [RolInterno.Comercial, RolInterno.GerenteComercial]);
        var (ticket, motivo) = await emisor.ValidarAsync(emitido.Ticket);

        motivo.Should().BeNull();
        ticket!.UsuarioId.Should().Be(usuario);
        ticket.NombreUsuario.Should().Be("JEFE");
        ticket.Jti.Should().Be(emitido.Jti);
        ticket.Roles.Should().Equal(RolInterno.Comercial, RolInterno.GerenteComercial);
        ticket.ExpiraAt.Should().Be(emitido.ExpiraAt);
        emitido.ExpiraAt.Should().BeCloseTo(_reloj.AhoraUtc.AddMinutes(5), TimeSpan.FromSeconds(1));
        var jwt = new JsonWebToken(emitido.Ticket);
        jwt.Typ.Should().Be(clsEmisorTicketEleccionJwt.Tipo);
        jwt.Audiences.Should().Equal(clsEmisorTicketEleccionJwt.Audiencia);
        jwt.Claims.Select(c => c.Type).Should().NotContain("rol", "el ticket no lleva un rol de sesión");
    }

    [Fact]
    public async Task PasadosLosCincoMinutosElTicketEstaVencido()
    {
        var emisor = Emisor();
        var emitido = emisor.Emitir(Guid.NewGuid(), "JEFE", [RolInterno.Comercial, RolInterno.Director]);

        _reloj.Avanzar(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(50));
        var (vigente, _) = await emisor.ValidarAsync(emitido.Ticket);
        _reloj.Avanzar(TimeSpan.FromSeconds(15));
        var (vencido, motivo) = await emisor.ValidarAsync(emitido.Ticket);

        vigente.Should().NotBeNull();
        vencido.Should().BeNull();
        motivo.Should().Be(clsMotivosAcceso.TicketVencido);
    }

    [Fact]
    public async Task UnAccessTokenNoSirveComoTicket()
    {
        var acceso = new clsEmisorTokensJwt(Configuracion(), _reloj).EmitirAcceso(Guid.NewGuid(), "JEFE", RolInterno.Director);

        var (ticket, motivo) = await Emisor().ValidarAsync(acceso.Token);

        ticket.Should().BeNull();
        motivo.Should().Be(clsMotivosAcceso.TicketInvalido);
    }

    [Fact]
    public async Task ElTicketNoValidaComoAccessNiConLaClaveNiConLaAudienciaDelAccess()
    {
        var emitido = Emisor().Emitir(Guid.NewGuid(), "JEFE", [RolInterno.Comercial, RolInterno.GerenteComercial]);

        // Los parámetros del JwtBearer de la API (clave del access, audiencia "clientepro").
        var comoAccess = await new JsonWebTokenHandler().ValidateTokenAsync(emitido.Ticket, new TokenValidationParameters
        {
            ValidIssuer = "cotifly-api",
            ValidAudience = "clientepro",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Clave)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });
        // Aunque una validación olvidara la audiencia, la firma (clave derivada) no coincide.
        var sinAudiencia = await new JsonWebTokenHandler().ValidateTokenAsync(emitido.Ticket, new TokenValidationParameters
        {
            ValidIssuer = "cotifly-api",
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Clave)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        });

        comoAccess.IsValid.Should().BeFalse();
        sinAudiencia.IsValid.Should().BeFalse();
        sinAudiencia.Exception.Should().Match(e => e is SecurityTokenSignatureKeyNotFoundException || e is SecurityTokenInvalidSignatureException);
    }

    [Fact]
    public async Task UnTicketAlteradoOFirmadoConOtraClaveEsInvalido()
    {
        var emitido = Emisor().Emitir(Guid.NewGuid(), "JEFE", [RolInterno.Comercial, RolInterno.GerenteComercial]);
        var partes = emitido.Ticket.Split('.');
        var cargaAlterada = Base64UrlEncoder.Encode(Base64UrlEncoder.Decode(partes[1]).Replace("gerente_comercial", "director", StringComparison.Ordinal));
        var alterado = $"{partes[0]}.{cargaAlterada}.{partes[2]}";
        var deOtraClave = new clsEmisorTicketEleccionJwt(Configuracion("otra-clave-de-pruebas-de-32-bytes-o-mas-9876"), _reloj)
            .Emitir(Guid.NewGuid(), "JEFE", [RolInterno.Director, RolInterno.Comercial]);

        foreach (var invalido in new[] { alterado, deOtraClave.Ticket, "no-es-un-jwt", emitido.Ticket + "x" })
        {
            var (ticket, motivo) = await Emisor().ValidarAsync(invalido);
            ticket.Should().BeNull(invalido);
            motivo.Should().Be(clsMotivosAcceso.TicketInvalido);
        }
    }

    [Fact]
    public void LaClaveDelTicketEsDistintaDeLaDelAccessYDeterminista()
    {
        var acceso = Encoding.UTF8.GetBytes(Clave);

        var derivada = clsEmisorTicketEleccionJwt.DerivarClave(Encoding.UTF8.GetBytes(Clave));

        derivada.Should().HaveCount(32).And.NotEqual(acceso);
        clsEmisorTicketEleccionJwt.DerivarClave(Encoding.UTF8.GetBytes(Clave)).Should().Equal(derivada);
    }

    private clsEmisorTicketEleccionJwt Emisor() => new(Configuracion(), _reloj);

    private static clsConfiguracionJwt Configuracion(string clave = Clave) =>
        new(Options.Create(new clsOpcionesJwt { SigningKey = clave }), new clsLoggerFalso<clsConfiguracionJwt>());
}
