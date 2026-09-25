using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Emite el access token de Cotifly: JWT HS256 con <c>sub</c> (UUID de <c>UsuariosWeb</c>), <c>jti</c>, <c>username</c> y
/// <c>rol</c> (<see cref="clsClaimsCotifly"/>), vigencia <see cref="IClsConfiguracionJwt.VigenciaAcceso"/> (ADR-009).
/// </summary>
public interface IClsEmisorTokens
{
    clsTokenAccesoEmitido EmitirAcceso(Guid usuarioId, string nombreUsuario, RolInterno rol);
}

/// <summary>
/// Parámetros de los tokens (sección <c>Jwt</c>): los usa el emisor y la validación de <c>JwtBearer</c> en la Api, que así
/// no depende de Infraestructura (ADR-002).
/// </summary>
public interface IClsConfiguracionJwt
{
    /// <summary>Claim <c>iss</c> emitido y exigido.</summary>
    string Emisor { get; }

    /// <summary>Claim <c>aud</c> emitido y exigido.</summary>
    string Audiencia { get; }

    /// <summary>Vigencia del access token (por defecto 60 min).</summary>
    TimeSpan VigenciaAcceso { get; }

    /// <summary>Vigencia absoluta de la sesión de refresh desde el login (por defecto 30 días; la rotación no la extiende).</summary>
    TimeSpan VigenciaRefresh { get; }

    /// <summary>Clave HS256 (al menos 32 bytes). Copia nueva en cada llamada.</summary>
    byte[] ObtenerClaveFirma();
}

/// <summary>Nombres de los claims del access token (<c>MapInboundClaims = false</c>: llegan con estos nombres).</summary>
public static class clsClaimsCotifly
{
    public const string Sub = "sub";
    public const string Jti = "jti";
    public const string Username = "username";
    public const string Rol = "rol";
    public const string Expira = "exp";
}
