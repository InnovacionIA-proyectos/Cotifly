using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;

namespace Cotifly.Api.Comun;

/// <summary>
/// Marca un controller (o acción) cuyo JSON de entrada y de salida va en snake_case: todo <c>/web/*</c> (ADR-002).
/// Lo leen <see cref="clsFormateadorSalidaWeb"/> y <see cref="clsFormateadorEntradaWeb"/> desde los metadatos del
/// endpoint; <c>/api/auth/*</c> y <c>/health</c> no lo llevan y siguen con la serialización por defecto de MVC.
/// Se hereda: basta con ponerlo en <see cref="clsControladorWebBase"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class clsJsonSnakeCaseAttribute : Attribute
{
}

/// <summary>
/// Convención JSON por grupo de endpoints (ADR-002). MVC tiene formateadores globales; aquí se agrega un par de
/// formateadores System.Text.Json con snake_case al principio de la lista, y cada uno solo acepta la petición si el endpoint
/// tiene <see cref="clsJsonSnakeCaseAttribute"/>. Si no, MVC sigue con los formateadores por defecto (camelCase o los nombres
/// explícitos de <c>JsonPropertyName</c>, como en <c>/api/auth/*</c>).
/// </summary>
public static class clsJsonWeb
{
    /// <summary>
    /// Opciones de <c>/web/*</c>: snake_case minúsculas en propiedades y claves de diccionario, lectura sin distinguir mayúsculas,
    /// números solo como números (<c>"0.2"</c> como texto es un cuerpo inválido), nulos siempre escritos (el contrato los declara
    /// <c>nullable</c>, no opcionales) y tildes sin escapar, igual que el resto de MVC.
    /// </summary>
    public static JsonSerializerOptions Opciones { get; } = CrearOpciones();

    /// <summary>¿El endpoint de la petición pertenece al grupo snake_case?</summary>
    public static bool EsGrupoWeb(HttpContext? contexto) =>
        contexto?.GetEndpoint()?.Metadata.GetMetadata<clsJsonSnakeCaseAttribute>() is not null;

    /// <summary>Registra los formateadores del grupo <c>/web/*</c> en MVC.</summary>
    public static IMvcBuilder AddConvencionJsonWeb(this IMvcBuilder mvc)
    {
        ArgumentNullException.ThrowIfNull(mvc);
        mvc.Services.AddSingleton<IConfigureOptions<MvcOptions>, clsConfigurarFormateadoresWeb>();
        return mvc;
    }

    /// <summary>Aplica la política snake_case de <c>/web/*</c> a unas opciones existentes (las del formateador de entrada).</summary>
    internal static void Aplicar(JsonSerializerOptions opciones)
    {
        opciones.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        opciones.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
        opciones.PropertyNameCaseInsensitive = true;
        opciones.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
        opciones.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
        opciones.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    }

    private static JsonSerializerOptions CrearOpciones()
    {
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Aplicar(opciones);
        opciones.MakeReadOnly(populateMissingResolver: true);
        return opciones;
    }

    /// <summary>Inserta los formateadores de <c>/web/*</c> antes que los de MVC.</summary>
    private sealed class clsConfigurarFormateadoresWeb(ILoggerFactory registros) : IConfigureOptions<MvcOptions>
    {
        public void Configure(MvcOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.InputFormatters.Insert(0, new clsFormateadorEntradaWeb(registros.CreateLogger<SystemTextJsonInputFormatter>()));
            options.OutputFormatters.Insert(0, new clsFormateadorSalidaWeb());
        }
    }
}

/// <summary>Salida JSON snake_case, solo para endpoints con <see cref="clsJsonSnakeCaseAttribute"/>.</summary>
public sealed class clsFormateadorSalidaWeb() : SystemTextJsonOutputFormatter(clsJsonWeb.Opciones)
{
    public override bool CanWriteResult(OutputFormatterCanWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return clsJsonWeb.EsGrupoWeb(context.HttpContext) && base.CanWriteResult(context);
    }
}

/// <summary>Entrada JSON snake_case, solo para endpoints con <see cref="clsJsonSnakeCaseAttribute"/>.</summary>
public sealed class clsFormateadorEntradaWeb : SystemTextJsonInputFormatter
{
    public clsFormateadorEntradaWeb(ILogger<SystemTextJsonInputFormatter> registro)
        : base(CrearOpcionesMvc(), registro)
    {
    }

    public override bool CanRead(InputFormatterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return clsJsonWeb.EsGrupoWeb(context.HttpContext) && base.CanRead(context);
    }

    private static JsonOptions CrearOpcionesMvc()
    {
        var opciones = new JsonOptions { AllowInputFormatterExceptionMessages = false };
        clsJsonWeb.Aplicar(opciones.JsonSerializerOptions);
        return opciones;
    }
}
