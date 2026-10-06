using System.Text.Json;

namespace Cotifly.Aplicacion.Redaccion.DTOs;

/// <summary>
/// <c>TextosCotizacion</c> (HU-013.5): asunto (hasta 120 caracteres) e introducción (hasta 900 de la IA y 1100 del comercial, párrafos separados por una línea en
/// blanco) del PDF guardados en <c>Cotizaciones.textosCotizacion</c>. <see cref="Fuente"/> es <see cref="FuenteIa"/> o
/// <see cref="FuenteEstandar"/>; <see cref="Editado"/> es <c>true</c> si el comercial los cambió después.
/// </summary>
public sealed record clsTextosCotizacion(string Asunto, string Introduccion, string Fuente, bool Editado, DateTimeOffset ActualizadoAt)
{
    /// <summary>Los redactó la IA y pasaron la validación.</summary>
    public const string FuenteIa = "ia";

    /// <summary>Textos de <c>clsTextosEstandar</c> (la IA tardó, falló o su salida no pasó la validación).</summary>
    public const string FuenteEstandar = "estandar";

    /// <summary>Largo máximo del asunto (contrato <c>TextosCotizacion.asunto</c>).</summary>
    public const int LargoMaximoAsunto = 120;

    /// <summary>Largo máximo de la introducción que redacta la IA (esquema del redactor).</summary>
    public const int LargoMaximoIntroduccion = 900;

    /// <summary>
    /// Largo máximo de la introducción del comercial (<c>TextosCotizacionWeb.introduccion</c>) y de la respuesta
    /// (<c>TextosCotizacion.introduccion</c>): 1100, para que pueda guardar la introducción estándar del pliego (1026) sin recortarla.
    /// </summary>
    public const int LargoMaximoIntroduccionComercial = 1100;

    /// <summary>
    /// ¿El PDF imprime estos textos? Solo los de la IA o los editados por el comercial; los estándar sin editar dejan que el PDF use
    /// los suyos (la introducción de su variante: tarifa única o pliego), que son los mismos de <c>clsTextosEstandar</c>.
    /// </summary>
    public bool ReemplazanLosDelPdf() => Editado || Fuente == FuenteIa;

    /// <summary>Párrafos de la introducción (separados por una línea en blanco).</summary>
    public IReadOnlyList<string> Parrafos() =>
        [.. Introduccion.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>Textos guardados y la versión nueva de la fila (va en el <c>ETag</c>, no en el cuerpo).</summary>
public sealed record clsTextosGuardados(clsTextosCotizacion Textos, string RowVersion);

/// <summary>Lectura y escritura del JSON de <c>textosCotizacion</c> (camelCase, validado por <c>ck_cotizaciones_textosCotizacion_json</c>).</summary>
public static class clsTextosCotizacionJson
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Textos guardados, o <c>null</c> si no hay o el JSON no tiene la forma esperada.</summary>
    public static clsTextosCotizacion? Leer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var textos = JsonSerializer.Deserialize<clsTextosCotizacion>(json, _json);
            return textos is { Asunto.Length: > 0, Introduccion.Length: > 0, Fuente: clsTextosCotizacion.FuenteIa or clsTextosCotizacion.FuenteEstandar }
                ? textos
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Escribir(clsTextosCotizacion textos)
    {
        ArgumentNullException.ThrowIfNull(textos);
        return JsonSerializer.Serialize(textos, _json);
    }
}
