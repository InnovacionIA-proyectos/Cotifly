using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// URL firmada del PDF (ADR-011 §3): token <c>{usuarioId}.{vence}.{firma}</c>, con la firma HMAC-SHA256 (base64url) de
/// <c>archivo\nusuarioId\nvence</c> y <c>vence</c> en segundos Unix (<c>Pdf:MinutosUrlFirmada</c>, por defecto 15). La comparación es
/// de tiempo constante. Sin <c>Pdf:ClaveFirmaUrl</c> se usa una clave aleatoria por proceso (warning).
/// </summary>
public sealed class clsFirmaUrlPdfHmac : IClsFirmaUrlPdf
{
    private readonly byte[] _clave;
    private readonly TimeSpan _vigencia;
    private readonly IClsReloj _reloj;

    public clsFirmaUrlPdfHmac(IOptions<clsOpcionesFirmaUrlPdf> opciones, IClsReloj reloj, ILogger<clsFirmaUrlPdfHmac> logger)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        ArgumentNullException.ThrowIfNull(reloj);
        ArgumentNullException.ThrowIfNull(logger);
        var valor = opciones.Value;
        if (string.IsNullOrWhiteSpace(valor.ClaveFirmaUrl))
        {
            logger.LogWarning("Pdf:ClaveFirmaUrl no está configurada: los enlaces firmados del PDF usan una clave efímera por proceso.");
            _clave = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            _clave = Encoding.UTF8.GetBytes(valor.ClaveFirmaUrl);
        }

        _vigencia = TimeSpan.FromMinutes(valor.MinutosUrlFirmada);
        _reloj = reloj;
    }

    public string Firmar(string archivo, Guid usuarioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivo);
        var vence = (_reloj.AhoraUtc + _vigencia).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var usuario = usuarioId.ToString("N");
        return $"{usuario}.{vence}.{Base64Url(Firma(archivo, usuario, vence))}";
    }

    public Guid? Verificar(string archivo, string? token)
    {
        if (string.IsNullOrWhiteSpace(archivo) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var partes = token.Split('.');
        if (partes.Length != 3
            || !Guid.TryParseExact(partes[0], "N", out var usuarioId)
            || !long.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var vence)
            || _reloj.AhoraUtc.ToUnixTimeSeconds() > vence)
        {
            return null;
        }

        byte[] recibida;
        try
        {
            recibida = DesdeBase64Url(partes[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        var esperada = Firma(archivo, partes[0], partes[1]);
        return CryptographicOperations.FixedTimeEquals(esperada, recibida) ? usuarioId : null;
    }

    private byte[] Firma(string archivo, string usuario, string vence) =>
        HMACSHA256.HashData(_clave, Encoding.UTF8.GetBytes($"{archivo}\n{usuario}\n{vence}"));

    private static string Base64Url(byte[] datos) => Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DesdeBase64Url(string texto)
    {
        var base64 = texto.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }
}
