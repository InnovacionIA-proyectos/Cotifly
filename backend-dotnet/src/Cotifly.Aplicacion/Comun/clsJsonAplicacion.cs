using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Cotifly.Aplicacion.Comun;

/// <summary>
/// Opciones JSON compartidas por los casos de uso: nombres snake_case (convención de <c>/web/*</c> y de las herramientas
/// heredadas del Python), lectura sin distinguir mayúsculas y números que llegan como texto.
/// </summary>
public static class clsJsonAplicacion
{
    /// <summary>
    /// snake_case en minúsculas (<c>linea_negocio</c>, <c>cotizacion_id</c>). Tildes y eñes sin escapar
    /// (<c>"Cotización"</c>, no <c>"Cotización"</c>): el JSON se guarda en la base (<c>EventosHerramienta</c>) y se lee a mano.
    /// </summary>
    public static readonly JsonSerializerOptions SnakeCase = Crear();

    private static JsonSerializerOptions Crear()
    {
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
        opciones.MakeReadOnly(populateMissingResolver: true);
        return opciones;
    }
}
