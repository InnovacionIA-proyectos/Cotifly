using System.Security.Cryptography;
using System.Text;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// Refresh token opaco (ADR-009): 32 bytes aleatorios de un generador criptográfico en base64url sin relleno
/// (43 caracteres). En la base solo se guarda su SHA-256 en hexadecimal minúscula (64 caracteres).
/// </summary>
public static class clsTokenOpaco
{
    public const int BytesAleatorios = 32;

    /// <summary>Valor nuevo para entregar al cliente (nunca se persiste).</summary>
    public static string Generar()
    {
        Span<byte> bytes = stackalloc byte[BytesAleatorios];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>SHA-256 del valor (UTF-8) en hexadecimal minúscula: lo que guarda <c>TokensRefresco.tokenHash</c>.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
