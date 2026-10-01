using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// JSON Schema tipado del parámetro <c>cotizacion</c> de <c>generar_pdf</c> (HU-003.6, issue #84). El método recibe un
/// <see cref="JsonElement"/> libre, así que Semantic Kernel lo declara sin tipo. Con un parámetro sin tipo, Gemini vía OmniRoute
/// arma objetos anidados como texto (<c>"textos": "{\"intro\": ...}"</c>, <c>"tarifa": "COTIZACIÓN..."</c>) y el payload no
/// pasa la lectura (<c>PDF_PAYLOAD_INVALIDO</c>). Con el esquema anidado el modelo respeta la forma (probado el 2026-10-01).
/// </summary>
/// <remarks>
/// Es solo la parte del payload que aporta el agente, la misma del prompt (paso 5): sin <c>referencia</c> (el folio lo genera el
/// servidor) y sin números (costo, margen, tarifa, precio), que el servidor toma del estado. Sigue a
/// <see cref="clsCotizacionPdfPayload"/> en snake_case; la lectura sigue aceptando también los demás campos.
/// </remarks>
public static class clsEsquemaPayloadPdf
{
    public const string Parametro = "cotizacion";

    private const string Json = """
        {
          "type": "object",
          "properties": {
            "ciudad": { "type": "string", "description": "Ciudad del comercial (fecha larga del PDF)." },
            "fecha": { "type": "string", "description": "Fecha AAAA-MM-DD." },
            "cliente": { "type": "string", "description": "Nombre del cliente, como texto." },
            "contactos": { "type": "array", "items": { "type": "string" }, "description": "Contactos con tratamiento Señor/Señora." },
            "linea_negocio": { "type": "string", "description": "Línea de negocio (Nacional, Distribucion, Importacion...)." },
            "modalidad": { "type": "string", "description": "Texto corto de la modalidad." },
            "asunto": { "type": "string" },
            "textos": {
              "type": "object",
              "description": "Textos que reemplazan a los estándar; intro es la introducción consultiva.",
              "properties": {
                "intro": { "type": "array", "items": { "type": "string" }, "description": "Párrafos de la introducción consultiva." },
                "incluye": { "type": "array", "items": { "type": "string" } },
                "no_incluye": { "type": "array", "items": { "type": "string" } },
                "nota_general": { "type": "string" }
              }
            },
            "comercial": {
              "type": "object",
              "properties": { "cargo": { "type": "string", "description": "Cargo del comercial." } }
            },
            "tarifa": {
              "type": "object",
              "description": "Tabla de tarifa: título, origen y una fila por destino (sin números: los pone el servidor).",
              "properties": {
                "titulo": { "type": "string", "description": "Ej. COTIZACIÓN TRANSPORTE NACIONAL." },
                "origen": { "type": "string", "description": "Ciudad de origen." },
                "filas": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "destino": { "type": "string" },
                      "vehiculo_peso": { "type": "string", "description": "Vehículo y peso, ej. Tractomula - 10 TON." },
                      "nota": { "type": "string", "description": "Condiciones puntuales de la fila." }
                    },
                    "required": ["destino"]
                  }
                }
              },
              "required": ["filas"]
            },
            "aduana": {
              "type": "object",
              "description": "Solo para Internacional/ANDINO y si el comercial dio los datos.",
              "properties": { "modalidad": { "type": "string" }, "frontera": { "type": "string" }, "arancel": { "type": "string" } }
            },
            "extradimensionada": {
              "type": "object",
              "description": "Solo si la carga es extradimensionada.",
              "properties": { "peso_kg": { "type": "string" }, "ancho": { "type": "string" }, "largo": { "type": "string" }, "fotos": { "type": "string" } }
            },
            "refrigerada": {
              "type": "object",
              "description": "Solo si la carga es refrigerada.",
              "properties": { "temperatura": { "type": "string" } }
            }
          },
          "required": ["cliente", "linea_negocio", "tarifa"]
        }
        """;

    /// <summary>Esquema del parámetro <c>cotizacion</c> (sin descripción: se conserva la de la función de Semantic Kernel).</summary>
    public static JsonElement Esquema { get; } = Crear();

    /// <summary>Esquemas tipados por herramienta y parámetro, para las declaraciones de <see cref="clsEsquemaPortableHerramientas"/>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> PorHerramienta { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal)
        {
            [clsNombresHerramientas.GenerarPdf] = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [Parametro] = Esquema },
        };

    private static JsonElement Crear()
    {
        using var documento = JsonDocument.Parse(Json);
        return documento.RootElement.Clone();
    }
}
