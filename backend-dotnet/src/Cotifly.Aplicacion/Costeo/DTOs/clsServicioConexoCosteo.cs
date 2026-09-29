using System.Text.Json;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Servicio conexo del costeo de Nexo tal como lo guarda Cotifly en <c>Cotizaciones.serviciosConexos</c> (HU-005.7, ADR-015 §5):
/// su costo de Nexo (solo interno, nunca va al comercial), su precio con el margen de la cotización y si el comercial lo incluye.
/// </summary>
public sealed record clsServicioConexoCosteo(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal Costo, decimal Precio, bool Incluido);

/// <summary>Lectura y escritura del JSON de <c>serviciosConexos</c> (camelCase, validado por <c>ck_cotizaciones_serviciosConexos_json</c>).</summary>
public static class clsServiciosConexosJson
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Servicios guardados; lista vacía si no hay o el JSON no tiene la forma esperada.</summary>
    public static IReadOnlyList<clsServicioConexoCosteo> Leer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<clsServicioConexoCosteo>>(json, _json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>JSON de los servicios, o <c>null</c> si no hay ninguno.</summary>
    public static string? Escribir(IReadOnlyList<clsServicioConexoCosteo> servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        return servicios.Count == 0 ? null : JsonSerializer.Serialize(servicios, _json);
    }
}
