using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Dominio.Agente;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Serializa <see cref="clsEstadoConversacion"/> como JSON en <c>Sesiones.estadoAgente</c> (ADR-004):
/// propiedades en camelCase y enums con su código (<c>politica_linea</c>, <c>prospecto</c>...).
/// La columna tiene <c>CHECK (ISJSON(estadoAgente) = 1)</c>.
/// </summary>
public sealed class clsConvertidorEstadoConversacion : ValueConverter<clsEstadoConversacion, string>
{
    public static readonly JsonSerializerOptions OpcionesJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    public clsConvertidorEstadoConversacion()
        : base(estado => Serializar(estado), json => Deserializar(json))
    {
    }

    public static string Serializar(clsEstadoConversacion estado) => JsonSerializer.Serialize(estado, OpcionesJson);

    public static clsEstadoConversacion Deserializar(string json) =>
        JsonSerializer.Deserialize<clsEstadoConversacion>(json, OpcionesJson) ?? clsEstadoConversacion.Vacio;
}
