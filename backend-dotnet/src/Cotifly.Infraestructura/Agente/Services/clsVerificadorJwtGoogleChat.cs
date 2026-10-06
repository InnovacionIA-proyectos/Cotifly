using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// Verifica el JWT de Google Chat (port de <c>GoogleChatJwtVerifier</c> de <c>orquestador/gchat.py</c>, sin <c>google-auth</c>):
/// firma RS256 contra los certificados públicos de <c>chat@system.gserviceaccount.com</c> (<c>GChat:UrlCertificados</c>, en caché
/// según su <c>Cache-Control</c> o una hora, y releídos si llega un <c>kid</c> desconocido), emisor de sistema de Chat, audiencia =
/// <c>GChat:ProjectNumber</c> y vigencia. Singleton; nunca lanza por un token inválido.
/// </summary>
public sealed class clsVerificadorJwtGoogleChat(
    IHttpClientFactory http,
    IOptions<clsOpcionesGoogleChat> opciones,
    TimeProvider tiempo,
    ILogger<clsVerificadorJwtGoogleChat> logger) : IClsVerificadorJwtGoogleChat
{
    public const string Emisor = "chat@system.gserviceaccount.com";
    public const string NombreCliente = "GoogleChat";

    /// <summary>Vida de la caché de certificados cuando la respuesta no trae <c>max-age</c>.</summary>
    public static readonly TimeSpan CachePorDefecto = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _candado = new(1, 1);
    private IReadOnlyList<SecurityKey> _claves = [];
    private DateTimeOffset _vence = DateTimeOffset.MinValue;

    /// <summary>Descargas de certificados hechas (diagnóstico y pruebas de la caché).</summary>
    public int Descargas { get; private set; }

    public async Task<bool> VerificarAsync(string? authorization, CancellationToken cancellationToken = default)
    {
        var valor = opciones.Value;
        if (!valor.VerificarJwt)
        {
            return true;
        }

        var token = ExtraerBearer(authorization);
        if (token is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(valor.ProjectNumber))
        {
            logger.LogWarning("Webhook de Google Chat: GChat:ProjectNumber no está configurado (audiencia del JWT); se rechaza el evento.");
            return false;
        }

        try
        {
            var resultado = await ValidarAsync(token, valor.ProjectNumber.Trim(), forzarDescarga: false, cancellationToken);
            if (!resultado.IsValid && resultado.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // Google rota sus claves: un kid desconocido obliga a releer los certificados una vez.
                resultado = await ValidarAsync(token, valor.ProjectNumber.Trim(), forzarDescarga: true, cancellationToken);
            }

            if (!resultado.IsValid)
            {
                logger.LogWarning("Webhook de Google Chat: JWT inválido ({Motivo}).", resultado.Exception?.GetType().Name ?? "desconocido");
            }

            return resultado.IsValid;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning(ex, "Webhook de Google Chat: no se pudieron leer los certificados de Google; se rechaza el evento.");
            return false;
        }
    }

    /// <summary>Token de <c>Authorization: Bearer &lt;jwt&gt;</c>, o <c>null</c> si falta o está mal formado.</summary>
    public static string? ExtraerBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return null;
        }

        var partes = authorization.Trim().Split(' ', 2);
        return partes.Length == 2 && partes[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(partes[1])
            ? partes[1].Trim()
            : null;
    }

    private async Task<TokenValidationResult> ValidarAsync(string token, string audiencia, bool forzarDescarga, CancellationToken cancellationToken)
    {
        var claves = await ClavesAsync(forzarDescarga, cancellationToken);
        var parametros = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Emisor,
            ValidateAudience = true,
            ValidAudience = audiencia,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = claves,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(5),
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parametros);
    }

    private async Task<IReadOnlyList<SecurityKey>> ClavesAsync(bool forzarDescarga, CancellationToken cancellationToken)
    {
        if (!forzarDescarga && tiempo.GetUtcNow() < _vence)
        {
            return _claves;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            if (!forzarDescarga && tiempo.GetUtcNow() < _vence)
            {
                return _claves;
            }

            using var cliente = http.CreateClient(NombreCliente);
            using var respuesta = await cliente.GetAsync(opciones.Value.UrlCertificados, cancellationToken);
            respuesta.EnsureSuccessStatusCode();
            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
            var claves = new List<SecurityKey>();
            foreach (var propiedad in documento.RootElement.EnumerateObject())
            {
                if (propiedad.Value.ValueKind == JsonValueKind.String)
                {
                    var certificado = X509Certificate2.CreateFromPem(propiedad.Value.GetString());
                    claves.Add(new X509SecurityKey(certificado, propiedad.Name));
                }
            }

            Descargas++;
            _claves = claves;
            _vence = tiempo.GetUtcNow() + (respuesta.Headers.CacheControl?.MaxAge ?? CachePorDefecto);
            return _claves;
        }
        finally
        {
            _candado.Release();
        }
    }
}
