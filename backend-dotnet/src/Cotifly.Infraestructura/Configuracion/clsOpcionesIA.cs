using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Proveedor de IA del agente (sección <c>IA</c>, ADR-003): cambiar de proveedor es un cambio de configuración. Variables
/// <c>IA__Proveedor</c>, <c>IA__Modelo</c>, <c>IA__ApiKey</c>, <c>IA__Endpoint</c>, <c>IA__Rpm</c>, <c>IA__TimeoutSeg</c> y
/// <c>IA__Fallbacks__0__Proveedor</c>... Los límites del loop (<c>IA:MaxTokens</c>, <c>IA:MaxIteraciones</c>) los lee
/// <c>clsOpcionesAgente</c> de Aplicación en la misma sección.
/// </summary>
/// <remarks>
/// Proveedores: <c>openai</c>, <c>azure_openai</c> (API v1 de Azure OpenAI, <c>{Endpoint}/openai/v1/</c>), <c>openai_compatible</c>
/// (OpenRouter, vLLM, LM Studio... con <c>Endpoint</c>), <c>gemini</c> (endpoint compatible con OpenAI de Google), <c>ollama</c>
/// (endpoint compatible <c>/v1</c>) y <c>anthropic</c> (SDK oficial). Sin <see cref="Proveedor"/> el agente no está configurado:
/// cada turno responde el error controlado y la API arranca igual (desarrollo y pruebas sin red).
/// </remarks>
public sealed class clsOpcionesIA
{
    public const string Seccion = "IA";

    public const string OpenAi = "openai";
    public const string AzureOpenAi = "azure_openai";
    public const string OpenAiCompatible = "openai_compatible";
    public const string Gemini = "gemini";
    public const string Ollama = "ollama";
    public const string Anthropic = "anthropic";

    public static readonly IReadOnlySet<string> Proveedores =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OpenAi, AzureOpenAi, OpenAiCompatible, Gemini, Ollama, Anthropic };

    /// <summary>Proveedores que exigen clave (los locales y los compatibles pueden no tenerla).</summary>
    public static readonly IReadOnlySet<string> ConClaveObligatoria =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OpenAi, AzureOpenAi, Gemini, Anthropic };

    /// <summary>Proveedor principal; vacío = agente sin configurar.</summary>
    [MaxLength(40)]
    public string? Proveedor { get; set; }

    /// <summary>Modelo (o deployment de Azure). Obligatorio con proveedor.</summary>
    [MaxLength(200)]
    public string? Modelo { get; set; }

    /// <summary>Clave del proveedor principal (secreto). Obligatoria para openai, azure_openai, gemini y anthropic.</summary>
    [clsSecreto(LargoMinimo = 16, Opcional = true)]
    public string? ApiKey { get; set; }

    /// <summary>URL base del proveedor. Obligatoria para azure_openai y openai_compatible; opcional para el resto.</summary>
    [MaxLength(500)]
    public string? Endpoint { get; set; }

    /// <summary>
    /// Peticiones por minuto al proveedor (limitador local, evita el 429 de las capas gratuitas); 0 lo desactiva. Por defecto 8. Ventana
    /// deslizante de 60 s (HU-013.16): las llamadas de un turno no se espacian mientras el minuto tenga cupo.
    /// </summary>
    [Range(0, 100_000)]
    public int Rpm { get; set; } = 8;

    /// <summary>Tiempo máximo de cada llamada al proveedor, en segundos. Por defecto 60.</summary>
    [Range(5, 600)]
    public int TimeoutSeg { get; set; } = 60;

    /// <summary>Proveedores de respaldo, en orden: se prueban ante un 429, un 5xx o un fallo de red del anterior.</summary>
    public List<clsOpcionesProveedorIA> Fallbacks { get; set; } = [];

    /// <summary>
    /// Respaldos con proveedor (#84): un respaldo con <c>Proveedor</c> vacío no existe. Así los manifiestos pueden declarar
    /// <c>IA__Fallbacks__0__*</c> con <c>${...:-}</c> y dejarlos vacíos cuando no hay respaldo.
    /// </summary>
    public IReadOnlyList<clsOpcionesProveedorIA> FallbacksConfigurados() =>
        Fallbacks.Where(f => !string.IsNullOrWhiteSpace(f.Proveedor)).ToList();

    /// <summary>El proveedor principal como <see cref="clsOpcionesProveedorIA"/>.</summary>
    public clsOpcionesProveedorIA Principal() => new() { Proveedor = Proveedor, Modelo = Modelo, ApiKey = ApiKey, Endpoint = Endpoint };
}

/// <summary>Un proveedor de IA (el principal o uno de <c>IA:Fallbacks</c>).</summary>
public sealed class clsOpcionesProveedorIA
{
    public string? Proveedor { get; set; }

    public string? Modelo { get; set; }

    public string? ApiKey { get; set; }

    public string? Endpoint { get; set; }
}

/// <summary>
/// Valida <see cref="clsOpcionesIA"/>: proveedor conocido, modelo, clave y endpoint según el proveedor (también en los respaldos) y,
/// en Production, las claves de los respaldos con la misma regla de secretos (<see cref="clsValidadorSecretos"/>).
/// </summary>
public sealed class clsValidadorOpcionesIA(IHostEnvironment entorno) : IValidateOptions<clsOpcionesIA>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesIA options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Proveedor))
        {
            if (options.FallbacksConfigurados().Count > 0)
            {
                errores.Add("IA:Fallbacks exige IA:Proveedor.");
            }

            return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
        }

        Validar("IA", options.Principal(), errores, validarSecreto: false);
        for (var i = 0; i < options.Fallbacks.Count; i++)
        {
            var respaldo = options.Fallbacks[i];
            if (string.IsNullOrWhiteSpace(respaldo.Proveedor))
            {
                // Vacío del todo = sin respaldo; con modelo, endpoint o clave pero sin proveedor es un error de configuración.
                if (!string.IsNullOrWhiteSpace(respaldo.Modelo) || !string.IsNullOrWhiteSpace(respaldo.Endpoint) || !string.IsNullOrWhiteSpace(respaldo.ApiKey))
                {
                    errores.Add($"IA:Fallbacks:{i}:Proveedor es obligatorio si el respaldo tiene modelo, endpoint o clave.");
                }

                continue;
            }

            Validar($"IA:Fallbacks:{i}", options.Fallbacks[i], errores, validarSecreto: true);
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    private void Validar(string prefijo, clsOpcionesProveedorIA proveedor, List<string> errores, bool validarSecreto)
    {
        var nombre = proveedor.Proveedor?.Trim();
        if (string.IsNullOrEmpty(nombre) || !clsOpcionesIA.Proveedores.Contains(nombre))
        {
            errores.Add($"{prefijo}:Proveedor debe ser uno de: {string.Join(", ", clsOpcionesIA.Proveedores.Order(StringComparer.Ordinal))}.");
            return;
        }

        if (string.IsNullOrWhiteSpace(proveedor.Modelo))
        {
            errores.Add($"{prefijo}:Modelo es obligatorio con el proveedor {nombre}.");
        }

        if (clsOpcionesIA.ConClaveObligatoria.Contains(nombre) && string.IsNullOrWhiteSpace(proveedor.ApiKey))
        {
            errores.Add($"{prefijo}:ApiKey es obligatoria con el proveedor {nombre}.");
        }
        else if (validarSecreto && !string.IsNullOrWhiteSpace(proveedor.ApiKey)
                 && clsValidadorSecretos.Validar($"{prefijo}:ApiKey", proveedor.ApiKey, 16, entorno.IsProduction()) is { } secreto)
        {
            errores.Add(secreto);
        }

        var exigeEndpoint = nombre.Equals(clsOpcionesIA.AzureOpenAi, StringComparison.OrdinalIgnoreCase)
                            || nombre.Equals(clsOpcionesIA.OpenAiCompatible, StringComparison.OrdinalIgnoreCase);
        if (exigeEndpoint && string.IsNullOrWhiteSpace(proveedor.Endpoint))
        {
            errores.Add($"{prefijo}:Endpoint es obligatorio con el proveedor {nombre}.");
        }
        else if (!string.IsNullOrWhiteSpace(proveedor.Endpoint)
                 && (!Uri.TryCreate(proveedor.Endpoint.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            errores.Add($"{prefijo}:Endpoint debe ser una URL http(s) absoluta.");
        }
    }
}
