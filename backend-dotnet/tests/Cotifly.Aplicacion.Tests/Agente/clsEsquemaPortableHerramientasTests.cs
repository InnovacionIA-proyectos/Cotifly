using System.ComponentModel;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Declaraciones portables de las herramientas (HU-003.6, issue #84): el esquema de Semantic Kernel trae la descripción en la clave
/// no estándar <c>condition</c> y la nulabilidad como arreglo de tipos, que Gemini (vía OmniRoute) rechaza con 400. Contrato: las 9
/// herramientas que ve el modelo solo usan palabras portables y conservan nombres, descripciones, tipos y obligatorios.
/// </summary>
public sealed class clsEsquemaPortableHerramientasTests
{
    private static readonly string[] _nueveHerramientas =
    [
        "buscar_cliente", "resolver_municipio", "buscar_tarifa", "obtener_margen", "calcular_precio", "registrar_ajuste_margen",
        "generar_pdf", "enviar_correo", "consultar_cotizacion",
    ];

    private readonly clsArnesAgente _arnes = new();

    // Contrato sobre lo que de verdad recibe el modelo: las declaraciones de la primera llamada del loop, con el correo habilitado.
    [Fact]
    public async Task LasNueveHerramientasQueVeElModeloSoloUsanJsonSchemaPortable()
    {
        var declaraciones = await DeclaracionesDelTurnoAsync();

        declaraciones.Select(d => d.Name).Should().BeEquivalentTo(_nueveHerramientas);
        foreach (var declaracion in declaraciones)
        {
            declaracion.Description.Should().NotBeNullOrWhiteSpace($"{declaracion.Name} lleva la descripción a nivel de función");
            var esquema = declaracion.JsonSchema;
            esquema.GetProperty("type").GetString().Should().Be("object", declaracion.Name);
            esquema.TryGetProperty("description", out _).Should().BeFalse($"{declaracion.Name}: la descripción no se repite en el esquema");
            RecorrerSoloPortable(esquema, declaracion.Name);

            var propiedades = esquema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
            if (esquema.TryGetProperty("required", out var requeridos))
            {
                requeridos.EnumerateArray().Select(r => r.GetString()).Should().OnlyContain(r => propiedades.Contains(r!), declaracion.Name);
            }
        }
    }

    // Mismo contenido que el esquema de Semantic Kernel: cambia la forma, no lo que el modelo sabe de cada parámetro.
    [Fact]
    public async Task ConservanNombresDescripcionesTiposYObligatoriosDelEsquemaDeSemanticKernel()
    {
        var declaraciones = (await DeclaracionesDelTurnoAsync()).ToDictionary(d => d.Name);
        var plugin = KernelPluginFactory.CreateFromObject(_arnes.Herramientas(), clsNombresHerramientas.Plugin);

        foreach (var funcion in plugin)
        {
            var portable = declaraciones[funcion.Name];
            portable.Description.Should().Be(funcion.Description);
            var original = funcion.JsonSchema.GetProperty("properties");
            var nuevas = portable.JsonSchema.GetProperty("properties");
            nuevas.EnumerateObject().Select(p => p.Name).Should().Equal(original.EnumerateObject().Select(p => p.Name), funcion.Name);
            foreach (var propiedad in original.EnumerateObject())
            {
                var nueva = nuevas.GetProperty(propiedad.Name);
                Texto(nueva, "description").Should().Be(Texto(propiedad.Value, "description"), $"{funcion.Name}.{propiedad.Name}");
                if (propiedad.Value.TryGetProperty("type", out var tipo))
                {
                    var esperado = tipo.ValueKind == JsonValueKind.Array
                        ? tipo.EnumerateArray().Select(t => t.GetString()).Single(t => t != "null")
                        : tipo.GetString();
                    nueva.GetProperty("type").GetString().Should().Be(esperado, $"{funcion.Name}.{propiedad.Name}");
                }
            }

            Requeridos(portable.JsonSchema).Should().BeEquivalentTo(Requeridos(funcion.JsonSchema), funcion.Name);
        }
    }

    // La causa del 400 de Gemini: Semantic Kernel 1.80.1 pone la descripción de la función en "condition".
    [Fact]
    public void ElEsquemaDeSemanticKernelTraeConditionYElPortableNo()
    {
        var funcion = KernelPluginFactory.CreateFromObject(_arnes.Herramientas(), clsNombresHerramientas.Plugin)[clsNombresHerramientas.BuscarTarifa];

        funcion.JsonSchema.GetProperty("condition").GetString().Should().Be(funcion.Description, "si Semantic Kernel lo corrige, esta prueba avisa");
        funcion.JsonSchema.GetProperty("properties").GetProperty("pesoKg").GetProperty("type").ValueKind.Should().Be(JsonValueKind.Array);

        var portable = clsEsquemaPortableHerramientas.Declarar(funcion);

        portable.JsonSchema.TryGetProperty("condition", out _).Should().BeFalse();
        portable.JsonSchema.GetProperty("properties").GetProperty("pesoKg").GetProperty("type").GetString().Should().Be("number");
        Requeridos(portable.JsonSchema).Should().NotContain("pesoKg");
    }

    // El payload de generar_pdf se declara tipado (Semantic Kernel lo deja sin tipo y Gemini arma los objetos anidados como texto).
    [Fact]
    public async Task ElPayloadDeGenerarPdfSeDeclaraTipadoConLaDescripcionOriginal()
    {
        var generar = (await DeclaracionesDelTurnoAsync()).Single(d => d.Name == clsNombresHerramientas.GenerarPdf);
        var original = KernelPluginFactory.CreateFromObject(_arnes.Herramientas(), clsNombresHerramientas.Plugin)[clsNombresHerramientas.GenerarPdf];

        var cotizacion = generar.JsonSchema.GetProperty("properties").GetProperty(clsEsquemaPayloadPdf.Parametro);

        cotizacion.GetProperty("type").GetString().Should().Be("object");
        Texto(cotizacion, "description").Should().Be(Texto(original.JsonSchema.GetProperty("properties").GetProperty("cotizacion"), "description"));
        var propiedades = cotizacion.GetProperty("properties");
        propiedades.GetProperty("textos").GetProperty("properties").GetProperty("intro").GetProperty("type").GetString().Should().Be("array");
        var fila = propiedades.GetProperty("tarifa").GetProperty("properties").GetProperty("filas").GetProperty("items");
        fila.GetProperty("properties").EnumerateObject().Select(p => p.Name).Should().Equal("destino", "vehiculo_peso", "nota");
        propiedades.TryGetProperty("referencia", out _).Should().BeFalse("el folio lo genera el servidor");
        Requeridos(cotizacion).Should().Equal("cliente", "linea_negocio", "tarifa");
        Requeridos(generar.JsonSchema).Should().Equal("cotizacion");
    }

    // Lo que declara el esquema tipado lo lee el payload real del PDF (mismos nombres en snake_case).
    [Fact]
    public void UnPayloadQueCumpleElEsquemaTipadoSeLeeSinFallos()
    {
        const string Json = """
            {"ciudad": "Bogotá", "fecha": "2026-10-01", "cliente": "Comercializadora Ficticia Andina SAS", "contactos": ["Señora Ana Prueba"],
             "linea_negocio": "Nacional", "modalidad": "Carga seca", "textos": {"intro": ["Párrafo de prueba."]}, "comercial": {"cargo": "Ejecutiva"},
             "tarifa": {"titulo": "COTIZACIÓN TRANSPORTE NACIONAL", "origen": "Bogotá", "filas": [{"destino": "Medellín", "vehiculo_peso": "Tractomula - 10 TON", "nota": ""}]},
             "aduana": {"modalidad": "DTA", "frontera": "Ipiales", "arancel": "0"}, "extradimensionada": {"peso_kg": "40000"}, "refrigerada": {"temperatura": "4 °C"}}
            """;
        var nombres = new HashSet<string>(StringComparer.Ordinal);
        RecolectarPropiedades(clsEsquemaPayloadPdf.Esquema, "", nombres);

        var payload = Cotifly.Aplicacion.Pdf.DTOs.clsCotizacionPdfPayload.DesdeJson(Json);

        payload.EsExitoso.Should().BeTrue();
        payload.Valor.Tarifa!.Filas!.Single().VehiculoPeso.Should().Be("Tractomula - 10 TON");
        payload.Valor.Textos!.Intro.Should().Equal("Párrafo de prueba.");
        nombres.Should().Contain(["textos.intro", "tarifa.filas.vehiculo_peso", "extradimensionada.peso_kg", "comercial.cargo"]);
    }

    [Fact]
    public void LaNulabilidadSeExpresaConUnSoloTipoYFueraDeRequired()
    {
        var esquema = Esquema("""
            {
              "type": "object",
              "condition": "descripción mal ubicada",
              "description": "descripción de la raíz",
              "additionalProperties": false,
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "properties": {
                "peso": { "type": ["number", "null"], "description": "Peso." },
                "tipo": { "type": "string", "nullable": true },
                "moneda": { "anyOf": [ { "type": "string", "enum": ["COP", "USD"] }, { "type": "null" } ], "description": "Moneda." },
                "metodo": { "type": "string", "enum": ["margen", "markup", null], "default": "margen", "title": "Método" },
                "fecha": { "type": "string", "format": "date-time" },
                "sitio": { "type": "string", "format": "uri", "minLength": 3 },
                "destinos": { "type": "array", "items": { "type": "object", "properties": { "ciudad": { "type": ["string", "null"] } }, "required": ["ciudad"], "additionalProperties": false } },
                "libre": { "type": ["string", "number"] }
              },
              "required": ["peso", "tipo", "moneda", "metodo", "fecha", "inexistente"]
            }
            """);

        var portable = clsEsquemaPortableHerramientas.Portable(esquema);

        RecorrerSoloPortable(portable, "sintético");
        portable.TryGetProperty("description", out _).Should().BeFalse();
        var propiedades = portable.GetProperty("properties");
        propiedades.GetProperty("peso").GetRawText().Should().Be("""{"type":"number","description":"Peso."}""");
        propiedades.GetProperty("tipo").GetRawText().Should().Be("""{"type":"string"}""");
        propiedades.GetProperty("moneda").GetRawText().Should().Be("""{"type":"string","enum":["COP","USD"],"description":"Moneda."}""");
        propiedades.GetProperty("metodo").GetRawText().Should().Be("""{"type":"string","enum":["margen","markup"]}""");
        propiedades.GetProperty("fecha").GetProperty("format").GetString().Should().Be("date-time");
        propiedades.GetProperty("sitio").GetRawText().Should().Be("""{"type":"string"}""", "format uri y minLength no son portables");
        propiedades.GetProperty("destinos").GetProperty("items").GetRawText()
            .Should().Be("""{"type":"object","properties":{"ciudad":{"type":"string"}}}""");
        propiedades.GetProperty("libre").TryGetProperty("type", out _).Should().BeFalse("con varios tipos no nulos el campo queda sin tipo");
        Requeridos(portable).Should().Equal("fecha");
    }

    [Fact]
    public void UnEsquemaVacioONoObjetoQuedaComoObjetoSinPropiedades()
    {
        clsEsquemaPortableHerramientas.Portable(default).GetRawText().Should().Be("""{"type":"object","properties":{}}""");
        clsEsquemaPortableHerramientas.Portable(Esquema("[1]")).GetRawText().Should().Be("""{"type":"object","properties":{}}""");
    }

    [Fact]
    public void UnaFuncionSinDescripcionTambienQuedaPortable()
    {
        var funcion = KernelFunctionFactory.CreateFromMethod(([Description("Ciudad.")] string ciudad) => ciudad, "sin_descripcion");

        var declaracion = clsEsquemaPortableHerramientas.Declarar(funcion);

        declaracion.Name.Should().Be("sin_descripcion");
        declaracion.JsonSchema.GetProperty("properties").GetProperty("ciudad").GetProperty("description").GetString().Should().Be("Ciudad.");
        RecorrerSoloPortable(declaracion.JsonSchema, "sin_descripcion");
    }

    private async Task<IReadOnlyList<AIFunctionDeclaration>> DeclaracionesDelTurnoAsync()
    {
        _arnes.OpcionesCorreo.Habilitado = true;
        _arnes.Modelo.Cerrar("ok");
        await _arnes.TurnoAsync("hola");
        return _arnes.Modelo.Llamadas[0].Opciones!.Tools!.Cast<AIFunctionDeclaration>().ToList();
    }

    /// <summary>Recorre el esquema: solo palabras portables, <c>type</c> siempre un string y <c>format</c> de la lista segura.</summary>
    private static void RecorrerSoloPortable(JsonElement nodo, string ruta)
    {
        nodo.ValueKind.Should().Be(JsonValueKind.Object, ruta);
        foreach (var propiedad in nodo.EnumerateObject())
        {
            clsEsquemaPortableHerramientas.PalabrasPermitidas.Should().Contain(propiedad.Name, $"{ruta}: '{propiedad.Name}' no es portable");
            switch (propiedad.Name)
            {
                case "type":
                    propiedad.Value.ValueKind.Should().Be(JsonValueKind.String, $"{ruta}: type es un solo tipo, nunca un arreglo");
                    propiedad.Value.GetString().Should().NotBe("null", ruta);
                    break;
                case "format":
                    clsEsquemaPortableHerramientas.FormatosPermitidos.Should().Contain(propiedad.Value.GetString()!, ruta);
                    break;
                case "properties":
                    foreach (var hija in propiedad.Value.EnumerateObject())
                    {
                        RecorrerSoloPortable(hija.Value, $"{ruta}.{hija.Name}");
                    }

                    break;
                case "items":
                    RecorrerSoloPortable(propiedad.Value, ruta + "[]");
                    break;
                case "enum":
                    propiedad.Value.EnumerateArray().Should().NotContain(v => v.ValueKind == JsonValueKind.Null, ruta);
                    break;
            }
        }
    }

    private static void RecolectarPropiedades(JsonElement nodo, string ruta, HashSet<string> nombres)
    {
        if (nodo.TryGetProperty("items", out var elementos))
        {
            RecolectarPropiedades(elementos, ruta, nombres);
        }

        if (!nodo.TryGetProperty("properties", out var propiedades))
        {
            return;
        }

        foreach (var propiedad in propiedades.EnumerateObject())
        {
            var hija = ruta.Length == 0 ? propiedad.Name : $"{ruta}.{propiedad.Name}";
            nombres.Add(hija);
            RecolectarPropiedades(propiedad.Value, hija, nombres);
        }
    }

    private static List<string> Requeridos(JsonElement esquema) =>
        esquema.TryGetProperty("required", out var requeridos) ? requeridos.EnumerateArray().Select(r => r.GetString()!).ToList() : [];

    private static string? Texto(JsonElement nodo, string clave) => nodo.TryGetProperty(clave, out var valor) ? valor.GetString() : null;

    private static JsonElement Esquema(string json)
    {
        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }
}
