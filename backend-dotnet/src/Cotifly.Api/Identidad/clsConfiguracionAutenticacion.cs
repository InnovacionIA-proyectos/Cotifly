using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Cotifly.Api.Identidad;

/// <summary>Políticas de autorización por rol interno (claim <c>rol</c>), para los endpoints de <c>/web/*</c>.</summary>
public static class clsPoliticas
{
    /// <summary>
    /// Consola del comercial: <c>comercial</c>, <c>gerente_comercial</c> o <c>director</c> (el gerente y el director también
    /// cotizan).
    /// </summary>
    public const string Comercial = "Comercial";

    /// <summary>
    /// Administración de márgenes, tablero y listado de la regional: <c>gerente_comercial</c> o <c>director</c> (el director usa
    /// todo lo del gerente; su alcance de regionales lo decide el caso de uso).
    /// </summary>
    public const string GerenteComercial = "GerenteComercial";

    /// <summary>Roles (claim <c>rol</c>) de la política <see cref="Comercial"/>.</summary>
    internal static readonly string[] RolesComercial = ["comercial", "gerente_comercial", "director"];

    /// <summary>Roles (claim <c>rol</c>) de la política <see cref="GerenteComercial"/>.</summary>
    internal static readonly string[] RolesGerencial = ["gerente_comercial", "director"];
}

/// <summary>Límite de peticiones de <c>/api/auth/*</c> (sección <c>LimiteAuth</c>): ventana fija por IP.</summary>
public sealed class clsOpcionesLimiteAuth
{
    public const string Seccion = "LimiteAuth";

    /// <summary>Peticiones permitidas por IP en cada ventana. Por defecto 30.</summary>
    [Range(1, 100_000)]
    public int PeticionesPorVentana { get; set; } = 30;

    /// <summary>Duración de la ventana en segundos. Por defecto 60.</summary>
    [Range(1, 3600)]
    public int VentanaSegundos { get; set; } = 60;
}

/// <summary>
/// Límite de peticiones de <c>POST /web/chat</c> (sección <c>LimiteChat</c>): ventana fija por comercial (claim <c>sub</c>). Es el
/// 429 de la propia API; el límite del proveedor de IA responde 200 con <c>error: true</c> (contrato).
/// </summary>
public sealed class clsOpcionesLimiteChat
{
    public const string Seccion = "LimiteChat";

    /// <summary>Turnos permitidos por comercial en cada ventana. Por defecto 20.</summary>
    [Range(1, 100_000)]
    public int PeticionesPorVentana { get; set; } = 20;

    /// <summary>Duración de la ventana en segundos. Por defecto 60.</summary>
    [Range(1, 3600)]
    public int VentanaSegundos { get; set; } = 60;
}

/// <summary>
/// Autenticación y autorización de la Api (ADR-009): <c>JwtBearer</c> sobre el access HS256 que emite
/// <c>/api/auth/login-clientepro</c>, revocación por <c>jti</c>, respuestas <c>ErrorWeb</c> en 401/403/429, políticas por
/// rol, <see cref="IClsContextoUsuario"/> desde los claims y límite de peticiones de <c>/api/auth/*</c>.
/// </summary>
public static class clsConfiguracionAutenticacion
{
    public const string PoliticaLimiteAuth = "auth";
    public const string PoliticaLimiteChat = "chat";

    /// <summary>Tolerancia de reloj al validar <c>exp</c>/<c>nbf</c>.</summary>
    public static readonly TimeSpan ToleranciaReloj = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddAutenticacionCotifly(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        servicios.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        servicios.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IClsConfiguracionJwt>(Configurar);

        servicios.AddAuthorizationBuilder()
            .AddPolicy(clsPoliticas.Comercial, p => p.RequireAuthenticatedUser().RequireRole(clsPoliticas.RolesComercial))
            .AddPolicy(clsPoliticas.GerenteComercial, p => p.RequireAuthenticatedUser().RequireRole(clsPoliticas.RolesGerencial));

        servicios.AddHttpContextAccessor();
        servicios.AddScoped<IClsContextoUsuario, clsContextoUsuarioHttp>();

        servicios.AddOptions<clsOpcionesLimiteAuth>()
            .BindConfiguration(clsOpcionesLimiteAuth.Seccion)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        servicios.AddOptions<clsOpcionesLimiteChat>()
            .BindConfiguration(clsOpcionesLimiteChat.Seccion)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        servicios.AddRateLimiter(ConfigurarLimite);

        return servicios;
    }

    /// <summary>Parámetros de validación del access token y eventos (revocación, 401 y 403 con <c>ErrorWeb</c>).</summary>
    internal static void Configurar(JwtBearerOptions opciones, IClsConfiguracionJwt configuracion)
    {
        opciones.MapInboundClaims = false;
        opciones.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuracion.Emisor,
            ValidateAudience = true,
            ValidAudience = configuracion.Audiencia,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            // Sin caché de proveedores de firma: la caché global de Microsoft.IdentityModel desecha proveedores en uso cuando
            // muchas claves conviven (varias instancias en un proceso) y la validación falla de forma intermitente (401).
            IssuerSigningKey = new SymmetricSecurityKey(configuracion.ObtenerClaveFirma())
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            },
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = ToleranciaReloj,
            NameClaimType = clsClaimsCotifly.Username,
            RoleClaimType = clsClaimsCotifly.Rol,
        };
        opciones.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidarRevocacionAsync,
            OnAuthenticationFailed = contexto =>
            {
                // Solo el tipo y el mensaje (IdentityModel oculta los datos personales): diagnóstico de 401 inesperados.
                contexto.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(clsConfiguracionAutenticacion).FullName!)
                    .LogInformation("Access token rechazado ({Tipo}): {Mensaje}", contexto.Exception.GetType().Name, contexto.Exception.Message);
                return Task.CompletedTask;
            },
            OnChallenge = async contexto =>
            {
                contexto.HandleResponse();
                contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
                contexto.Response.Headers.WWWAuthenticate = "Bearer";
                await contexto.Response.WriteAsJsonAsync(new clsErrorWeb(clsCodigosAcceso.MensajeSesionExpirada));
            },
            OnForbidden = async contexto =>
            {
                contexto.Response.StatusCode = StatusCodes.Status403Forbidden;
                await contexto.Response.WriteAsJsonAsync(new clsErrorWeb(clsErrorWeb.MensajeSinPermiso));
            },
        };
    }

    /// <summary>
    /// Después de validar firma, emisor, audiencia y vigencia: exige <c>sub</c> (UUID) y <c>jti</c>, y rechaza el token si
    /// su <c>jti</c> fue revocado en un logout (<see cref="IClsRevocacionAccesos"/>, memoria con foto de 60 s).
    /// </summary>
    internal static async Task ValidarRevocacionAsync(TokenValidatedContext contexto)
    {
        var jti = contexto.Principal?.FindFirst(clsClaimsCotifly.Jti)?.Value;
        var sub = contexto.Principal?.FindFirst(clsClaimsCotifly.Sub)?.Value;
        if (string.IsNullOrEmpty(jti) || !Guid.TryParse(sub, out _))
        {
            contexto.Fail("El token no trae sub o jti válidos.");
            return;
        }

        var revocacion = contexto.HttpContext.RequestServices.GetRequiredService<IClsRevocacionAccesos>();
        if (await revocacion.EstaRevocadoAsync(jti, contexto.HttpContext.RequestAborted))
        {
            contexto.Fail("Token revocado.");
        }
    }

    private static void ConfigurarLimite(RateLimiterOptions opciones)
    {
        opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        opciones.OnRejected = async (contexto, cancellationToken) =>
        {
            if (contexto.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
            {
                contexto.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(espera.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await contexto.HttpContext.Response.WriteAsJsonAsync(new clsErrorWeb(clsErrorWeb.MensajeDemasiadasPeticiones), cancellationToken);
        };
        opciones.AddPolicy(PoliticaLimiteAuth, http =>
        {
            var limite = http.RequestServices.GetRequiredService<IOptions<clsOpcionesLimiteAuth>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limite.PeticionesPorVentana,
                    Window = TimeSpan.FromSeconds(limite.VentanaSegundos),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
        opciones.AddPolicy(PoliticaLimiteChat, http =>
        {
            var limite = http.RequestServices.GetRequiredService<IOptions<clsOpcionesLimiteChat>>().Value;
            var clave = http.User.FindFirst(clsClaimsCotifly.Sub)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "sin-ip";
            return RateLimitPartition.GetFixedWindowLimiter(
                "chat:" + clave,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limite.PeticionesPorVentana,
                    Window = TimeSpan.FromSeconds(limite.VentanaSegundos),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        });
    }
}
