using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotifly.Api.Comun;

/// <summary>
/// <c>deadline</c> de la captura hacia Nexo: una fecha u hora ISO 8601 <b>sin zona</b> (<c>AAAA-MM-DD</c> o
/// <c>AAAA-MM-DDTHH:mm:ss</c>) se interpreta en hora de Bogotá (America/Bogota, UTC-05:00, sin horario de verano) y no en la zona del
/// servidor (UTC en el contenedor, que la correría al día anterior). Con zona (<c>Z</c> o <c>-05:00</c>) se respeta tal
/// cual. Cotifly la reenvía a Nexo en ISO 8601 con su desplazamiento. Un texto que no es ISO 8601 sigue siendo un cuerpo inválido.
/// </summary>
public sealed class clsConvertidorFechaBogota : JsonConverter<DateTimeOffset?>
{
    /// <summary>Desplazamiento de Bogotá si el sistema no tiene la zona <c>America/Bogota</c> (Colombia no usa horario de verano).</summary>
    public static readonly TimeSpan DesplazamientoBogota = TimeSpan.FromHours(-5);

    private static readonly TimeZoneInfo? _zona = BuscarZona();

    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String || !reader.TryGetDateTime(out var fecha))
        {
            throw new JsonException("La fecha debe estar en ISO 8601 (ej. 2026-10-10T17:00:00-05:00).");
        }

        return fecha.Kind == DateTimeKind.Unspecified ? EnBogota(fecha) : reader.GetDateTimeOffset();
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } fecha)
        {
            writer.WriteStringValue(fecha);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    /// <summary>La fecha y hora "de pared" <paramref name="fecha"/> (sin zona) como hora de Bogotá.</summary>
    public static DateTimeOffset EnBogota(DateTime fecha)
    {
        var local = DateTime.SpecifyKind(fecha, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zona?.GetUtcOffset(local) ?? DesplazamientoBogota);
    }

    private static TimeZoneInfo? BuscarZona()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
