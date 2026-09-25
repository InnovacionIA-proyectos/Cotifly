using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Google falso para las pruebas de Google Chat, sin red (<see cref="HttpMessageHandler"/> del cliente <c>GoogleChat</c>):
/// <list type="bullet">
/// <item>Certificados de <c>chat@system.gserviceaccount.com</c>: los de la fixture <c>fixtures/chat/certs_chat_falsos.json</c> (firman
/// <c>jwt_valido.txt</c>) más uno generado en memoria para firmar tokens a medida (<see cref="FirmarToken"/>).</item>
/// <item>Canje OAuth de la cuenta de servicio (<c>/token</c>) y la API de Chat (<c>/v1/spaces/.../messages</c>), que registran cada
/// petición en <see cref="Peticiones"/>.</item>
/// </list>
/// </summary>
public sealed class clsGoogleFalso : HttpMessageHandler
{
    public const string ProjectNumber = "123456789012";
    public const string UrlCertificados = "https://google-falso.test/certs";
    public const string UrlToken = "https://google-falso.test/token";
    public const string ApiBase = "https://google-falso.test/v1";
    public const string TokenAcceso = "ya29.token-falso";
    public const string KidMemoria = "memoria-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly X509Certificate2 _certificado;

    public clsGoogleFalso()
    {
        var solicitud = new CertificateRequest("CN=chat-memoria.cotifly.test", _rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _certificado = solicitud.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        ClaveCuentaServicio = RSA.Create(2048);
    }

    /// <summary>Peticiones recibidas (método, URL, encabezado Authorization y cuerpo).</summary>
    public ConcurrentQueue<clsPeticionFalsa> Peticiones { get; } = new();

    /// <summary>Estado que responde la API de Chat (200 por defecto).</summary>
    public HttpStatusCode EstadoChat { get; set; } = HttpStatusCode.OK;

    /// <summary>Clave de la cuenta de servicio falsa (su JSON en <see cref="CuentaServicioJson"/>).</summary>
    public RSA ClaveCuentaServicio { get; }

    public static string Fixture(string archivo) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "chat", archivo)).Trim();

    /// <summary>JSON de una cuenta de servicio falsa con <see cref="UrlToken"/> como <c>token_uri</c>.</summary>
    public string CuentaServicioJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["client_email"] = "cotifly-bot@proyecto-falso.iam.gserviceaccount.com",
        ["private_key_id"] = "clave-sa-1",
        ["private_key"] = ClaveCuentaServicio.ExportPkcs8PrivateKeyPem(),
        ["token_uri"] = UrlToken,
    });

    /// <summary>Configuración de <c>GChat</c> que apunta a este Google falso.</summary>
    public Dictionary<string, string?> Configuracion(bool conCuentaServicio = true) => new()
    {
        ["GChat:ProjectNumber"] = ProjectNumber,
        ["GChat:UrlCertificados"] = UrlCertificados,
        ["GChat:ApiBase"] = ApiBase,
        ["GChat:SaJson"] = conCuentaServicio ? CuentaServicioJson() : null,
    };

    /// <summary>JWT RS256 firmado con el certificado en memoria (se puede alterar emisor, audiencia y vigencia).</summary>
    public string FirmarToken(string emisor = "chat@system.gserviceaccount.com", string audiencia = ProjectNumber, TimeSpan? venceEn = null)
    {
        var ahora = DateTime.UtcNow;
        var vence = ahora + (venceEn ?? TimeSpan.FromHours(1));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = emisor,
            Audience = audiencia,
            IssuedAt = vence < ahora ? vence.AddHours(-1) : ahora,
            NotBefore = vence < ahora ? vence.AddHours(-1) : ahora,
            Expires = vence,
            Subject = new ClaimsIdentity([new Claim("email", emisor)]),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KidMemoria }, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Peticiones.Enqueue(new clsPeticionFalsa(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), cuerpo));
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);

        if (url == UrlCertificados)
        {
            var certificados = JsonSerializer.Deserialize<Dictionary<string, string>>(Fixture("certs_chat_falsos.json"))!;
            certificados[KidMemoria] = _certificado.ExportCertificatePem();
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(certificados));
        }

        if (url == UrlToken)
        {
            return Json(HttpStatusCode.OK, $$"""{"access_token":"{{TokenAcceso}}","expires_in":3599,"token_type":"Bearer"}""");
        }

        if (url.StartsWith(ApiBase + "/spaces/", StringComparison.Ordinal))
        {
            return Json(EstadoChat, """{"name":"spaces/X/messages/1"}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _certificado.Dispose();
            _rsa.Dispose();
            ClaveCuentaServicio.Dispose();
        }

        base.Dispose(disposing);
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Petición recibida por <see cref="clsGoogleFalso"/>.</summary>
public sealed record clsPeticionFalsa(HttpMethod Metodo, Uri Url, string? Autorizacion, string? Cuerpo);
