using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Redaccion.Services;

/// <summary>Resultado de validar unos textos: los textos normalizados si pasaron, o el motivo (código estable) y el mensaje.</summary>
public sealed record clsValidacionTextosCotizacion(string? Asunto, string? Introduccion, string? Motivo, string? Mensaje)
{
    public bool EsValida => Motivo is null;
}

/// <summary>
/// Esquema y reglas de los textos del PDF (HU-013.5, ADR-018 §2a). La salida del redactor es un objeto JSON con exactamente
/// <c>asunto</c> e <c>introduccion</c> (texto). Reglas, en este orden (la primera que falla es el motivo):
/// <list type="number">
/// <item>No vacíos: asunto de 1 a <see cref="clsTextosCotizacion.LargoMaximoAsunto"/> caracteres e introducción de 1 a
/// <see cref="clsTextosCotizacion.LargoMaximoIntroduccion"/> (la IA) o <see cref="clsTextosCotizacion.LargoMaximoIntroduccionComercial"/>
/// (el comercial), contados después de normalizar (sin caracteres de control, el asunto
/// en una línea, la introducción en párrafos separados por una línea en blanco).</item>
/// <item>Sin correos ni URL, tampoco ofuscados (<c>http(s)://</c>, <c>www.</c>, un dominio con terminación conocida, <c>[.]</c>,
/// <c>(punto)</c>, "punto com", <c>(arroba)</c>, <c>[at]</c>).</item>
/// <item>Solo para la IA, después de quitar los textos que vienen literalmente en el contexto (así "3M Colombia" no cuenta como
/// cifra): sin teléfonos (<see cref="Telefono"/>); sin montos ni cifras (<see cref="Monto"/>: <c>$</c>, <c>€</c>, porcentajes,
/// números de 4 o más dígitos con o sin separadores, millones, mil pesos, pesos, euros, dólares, USD, COP, TRM y lo que detecta
/// <see cref="clsReglasRespuestaAgente.Montos"/>); y sin condiciones comerciales inventadas (<see cref="CondicionNoPermitida"/>:
/// póliza, asegurad…, seguro, pago a, plazo, crédito, descuento, vigencia, garantiz… o "N días/horas"). Los montos y las
/// condiciones que escribe el comercial sí se permiten: son suyos.</item>
/// </list>
/// La validación es manual aunque el proveedor reciba el JSON Schema: no todos lo cumplen y las reglas de negocio no caben en él.
/// </summary>
public static partial class clsReglasTextosCotizacion
{
    public const string NombreEsquema = "textos_cotizacion";
    public const string DescripcionEsquema = "Asunto e introducción de la cotización de transporte de Botero Soto, sin montos, URL ni correos.";

    // Motivos (estables: auditoría, métricas y logs).
    public const string JsonInvalido = "json_invalido";
    public const string AsuntoVacio = "asunto_vacio";
    public const string IntroduccionVacia = "introduccion_vacia";
    public const string AsuntoLargo = "asunto_largo";
    public const string IntroduccionLarga = "introduccion_larga";
    public const string Correo = "correo";
    public const string Url = "url";
    public const string Monto = "monto";
    public const string Telefono = "telefono";
    public const string CondicionNoPermitida = "condicion_no_permitida";

    private const string CampoAsunto = "asunto";
    private const string CampoIntroduccion = "introduccion";

    private static readonly Lazy<JsonElement> _esquema = new(() => CrearEsquema(estricto: false));
    private static readonly Lazy<JsonElement> _esquemaEstricto = new(() => CrearEsquema(estricto: true));

    /// <summary>JSON Schema de la salida: los dos campos requeridos, texto con <c>minLength</c>/<c>maxLength</c>, sin adicionales.</summary>
    public static JsonElement Esquema => _esquema.Value;

    /// <summary>Variante del modo estricto de OpenAI (<c>IA:JsonSchemaEstricto</c>): sin <c>minLength</c> ni <c>maxLength</c>.</summary>
    public static JsonElement EsquemaEstricto => _esquemaEstricto.Value;

    /// <summary>El esquema como texto (va en la instrucción cuando el proveedor no admite JSON Schema).</summary>
    public static string EsquemaTexto => Esquema.GetRawText();

    /// <summary><c>response_format</c> con el esquema (estricto o no).</summary>
    public static ChatResponseFormat Formato(bool estricto) =>
        ChatResponseFormat.ForJsonSchema(estricto ? EsquemaEstricto : Esquema, NombreEsquema, DescripcionEsquema);

    /// <summary>
    /// Interpreta la salida cruda del modelo: JSON (se toleran las cercas <c>```json</c> y texto alrededor de un único objeto) con
    /// exactamente <c>asunto</c> e <c>introduccion</c> como texto, y luego las reglas de la IA. <paramref name="permitidos"/> son los
    /// textos del contexto enviados al modelo (cliente, ciudades, vehículo, conexos): sus apariciones literales no cuentan como cifras.
    /// Nunca lanza.
    /// </summary>
    public static clsValidacionTextosCotizacion InterpretarSalidaIa(string? crudo, IEnumerable<string?>? permitidos = null)
    {
        if (LeerObjeto(crudo) is not { } objeto)
        {
            return Fallo(JsonInvalido, "La salida no es un objeto JSON con 'asunto' e 'introduccion'.");
        }

        var nombres = objeto.EnumerateObject().Select(p => p.Name).ToList();
        if (nombres.Count != 2 || !nombres.Contains(CampoAsunto, StringComparer.Ordinal) || !nombres.Contains(CampoIntroduccion, StringComparer.Ordinal)
            || objeto.GetProperty(CampoAsunto).ValueKind != JsonValueKind.String
            || objeto.GetProperty(CampoIntroduccion).ValueKind != JsonValueKind.String)
        {
            return Fallo(JsonInvalido, "La salida no cumple el esquema: se esperan solo 'asunto' e 'introduccion' como texto.");
        }

        return Validar(objeto.GetProperty(CampoAsunto).GetString(), objeto.GetProperty(CampoIntroduccion).GetString(), permitirMontos: false, permitidos);
    }

    /// <summary>
    /// Normaliza y valida un asunto y una introducción. <paramref name="permitirMontos"/> es <c>true</c> para los del comercial (solo
    /// longitudes, correos y URL) y <c>false</c> para los de la IA (además teléfonos, cifras y condiciones; ver el resumen de la
    /// clase). <paramref name="permitidos"/>: textos del contexto cuyas apariciones literales no cuentan como cifras.
    /// </summary>
    public static clsValidacionTextosCotizacion Validar(
        string? asunto, string? introduccion, bool permitirMontos, IEnumerable<string?>? permitidos = null)
    {
        var asuntoNormal = NormalizarAsunto(asunto);
        var introNormal = NormalizarIntroduccion(introduccion);
        if (asuntoNormal is null)
        {
            return Fallo(AsuntoVacio, "El asunto es obligatorio.");
        }

        if (introNormal is null)
        {
            return Fallo(IntroduccionVacia, "La introducción es obligatoria.");
        }

        if (asuntoNormal.Length > clsTextosCotizacion.LargoMaximoAsunto)
        {
            return Fallo(AsuntoLargo, $"El asunto admite hasta {clsTextosCotizacion.LargoMaximoAsunto} caracteres.");
        }

        var maximoIntroduccion = permitirMontos ? clsTextosCotizacion.LargoMaximoIntroduccionComercial : clsTextosCotizacion.LargoMaximoIntroduccion;
        if (introNormal.Length > maximoIntroduccion)
        {
            return Fallo(IntroduccionLarga, $"La introducción admite hasta {maximoIntroduccion} caracteres.");
        }

        var literales = (permitidos ?? []).Select(p => p?.Trim()).OfType<string>().Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(p => p.Length).ToList();
        foreach (var texto in new[] { asuntoNormal, introNormal })
        {
            if (ExpresionCorreo().IsMatch(texto) || ExpresionCorreoOfuscado().IsMatch(texto))
            {
                return Fallo(Correo, "Los textos de la cotización no pueden llevar correos.");
            }

            if (ExpresionUrl().IsMatch(texto) || ExpresionUrlOfuscada().IsMatch(texto))
            {
                return Fallo(Url, "Los textos de la cotización no pueden llevar URL.");
            }

            if (permitirMontos)
            {
                continue;
            }

            var depurado = QuitarLiterales(texto, literales);
            if (ExpresionTelefono().IsMatch(depurado))
            {
                return Fallo(Telefono, "Los textos redactados por la IA no pueden llevar teléfonos.");
            }

            if (TieneMontos(depurado))
            {
                return Fallo(Monto, "Los textos redactados por la IA no pueden llevar montos ni cifras.");
            }

            if (ExpresionCondicion().IsMatch(depurado))
            {
                return Fallo(CondicionNoPermitida,
                    "Los textos redactados por la IA no pueden prometer condiciones comerciales (pólizas, seguros, plazos, pagos, descuentos, vigencias).");
            }
        }

        return new clsValidacionTextosCotizacion(asuntoNormal, introNormal, null, null);
    }

    /// <summary>
    /// ¿El texto trae un monto o una cifra? (<c>$</c>, <c>€</c>, palabras de cantidad monetaria, porcentajes, números de 4 o más
    /// dígitos con o sin separadores, o un monto que detecta <see cref="clsReglasRespuestaAgente.Montos"/>.)
    /// </summary>
    public static bool TieneMontos(string? texto) =>
        !string.IsNullOrWhiteSpace(texto)
        && (ExpresionMoneda().IsMatch(texto) || ExpresionPorcentaje().IsMatch(texto) || ExpresionCifra().IsMatch(texto)
            || clsReglasRespuestaAgente.Montos(texto).Count > 0);

    /// <summary>Quita del texto las apariciones literales de los textos del contexto (sin distinguir mayúsculas).</summary>
    private static string QuitarLiterales(string texto, IReadOnlyList<string> literales)
    {
        foreach (var literal in literales)
        {
            texto = texto.Replace(literal, " ", StringComparison.OrdinalIgnoreCase);
        }

        return texto;
    }

    /// <summary>Asunto en una línea, sin caracteres de control ni espacios repetidos; <c>null</c> si queda vacío.</summary>
    public static string? NormalizarAsunto(string? asunto) =>
        clsTextoSeguro.LimpiarLinea(asunto) is { } limpio ? ExpresionEspacios().Replace(limpio, " ").Trim() : null;

    /// <summary>Introducción en párrafos separados por una línea en blanco, sin caracteres de control; <c>null</c> si queda vacía.</summary>
    public static string? NormalizarIntroduccion(string? introduccion)
    {
        if (clsTextoSeguro.Limpiar(introduccion) is not { } limpio)
        {
            return null;
        }

        var parrafos = ExpresionLineasEnBlanco().Split(limpio)
            .Select(p => ExpresionEspacios().Replace(p.Replace('\n', ' '), " ").Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parrafos.Count == 0 ? null : string.Join("\n\n", parrafos);
    }

    private static clsValidacionTextosCotizacion Fallo(string motivo, string mensaje) => new(null, null, motivo, mensaje);

    /// <summary>El objeto JSON de la salida: entero, entre cercas <c>```</c> o el único objeto <c>{...}</c> dentro de texto.</summary>
    private static JsonElement? LeerObjeto(string? crudo)
    {
        if (string.IsNullOrWhiteSpace(crudo))
        {
            return null;
        }

        var texto = crudo.Trim();
        if (ExpresionCercas().Match(texto) is { Success: true } cerca)
        {
            texto = cerca.Groups["json"].Value.Trim();
        }

        if (Parsear(texto) is { } directo)
        {
            return directo;
        }

        var inicio = texto.IndexOf('{', StringComparison.Ordinal);
        var fin = texto.LastIndexOf('}');
        return inicio >= 0 && fin > inicio ? Parsear(texto[inicio..(fin + 1)]) : null;
    }

    private static JsonElement? Parsear(string texto)
    {
        try
        {
            using var documento = JsonDocument.Parse(texto, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return documento.RootElement.ValueKind == JsonValueKind.Object ? documento.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement CrearEsquema(bool estricto)
    {
        JsonObject Campo(string descripcion, int maximo)
        {
            var campo = new JsonObject { ["type"] = "string", ["description"] = descripcion };
            if (!estricto)
            {
                campo["minLength"] = 1;
                campo["maxLength"] = maximo;
            }

            return campo;
        }

        var esquema = new JsonObject
        {
            ["type"] = "object",
            ["description"] = DescripcionEsquema,
            ["properties"] = new JsonObject
            {
                [CampoAsunto] = Campo("Asunto de la cotización, en una línea.", clsTextosCotizacion.LargoMaximoAsunto),
                [CampoIntroduccion] = Campo("Introducción de la carta, en uno a tres párrafos separados por una línea en blanco.",
                    clsTextosCotizacion.LargoMaximoIntroduccion),
            },
            ["required"] = new JsonArray(CampoAsunto, CampoIntroduccion),
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(esquema);
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionCorreo();

    [GeneratedRegex(
        @"(?:\b(?:https?|ftp)://|\bwww\.)\S+|\b[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9-]+)*\.(?:com|co|net|org|io|info|biz|gov|edu|app|dev|xyz|me|ly|site|online|link|ai)(?:\.[a-z]{2})?(?![a-z0-9])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionUrl();

    [GeneratedRegex(@"\(\s*arroba\s*\)|\[\s*arroba\s*\]|\barroba\b|[\[(]\s*at\s*[\])]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionCorreoOfuscado();

    [GeneratedRegex(@"\[\s*(?:\.|punto|dot)\s*\]|\(\s*(?:\.|punto|dot)\s*\)|\b(?:punto|dot)\s+(?:com|co|net|org)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionUrlOfuscada();

    [GeneratedRegex(
        @"(?<!\d)(?:\+\d{1,3}[\s.-]?)?\(?\d{3}\)?[\s.-]\d{3}[\s.-]?\d{2,4}(?!\d)|\b(?:tel[eé]fonos?|celular(?:es)?|whats\s?app|m[oó]vil(?:es)?|pbx)(?!\p{L})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionTelefono();

    [GeneratedRegex(@"[$€£]|\b(?:COP|USD|EUR|TRM)\b|\b(?:millones|mill[oó]n|mil\s+pesos|pesos|euros?|d[oó]lares?)(?!\p{L})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionMoneda();

    [GeneratedRegex(@"\d\s?%|\bpor\s?ciento\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionPorcentaje();

    [GeneratedRegex(@"(?<![\p{L}\d])(?:\d{1,3}(?:[.,' ]\d{3})+(?:[.,]\d+)?|\d{4,})(?!\d)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionCifra();

    [GeneratedRegex(
        @"\b(?:p[oó]lizas?|asegurad\p{L}*|seguros?|pagos?\s+a|plazos?|cr[eé]ditos?|descuentos?|vigencias?|garantiz\p{L}*)(?!\p{L})|\b\d+\s*(?:d[ií]as?|horas?)(?!\p{L})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionCondicion();

    [GeneratedRegex(@"[ \t ]{2,}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionEspacios();

    [GeneratedRegex(@"\n[ \t]*\n\s*", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExpresionLineasEnBlanco();

    [GeneratedRegex(@"\A```(?:json)?\s*(?<json>.*?)\s*```\z", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExpresionCercas();
}
