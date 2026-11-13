using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// <see cref="IClsEmisorTicketEleccion"/> con <see cref="JsonWebTokenHandler"/> (ADR-025). JWT HS256 de 5 minutos con
/// <c>iss</c> (el del access), <c>aud</c> <see cref="Audiencia"/>, <c>typ</c> <see cref="Tipo"/>, <c>sub</c> (usuario), <c>jti</c>,
/// <c>username</c> y <c>roles</c> (los ofrecidos). Se firma con una clave DERIVADA de <c>Jwt:SigningKey</c>
/// (HMAC-SHA256 de la clave con <see cref="EtiquetaDerivacion"/>): aunque una validación olvidara la audiencia, la firma de un access no
/// valida como ticket ni la de un ticket como access. La vigencia se valida con el reloj de la aplicación (sin tolerancia extra).
/// </summary>
public sealed class clsEmisorTicketEleccionJwt : IClsEmisorTicketEleccion
{
    /// <summary>Audiencia exclusiva del ticket: el <c>JwtBearer</c> de la API exige la del access, así que un ticket nunca autentica.</summary>
    public const string Audiencia = "cotifly-eleccion-rol";

    /// <summary>Encabezado <c>typ</c> del ticket.</summary>
    public const string Tipo = "eleccion-rol+jwt";

    /// <summary>Etiqueta de la derivación de la clave del ticket.</summary>
    public const string EtiquetaDerivacion = "cotifly/eleccion-rol/v1";

    /// <summary>Claim con los códigos de los roles ofrecidos.</summary>
    public const string ClaimRoles = "roles";

    private readonly JsonWebTokenHandler _manejador = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly IClsConfiguracionJwt _configuracion;
    private readonly IClsReloj _reloj;
    private readonly SymmetricSecurityKey _clave;
    private readonly SigningCredentials _credenciales;

    public clsEmisorTicketEleccionJwt(IClsConfiguracionJwt configuracion, IClsReloj reloj)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        ArgumentNullException.ThrowIfNull(reloj);
        _configuracion = configuracion;
        _reloj = reloj;
        _clave = new SymmetricSecurityKey(DerivarClave(configuracion.ObtenerClaveFirma()))
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        };
        _credenciales = new SigningCredentials(_clave, SecurityAlgorithms.HmacSha256);
    }

    public TimeSpan Vigencia { get; } = TimeSpan.FromMinutes(5);

    public clsTicketEleccionEmitido Emitir(Guid usuarioId, string nombreUsuario, IReadOnlyList<RolInterno> roles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombreUsuario);
        ArgumentNullException.ThrowIfNull(roles);
        if (usuarioId == Guid.Empty || roles.Count == 0)
        {
            throw new ArgumentException("El ticket de elección necesita un usuario y al menos un rol.");
        }

        var ahora = _reloj.AhoraUtc;
        var expira = DateTimeOffset.FromUnixTimeSeconds((ahora + Vigencia).ToUnixTimeSeconds());
        var jti = Guid.NewGuid().ToString("N");
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _configuracion.Emisor,
            Audience = Audiencia,
            TokenType = Tipo,
            IssuedAt = ahora.UtcDateTime,
            Expires = expira.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [clsClaimsCotifly.Sub] = usuarioId.ToString("D"),
                [clsClaimsCotifly.Jti] = jti,
                [clsClaimsCotifly.Username] = nombreUsuario,
                [ClaimRoles] = roles.Select(r => clsCodigoEnum.ACodigo(r)).ToArray(),
            },
            SigningCredentials = _credenciales,
        };

        return new clsTicketEleccionEmitido(_manejador.CreateToken(descriptor), jti, expira);
    }

    public async Task<(clsTicketEleccionValido? Ticket, string? Motivo)> ValidarAsync(string ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || ticket.Length > clsCodigosAcceso.LargoMaximoTicket)
        {
            return (null, clsMotivosAcceso.TicketInvalido);
        }

        var ahora = _reloj.AhoraUtc;
        var vencido = false;
        var parametros = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _configuracion.Emisor,
            ValidateAudience = true,
            ValidAudience = Audiencia,
            ValidTypes = [Tipo],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _clave,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireExpirationTime = true,
            ValidateLifetime = true,
            LifetimeValidator = (_, expira, _, _) =>
            {
                vencido = expira is not { } fin || ahora >= new DateTimeOffset(DateTime.SpecifyKind(fin, DateTimeKind.Utc));
                return !vencido;
            },
        };

        TokenValidationResult resultado;
        try
        {
            resultado = await _manejador.ValidateTokenAsync(ticket, parametros);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException or JsonException)
        {
            return (null, clsMotivosAcceso.TicketInvalido);
        }

        if (!resultado.IsValid)
        {
            return (null, vencido ? clsMotivosAcceso.TicketVencido : clsMotivosAcceso.TicketInvalido);
        }

        var token = (JsonWebToken)resultado.SecurityToken;
        if (!Guid.TryParse(Claim(token, clsClaimsCotifly.Sub), out var usuarioId)
            || Claim(token, clsClaimsCotifly.Jti) is not { Length: > 0 and <= 64 } jti
            || Claim(token, clsClaimsCotifly.Username) is not { Length: > 0 } nombreUsuario
            || !RolesDe(token, out var roles))
        {
            return (null, clsMotivosAcceso.TicketInvalido);
        }

        return (new clsTicketEleccionValido(usuarioId, nombreUsuario, jti, new DateTimeOffset(token.ValidTo, TimeSpan.Zero), roles), null);
    }

    /// <summary>HMAC-SHA256(clave del access, <see cref="EtiquetaDerivacion"/>): 32 bytes que solo sirven para el ticket.</summary>
    internal static byte[] DerivarClave(byte[] claveAcceso)
    {
        ArgumentNullException.ThrowIfNull(claveAcceso);
        try
        {
            return HMACSHA256.HashData(claveAcceso, Encoding.UTF8.GetBytes(EtiquetaDerivacion));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveAcceso);
        }
    }

    private static string? Claim(JsonWebToken token, string nombre) =>
        token.TryGetPayloadValue<string>(nombre, out var valor) ? valor : null;

    private static bool RolesDe(JsonWebToken token, out IReadOnlyList<RolInterno> roles)
    {
        roles = [];
        if (!token.TryGetPayloadValue<string[]>(ClaimRoles, out var codigos) || codigos is not { Length: > 0 })
        {
            return false;
        }

        var lista = new List<RolInterno>();
        foreach (var codigo in codigos)
        {
            if (!clsCodigoEnum.TryDesdeCodigo<RolInterno>(codigo, out var rol))
            {
                return false;
            }

            lista.Add(rol);
        }

        roles = lista;
        return true;
    }
}
