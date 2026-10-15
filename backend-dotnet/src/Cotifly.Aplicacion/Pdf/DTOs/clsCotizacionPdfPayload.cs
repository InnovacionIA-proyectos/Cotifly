using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Comun;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Pdf.DTOs;

/// <summary>
/// Payload del PDF de la cotización: el mismo esquema que <c>tests/fixtures/cotizacion/ejemplo-solicitud.json</c> del Python
/// (el que arma el agente), en snake_case con <see cref="clsJsonAplicacion.SnakeCase"/>. Todo es opcional en el JSON; lo
/// obligatorio lo exige <c>clsValidadorCotizacionPdfPayload</c>.
/// </summary>
/// <remarks>
/// Diferencias con el Python:
/// <list type="bullet">
/// <item><c>comercial.cel</c>, <c>comercial.nombre</c> y <c>comercial.email</c> se ignoran: la firma sale de <c>UsuariosWeb</c>
/// (nunca del LLM, HU-003.5). Solo <c>comercial.cargo</c> se toma del payload (no existe en <c>UsuariosWeb</c>).</item>
/// <item><c>extra_css</c> y <c>dibujo_html</c> no existen: ya no hay HTML (ADR-006).</item>
/// <item><see cref="ServiciosConexos"/> es nuevo (HU-003.5, "PDF muestra solo lo realmente incluido, itemizado").</item>
/// </list>
/// </remarks>
public sealed class clsCotizacionPdfPayload
{
    /// <summary>Folio que escribió el agente. No se usa: el folio lo genera el servidor y llega en la entrada (ADR-011).</summary>
    public string? Referencia { get; set; }

    /// <summary>Ciudad de la fecha larga. Por defecto <c>Yumbo</c>.</summary>
    public string? Ciudad { get; set; }

    /// <summary>Fecha ISO <c>AAAA-MM-DD</c>. Si falta, la fecha de hoy en la zona de negocio.</summary>
    public string? Fecha { get; set; }

    public string? Cliente { get; set; }

    public List<string>? Contactos { get; set; }

    public string? LineaNegocio { get; set; }

    public string? Modalidad { get; set; }

    /// <summary><c>margen</c> (por defecto) o <c>markup</c>: cómo se calcula la tarifa de una fila que trae costo y margen.</summary>
    public string? MetodoCalculo { get; set; }

    public clsComercialPayload? Comercial { get; set; }

    public clsTarifaPayload? Tarifa { get; set; }

    /// <summary>Si trae secciones, el PDF usa la variante multi-sección (<c>armar_multi.py</c>).</summary>
    public List<clsTarifaPayload>? Secciones { get; set; }

    public clsAduanaPayload? Aduana { get; set; }

    public clsExtradimensionadaPayload? Extradimensionada { get; set; }

    public clsRefrigeradaPayload? Refrigerada { get; set; }

    public string? Asunto { get; set; }

    /// <summary>Reemplazos de los textos estándar (<c>intro</c>, <c>incluye</c>, <c>seguro</c>...).</summary>
    public clsTextosPayload? Textos { get; set; }

    public string? NotaDecreto { get; set; }

    public string? DiasLibres { get; set; }

    public string? FraseFinal { get; set; }

    /// <summary>
    /// Servicios conexos seleccionados, cada uno con su precio. Si vienen, "Estas tarifas incluyen" lista el flete
    /// (origen y destinos) y cada servicio con su precio en vez del texto estándar. Hoy Nexo (HU-005) no entrega el costeo
    /// desglosado por servicio: mientras tanto el agente o el formulario los envían así, o no se envían y queda el texto estándar.
    /// </summary>
    public List<clsServicioConexoPayload>? ServiciosConexos { get; set; }

    /// <summary>#173: horas de cargue de la ruta (captura); la nota general las usa en lugar de las 4 h fijas.</summary>
    public decimal? HorasCargue { get; set; }

    /// <summary>#173: horas de descargue de la ruta (captura).</summary>
    public decimal? HorasDescargue { get; set; }

    /// <summary>
    /// Lee un payload JSON con las opciones del proyecto (snake_case). Nunca lanza: JSON mal formado, tipos erróneos,
    /// sustitutos UTF-16 sueltos o un JSON vacío/<c>null</c> → <c>PDF_PAYLOAD_INVALIDO</c> (el LLM puede mandar cualquier cosa).
    /// </summary>
    public static clsResultado<clsCotizacionPdfPayload> DesdeJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, "El payload de la cotización está vacío.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<clsCotizacionPdfPayload>(json, clsJsonAplicacion.SnakeCase);
            return payload is null
                ? new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, "El payload de la cotización está vacío.")
                : payload;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, "El payload de la cotización no es un JSON válido con el esquema esperado.", ex.Message);
        }
    }
}

/// <summary>Bloque <c>comercial</c> del payload. Solo se usa <see cref="Cargo"/> (ver <see cref="clsCotizacionPdfPayload"/>).</summary>
public sealed class clsComercialPayload
{
    public string? Nombre { get; set; }

    public string? Cargo { get; set; }

    public string? Email { get; set; }

    /// <summary>Se ignora siempre: el celular sale de <c>UsuariosWeb.telefono</c>.</summary>
    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Cel { get; set; }
}

/// <summary>Tabla de tarifa (bloque <c>tarifa</c> o cada elemento de <c>secciones</c>).</summary>
public sealed class clsTarifaPayload
{
    public string? Titulo { get; set; }

    public string? Origen { get; set; }

    public List<clsFilaTarifaPayload>? Filas { get; set; }

    public string? NotaGeneral { get; set; }

    public List<string>? Incluye { get; set; }

    public List<string>? NoIncluye { get; set; }
}

/// <summary>Fila de la tabla de tarifa. Si trae <see cref="Tarifa"/> se muestra tal cual; si no, se calcula con costo y margen.</summary>
public sealed class clsFilaTarifaPayload
{
    public string? Destino { get; set; }

    public string? VehiculoPeso { get; set; }

    public decimal? Tarifa { get; set; }

    public decimal? Costo { get; set; }

    public decimal? Margen { get; set; }

    /// <summary><c>USD</c> muestra el prefijo <c>US$</c>; cualquier otra, <c>$</c>.</summary>
    public string? Moneda { get; set; }

    public string? Nota { get; set; }
}

public sealed class clsAduanaPayload
{
    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Modalidad { get; set; }

    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Frontera { get; set; }

    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Arancel { get; set; }
}

public sealed class clsExtradimensionadaPayload
{
    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? PesoKg { get; set; }

    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Ancho { get; set; }

    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Largo { get; set; }

    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Fotos { get; set; }
}

public sealed class clsRefrigeradaPayload
{
    [JsonConverter(typeof(clsConvertidorTextoFlexible))]
    public string? Temperatura { get; set; }
}

/// <summary>Reemplazos de los textos estándar por cotización (<c>textos</c> del payload).</summary>
public sealed class clsTextosPayload
{
    public List<string>? Intro { get; set; }

    public List<string>? Incluye { get; set; }

    public List<string>? NoIncluye { get; set; }

    public string? NotaGeneral { get; set; }

    public string? Seguro { get; set; }

    public string? StandBy { get; set; }

    public string? Vigencia { get; set; }

    public string? Facturacion { get; set; }
}

/// <summary>Servicio conexo incluido en la cotización (ej. levante o devolución de contenedor) con su precio.</summary>
public sealed class clsServicioConexoPayload
{
    public string? Descripcion { get; set; }

    public decimal? Precio { get; set; }

    public string? Moneda { get; set; }
}

/// <summary>
/// Lee como texto un valor que el agente puede mandar como texto, número o booleano (<c>"peso_kg": 40000</c> o
/// <c>"40000"</c>); los números conservan su forma original, como el <c>f"{v}"</c> del Python.
/// </summary>
public sealed class clsConvertidorTextoFlexible : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
            JsonTokenType.True => "True",
            JsonTokenType.False => null,
            _ => throw new JsonException($"Se esperaba texto o número y llegó {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
