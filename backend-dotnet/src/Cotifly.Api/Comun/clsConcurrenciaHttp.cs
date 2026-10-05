using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cotifly.Api.Comun;

/// <summary>
/// Concurrencia optimista por HTTP (ADR-018 §8): <c>ETag</c> = <c>Cotizaciones.rowVersion</c> en base64 entre comillas, y
/// <c>If-Match</c> con ese valor (con o sin comillas, también débil <c>W/"..."</c>). La comparación real la hace el caso de uso con
/// la fila bloqueada (<c>IClsUnitOfWork.GuardarCambiosSiVersionAsync</c>); aquí solo se lee y se escribe el encabezado.
/// </summary>
public static class clsConcurrenciaHttp
{
    public const string EncabezadoIfMatch = "If-Match";
    public const string EncabezadoEtag = "ETag";

    /// <summary>Clave de <see cref="HttpContext.Items"/> donde <see cref="clsRequiereIfMatchAttribute"/> deja la versión leída.</summary>
    public const string ClaveVersion = "cotifly.if-match";

    public const string MensajeSinIfMatch = "Recarga la cotización y vuelve a intentarlo (falta If-Match).";

    /// <summary>
    /// Versión del <c>If-Match</c>: <c>null</c> si el encabezado no viene o está vacío; un arreglo vacío si viene pero no es una versión
    /// (no es base64, es <c>*</c> o trae varias): nunca coincide con la fila y el caso de uso responde <c>CONFLICTO_CONCURRENCIA</c>.
    /// </summary>
    public static byte[]? LeerIfMatch(HttpRequest peticion)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        var valor = peticion.Headers[EncabezadoIfMatch].ToString().Trim();
        if (valor.Length == 0)
        {
            return null;
        }

        if (valor.StartsWith("W/", StringComparison.Ordinal))
        {
            valor = valor[2..].Trim();
        }

        valor = valor.Trim('"').Trim();
        if (valor.Length == 0 || valor.Length > 64 || valor.Contains(',', StringComparison.Ordinal))
        {
            return [];
        }

        var bytes = new byte[valor.Length];
        return Convert.TryFromBase64String(valor, bytes, out var escritos) ? bytes[..escritos] : [];
    }

    /// <summary>Valor del encabezado <c>ETag</c> para una versión en base64: entre comillas (ETag fuerte).</summary>
    public static string Etag(string rowVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowVersion);
        return $"\"{rowVersion}\"";
    }

    /// <summary>Escribe el <c>ETag</c> de la respuesta si hay versión.</summary>
    public static void EscribirEtag(HttpResponse respuesta, string? rowVersion)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        if (!string.IsNullOrWhiteSpace(rowVersion))
        {
            respuesta.Headers[EncabezadoEtag] = Etag(rowVersion);
        }
    }

    /// <summary>Versión que dejó <see cref="clsRequiereIfMatchAttribute"/> para la acción (siempre presente en una acción con el filtro).</summary>
    public static byte[] VersionDe(HttpContext contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        return contexto.Items.TryGetValue(ClaveVersion, out var valor) && valor is byte[] version
            ? version
            : throw new InvalidOperationException($"La acción no tiene [{nameof(clsRequiereIfMatchAttribute)}].");
    }

    /// <summary>428 <c>PRECONDICION_REQUERIDA</c> con <c>ErrorNegocio</c>.</summary>
    public static IActionResult PrecondicionRequerida() => new ObjectResult(
        clsErrorNegocioWeb.Desde(new clsErrorDominio(clsCodigosError.PrecondicionRequerida, MensajeSinIfMatch)))
    {
        StatusCode = StatusCodes.Status428PreconditionRequired,
    };
}

/// <summary>
/// Mutación de la etapa final con <c>If-Match</c> obligatorio (ADR-018 §8): sin el encabezado responde 428
/// <c>PRECONDICION_REQUERIDA</c> antes de ejecutar la acción (y antes de leer el cuerpo); si viene, deja la versión en
/// <see cref="HttpContext.Items"/> (<see cref="clsConcurrenciaHttp.VersionDe"/>). Lo usan el margen y los conexos (#98) y, con el
/// mismo patrón, conexos en Nexo, re-solicitar y textos (#99, #100, #101).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class clsRequiereIfMatchAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (clsConcurrenciaHttp.LeerIfMatch(context.HttpContext.Request) is { } version)
        {
            context.HttpContext.Items[clsConcurrenciaHttp.ClaveVersion] = version;
            return;
        }

        context.Result = clsConcurrenciaHttp.PrecondicionRequerida();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

/// <summary>
/// Publica el <c>ETag</c> en toda respuesta 2xx de <c>/web/*</c> cuyo cuerpo trae la versión de la cotización
/// (<see cref="IClsConVersionFila"/>, ej. <c>CosteoCotizacion</c>). Filtro global: las acciones nuevas lo heredan sin código.
/// </summary>
public sealed class clsEtagVersionFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result is ObjectResult { Value: IClsConVersionFila conVersion } resultado
            && (resultado.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300)
        {
            clsConcurrenciaHttp.EscribirEtag(context.HttpContext.Response, conVersion.RowVersion);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
