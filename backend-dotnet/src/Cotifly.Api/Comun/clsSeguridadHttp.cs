using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Cotifly.Api.Comun;

/// <summary>
/// CORS del frontend Angular de ClientePro (sección <c>Cors</c>, contrato §6). Orígenes exactos
/// (<c>https://host[:puerto]</c>, sin ruta ni comodines); métodos GET/POST/PUT; encabezados <c>Authorization</c> y
/// <c>Content-Type</c>; sin cookies ni credenciales. En Production la lista es obligatoria.
/// </summary>
public sealed class clsOpcionesCors
{
    public const string Seccion = "Cors";

    /// <summary><c>Cors__OrigenesPermitidos__0</c>, <c>__1</c>...</summary>
    public List<string> OrigenesPermitidos { get; set; } = [];
}

/// <summary>
/// Encabezados reenviados por el proxy (sección <c>ForwardedHeaders</c>, Traefik en Swarm, HU-006): con
/// <see cref="Habilitado"/> la API toma la IP real del cliente de <c>X-Forwarded-For</c> (la usa el límite de peticiones) y el
/// esquema de <c>X-Forwarded-Proto</c>, pero solo si la conexión viene de un proxy o una red conocidos.
/// </summary>
public sealed class clsOpcionesEncabezadosReenviados
{
    public const string Seccion = "ForwardedHeaders";

    /// <summary>Por defecto <c>false</c>: sin proxy delante (local) la IP es la de la conexión.</summary>
    public bool Habilitado { get; set; }

    /// <summary>IPs exactas del proxy (<c>ForwardedHeaders__ProxiesConocidos__0=10.0.1.5</c>).</summary>
    public List<string> ProxiesConocidos { get; set; } = [];

    /// <summary>Redes del proxy en CIDR (<c>ForwardedHeaders__RedesConocidas__0=10.0.0.0/8</c>, la red overlay de Swarm).</summary>
    public List<string> RedesConocidas { get; set; } = [];

    /// <summary>Saltos de proxy que se aceptan en <c>X-Forwarded-For</c> (por defecto 1: solo Traefik).</summary>
    [Range(1, 10)]
    public int LimiteSaltos { get; set; } = 1;
}

/// <summary>Límites HTTP propios de la Api (sección <c>SeguridadHttp</c>).</summary>
public sealed class clsOpcionesSeguridadHttp
{
    public const string Seccion = "SeguridadHttp";

    /// <summary>Tamaño máximo del cuerpo de una petición a <c>/web/*</c> (por defecto 64 KB; 413 al pasarlo).</summary>
    [Range(1024, 10 * 1024 * 1024)]
    public long LimiteCuerpoWebBytes { get; set; } = 64 * 1024;
}

/// <summary>Valida <see cref="clsOpcionesCors"/>: orígenes bien formados y, en Production, al menos uno.</summary>
public sealed class clsValidadorOpcionesCors(IHostEnvironment entorno) : IValidateOptions<clsOpcionesCors>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesCors options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        if (entorno.IsProduction() && options.OrigenesPermitidos.Count == 0)
        {
            errores.Add("Cors:OrigenesPermitidos es obligatorio en Production (dominio del frontend de ClientePro).");
        }

        for (var i = 0; i < options.OrigenesPermitidos.Count; i++)
        {
            if (!EsOrigenValido(options.OrigenesPermitidos[i]))
            {
                errores.Add($"Cors:OrigenesPermitidos:{i} debe ser un origen http(s) exacto, sin ruta ni comodines (ej. https://clientepro.boterosoto.com.co).");
            }
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    internal static bool EsOrigenValido(string? origen) =>
        !string.IsNullOrWhiteSpace(origen)
        && !origen.Contains('*', StringComparison.Ordinal)
        && Uri.TryCreate(origen.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && !origen.Trim().EndsWith('/')
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}

/// <summary>Valida <see cref="clsOpcionesEncabezadosReenviados"/>: IPs y CIDR válidos y, si está habilitado, al menos uno.</summary>
public sealed class clsValidadorOpcionesEncabezadosReenviados : IValidateOptions<clsOpcionesEncabezadosReenviados>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesEncabezadosReenviados options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        for (var i = 0; i < options.ProxiesConocidos.Count; i++)
        {
            if (!IPAddress.TryParse(options.ProxiesConocidos[i]?.Trim(), out _))
            {
                errores.Add($"ForwardedHeaders:ProxiesConocidos:{i} no es una dirección IP.");
            }
        }

        for (var i = 0; i < options.RedesConocidas.Count; i++)
        {
            if (!System.Net.IPNetwork.TryParse(options.RedesConocidas[i]?.Trim(), out _))
            {
                errores.Add($"ForwardedHeaders:RedesConocidas:{i} no es una red CIDR (ej. 10.0.0.0/8).");
            }
        }

        if (options.Habilitado && options.ProxiesConocidos.Count == 0 && options.RedesConocidas.Count == 0)
        {
            errores.Add("ForwardedHeaders:Habilitado exige ProxiesConocidos o RedesConocidas: sin ellos cualquiera podría falsear su IP.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}

/// <summary>
/// Endurecimiento HTTP de la Api (#12 parte B): CORS por configuración, encabezados reenviados de Traefik, encabezados de
/// seguridad en todas las respuestas y límite del cuerpo en <c>/web/*</c>.
/// </summary>
public static class clsSeguridadHttp
{
    public const string PoliticaCors = "clientepro";

    /// <summary>Métodos que admite CORS (los del contrato).</summary>
    public static readonly string[] MetodosCors = ["GET", "POST", "PUT"];

    /// <summary>Encabezados que admite CORS.</summary>
    public static readonly string[] EncabezadosCors = ["Authorization", "Content-Type"];

    /// <summary>Tiempo que el navegador puede cachear la respuesta del preflight.</summary>
    public static readonly TimeSpan CachePreflight = TimeSpan.FromMinutes(10);

    public const string MensajeCuerpoDemasiadoGrande = "El cuerpo de la petición es demasiado grande.";

    public static IServiceCollection AddSeguridadHttp(this IServiceCollection servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);

        servicios.AddOptions<clsOpcionesCors>().BindConfiguration(clsOpcionesCors.Seccion).ValidateOnStart();
        servicios.AddSingleton<IValidateOptions<clsOpcionesCors>, clsValidadorOpcionesCors>();
        servicios.AddOptions<clsOpcionesEncabezadosReenviados>()
            .BindConfiguration(clsOpcionesEncabezadosReenviados.Seccion)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        servicios.AddSingleton<IValidateOptions<clsOpcionesEncabezadosReenviados>, clsValidadorOpcionesEncabezadosReenviados>();
        servicios.AddOptions<clsOpcionesSeguridadHttp>()
            .BindConfiguration(clsOpcionesSeguridadHttp.Seccion)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        servicios.AddCors();
        servicios.AddOptions<CorsOptions>().Configure<IOptions<clsOpcionesCors>>((cors, opciones) =>
            cors.AddPolicy(PoliticaCors, politica => politica
                .WithOrigins([.. opciones.Value.OrigenesPermitidos.Select(o => o.Trim())])
                .WithMethods(MetodosCors)
                .WithHeaders(EncabezadosCors)
                .DisallowCredentials()
                .SetPreflightMaxAge(CachePreflight)));

        servicios.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<clsOpcionesEncabezadosReenviados>>((reenvio, opciones) =>
        {
            var valor = opciones.Value;

            // Deshabilitado: el middleware queda en la tubería pero no procesa ningún encabezado.
            reenvio.ForwardedHeaders = valor.Habilitado ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto : ForwardedHeaders.None;
            reenvio.ForwardLimit = valor.LimiteSaltos;
            reenvio.KnownProxies.Clear();
            reenvio.KnownIPNetworks.Clear();
            foreach (var proxy in valor.ProxiesConocidos)
            {
                reenvio.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
            }

            foreach (var red in valor.RedesConocidas)
            {
                reenvio.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(red.Trim()));
            }
        });

        return servicios;
    }

    /// <summary>
    /// Middleware en este orden: encabezados reenviados (antes que todo lo que lee la IP; sin efecto si no están habilitados),
    /// encabezados de seguridad, CORS (antes de la autenticación, porque el preflight no lleva token, y antes del límite, para
    /// que el frontend pueda leer un 413) y límite del cuerpo de <c>/web/*</c>. Las opciones se leen por petición: construir la
    /// tubería no debe validar configuración (eso ocurre al arrancar).
    /// </summary>
    public static WebApplication UseSeguridadHttp(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();
        app.Use(AgregarEncabezadosSeguridad);
        app.UseCors(PoliticaCors);
        app.UseWhen(
            contexto => contexto.Request.Path.StartsWithSegments("/web", StringComparison.OrdinalIgnoreCase),
            rama => rama.Use(LimitarCuerpoAsync));
        return app;
    }

    /// <summary>
    /// Encabezados de seguridad de una API JSON: sin sniffing de tipo, sin marcos, sin referer y CSP que no permite cargar nada.
    /// <c>/web/*</c> y <c>/pdfs/*</c> además no se guardan en caché (datos de clientes).
    /// </summary>
    private static Task AgregarEncabezadosSeguridad(HttpContext contexto, RequestDelegate siguiente)
    {
        var encabezados = contexto.Response.Headers;
        encabezados.XContentTypeOptions = "nosniff";
        encabezados.XFrameOptions = "DENY";
        encabezados["Referrer-Policy"] = "no-referrer";
        encabezados.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        var ruta = contexto.Request.Path;
        if (ruta.StartsWithSegments("/web", StringComparison.OrdinalIgnoreCase) || ruta.StartsWithSegments("/pdfs", StringComparison.OrdinalIgnoreCase))
        {
            encabezados.CacheControl = "no-store";
        }

        return siguiente(contexto);
    }

    /// <summary>
    /// 413 <c>ErrorWeb</c> si <c>Content-Length</c> supera el límite; si no lo declara (chunked), Kestrel corta la lectura al
    /// pasar el mismo límite (<see cref="IHttpMaxRequestBodySizeFeature"/>).
    /// </summary>
    private static async Task LimitarCuerpoAsync(HttpContext contexto, RequestDelegate siguiente)
    {
        var limite = contexto.RequestServices.GetRequiredService<IOptions<clsOpcionesSeguridadHttp>>().Value.LimiteCuerpoWebBytes;
        if (contexto.Request.ContentLength > limite)
        {
            contexto.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await contexto.Response.WriteAsJsonAsync(new clsErrorWeb(MensajeCuerpoDemasiadoGrande), contexto.RequestAborted);
            return;
        }

        if (contexto.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } tamano)
        {
            tamano.MaxRequestBodySize = limite;
        }

        await siguiente(contexto);
    }
}
