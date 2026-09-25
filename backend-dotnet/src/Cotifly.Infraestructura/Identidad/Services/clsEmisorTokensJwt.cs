using System.Security.Cryptography;
using System.Text;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// <see cref="IClsConfiguracionJwt"/> a partir de <see cref="clsOpcionesJwt"/>. Singleton: sin <c>Jwt:SigningKey</c> (solo
/// posible fuera de Production, la validación al arrancar lo impide allí) genera una clave aleatoria de 32 bytes para
/// este proceso y lo avisa.
/// </summary>
public sealed class clsConfiguracionJwt : IClsConfiguracionJwt
{
    private readonly byte[] _clave;

    public clsConfiguracionJwt(IOptions<clsOpcionesJwt> opciones, ILogger<clsConfiguracionJwt> logger)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(logger);
        var valor = opciones.Value;
        Emisor = valor.Issuer;
        Audiencia = valor.Audience;
        VigenciaAcceso = TimeSpan.FromMinutes(valor.AccessMinutes);
        VigenciaRefresh = TimeSpan.FromDays(valor.RefreshDays);

        if (string.IsNullOrEmpty(valor.SigningKey))
        {
            _clave = RandomNumberGenerator.GetBytes(clsOpcionesJwt.BytesMinimosClave);
            logger.LogWarning("Jwt:SigningKey no está configurada: se usa una clave aleatoria efímera (los tokens dejan de valer al reiniciar).");
        }
        else
        {
            _clave = Encoding.UTF8.GetBytes(valor.SigningKey);
        }
    }

    public string Emisor { get; }

    public string Audiencia { get; }

    public TimeSpan VigenciaAcceso { get; }

    public TimeSpan VigenciaRefresh { get; }

    public byte[] ObtenerClaveFirma() => (byte[])_clave.Clone();
}

/// <summary>
/// <see cref="IClsEmisorTokens"/> con <see cref="JsonWebTokenHandler"/>: JWT HS256 con <c>iss</c>, <c>aud</c>, <c>iat</c>,
/// <c>nbf</c>, <c>exp</c> (del reloj de la aplicación), <c>sub</c>, <c>jti</c> (GUID nuevo), <c>username</c> y <c>rol</c>
/// (código snake_case del rol interno).
/// </summary>
public sealed class clsEmisorTokensJwt(IClsConfiguracionJwt configuracion, IClsReloj reloj) : IClsEmisorTokens
{
    private readonly JsonWebTokenHandler _manejador = new() { SetDefaultTimesOnTokenCreation = false };

    // Una sola clave por emisor y sin caché global de proveedores de firma: crear una clave por token llenaba la caché de
    // Microsoft.IdentityModel, que desecha proveedores aún en uso por otras peticiones (fallos intermitentes).
    private readonly SigningCredentials _credenciales = new(
        new SymmetricSecurityKey(configuracion.ObtenerClaveFirma())
        {
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
        },
        SecurityAlgorithms.HmacSha256);

    public clsTokenAccesoEmitido EmitirAcceso(Guid usuarioId, string nombreUsuario, RolInterno rol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombreUsuario);

        var ahora = reloj.AhoraUtc;
        var expira = ahora + configuracion.VigenciaAcceso;
        var jti = Guid.NewGuid().ToString("D");
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = configuracion.Emisor,
            Audience = configuracion.Audiencia,
            IssuedAt = ahora.UtcDateTime,
            NotBefore = ahora.UtcDateTime,
            Expires = expira.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [clsClaimsCotifly.Sub] = usuarioId.ToString("D"),
                [clsClaimsCotifly.Jti] = jti,
                [clsClaimsCotifly.Username] = nombreUsuario,
                [clsClaimsCotifly.Rol] = clsCodigoEnum.ACodigo(rol),
            },
            SigningCredentials = _credenciales,
        };

        return new clsTokenAccesoEmitido(_manejador.CreateToken(descriptor), jti, DateTimeOffset.FromUnixTimeSeconds(expira.ToUnixTimeSeconds()));
    }
}
