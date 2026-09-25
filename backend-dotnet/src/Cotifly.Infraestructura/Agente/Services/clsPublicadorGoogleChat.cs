using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Infraestructura.Agente.Services;

/// <summary>
/// Token OAuth de la cuenta de servicio de la app de Chat (<c>GChat:SaJson</c>) con la concesión JWT-bearer de Google (RFC 7523):
/// una aserción RS256 firmada con la clave privada de la cuenta (<c>iss</c> = <c>client_email</c>, <c>aud</c> = <c>token_uri</c>,
/// <c>scope</c> = <c>chat.bot</c>) se canjea en <c>token_uri</c>. El token se reutiliza hasta un minuto antes de vencer.
/// </summary>
/// <remarks>
/// Se evaluó <c>Google.Apis.Auth</c> (Apache-2.0): arrastra Newtonsoft.Json y System.Management y su <c>GoogleCredential.FromJson</c>
/// está obsoleta. El flujo es corto y se hace con <c>Microsoft.IdentityModel.JsonWebTokens</c>, que el proyecto ya usa.
/// </remarks>
public sealed class clsTokenCuentaServicioGoogle(IHttpClientFactory http, IOptions<clsOpcionesGoogleChat> opciones, TimeProvider tiempo)
{
    public const string AlcanceChat = "https://www.googleapis.com/auth/chat.bot";
    public const string TokenUriPorDefecto = "https://oauth2.googleapis.com/token";

    private readonly SemaphoreSlim _candado = new(1, 1);
    private string? _token;
    private DateTimeOffset _vence = DateTimeOffset.MinValue;

    public async Task<string> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && tiempo.GetUtcNow() < _vence)
        {
            return _token;
        }

        await _candado.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && tiempo.GetUtcNow() < _vence)
            {
                return _token;
            }

            var cuenta = LeerCuenta(opciones.Value.LeerCuentaServicio());
            var ahora = tiempo.GetUtcNow();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(cuenta.ClavePrivada);
            var clave = new RsaSecurityKey(rsa) { KeyId = cuenta.IdClave, CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false } };
            var asercion = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = cuenta.Correo,
                Audience = cuenta.TokenUri,
                IssuedAt = ahora.UtcDateTime,
                NotBefore = ahora.UtcDateTime,
                Expires = ahora.AddHours(1).UtcDateTime,
                Subject = new ClaimsIdentity([new Claim("scope", AlcanceChat)]),
                SigningCredentials = new SigningCredentials(clave, SecurityAlgorithms.RsaSha256),
            });

            using var cliente = http.CreateClient(clsVerificadorJwtGoogleChat.NombreCliente);
            using var cuerpo = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = asercion,
            });
            using var respuesta = await cliente.PostAsync(cuenta.TokenUri, cuerpo, cancellationToken);
            respuesta.EnsureSuccessStatusCode();
            using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(cancellationToken));
            var token = documento.RootElement.GetProperty("access_token").GetString()
                        ?? throw new InvalidOperationException("Google no devolvió access_token.");
            var segundos = documento.RootElement.TryGetProperty("expires_in", out var vence) && vence.TryGetInt32(out var s) ? s : 3600;
            _token = token;
            _vence = ahora.AddSeconds(Math.Max(0, segundos - 60));
            return token;
        }
        finally
        {
            _candado.Release();
        }
    }

    private static (string Correo, string ClavePrivada, string? IdClave, string TokenUri) LeerCuenta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("GChat:SaJson no está configurado.");
        }

        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        string? Texto(string nombre) => raiz.TryGetProperty(nombre, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;
        return (
            Texto("client_email") ?? throw new InvalidOperationException("GChat:SaJson no trae client_email."),
            Texto("private_key") ?? throw new InvalidOperationException("GChat:SaJson no trae private_key."),
            Texto("private_key_id"),
            Texto("token_uri") ?? TokenUriPorDefecto);
    }
}

/// <summary>
/// Publica en Google Chat con la API REST (port de <c>construir_publicador</c> de <c>orquestador/publicador.py</c>):
/// <c>POST {GChat:ApiBase}/{space}/messages?messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD</c> con el token de la cuenta de
/// servicio y el hilo en <c>thread.name</c>. Sin <c>GChat:SaJson</c> no publica (warning). Un error HTTP se propaga
/// (<see cref="HttpRequestException"/> con el estado).
/// </summary>
public sealed partial class clsPublicadorGoogleChat(
    IHttpClientFactory http,
    clsTokenCuentaServicioGoogle token,
    IOptions<clsOpcionesGoogleChat> opciones,
    ILogger<clsPublicadorGoogleChat> logger) : IClsPublicadorChat
{
    public const string OpcionRespuesta = "REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD";

    public async Task PublicarAsync(string spaceId, string? threadId, JsonObject mensaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mensaje);
        if (string.IsNullOrWhiteSpace(spaceId) || !ExpresionSpace().IsMatch(spaceId))
        {
            throw new ArgumentException("spaceId debe tener la forma spaces/XXXX.", nameof(spaceId));
        }

        if (string.IsNullOrWhiteSpace(opciones.Value.SaJson))
        {
            logger.LogWarning("Google Chat: GChat:SaJson no está configurado; no se publica la respuesta en {Space}.", spaceId);
            return;
        }

        var cuerpo = mensaje.DeepClone().AsObject();
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            cuerpo["thread"] = new JsonObject { ["name"] = threadId };
        }

        var url = $"{opciones.Value.ApiBase.TrimEnd('/')}/{spaceId}/messages?messageReplyOption={OpcionRespuesta}";
        using var peticion = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(cuerpo) };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token.ObtenerAsync(cancellationToken));
        using var cliente = http.CreateClient(clsVerificadorJwtGoogleChat.NombreCliente);
        using var respuesta = await cliente.SendAsync(peticion, cancellationToken);
        if (!respuesta.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"La API de Google Chat respondió {(int)respuesta.StatusCode}.", null, respuesta.StatusCode);
        }
    }

    [GeneratedRegex(@"^spaces/[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionSpace();
}
