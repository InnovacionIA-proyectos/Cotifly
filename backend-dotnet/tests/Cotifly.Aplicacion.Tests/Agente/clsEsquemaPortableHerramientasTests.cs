using System.ComponentModel;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Microsoft.Extensions.AI;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.Services;
using Microsoft.SemanticKernel;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Declaraciones portables de las herramientas (HU-003.6, issue #84): el esquema de Semantic Kernel trae la descripción en la clave
/// no estándar <c>condition</c> y la nulabilidad como arreglo de tipos, que Gemini (vía OmniRoute) rechaza con 400. Contrato: las 4
/// herramientas del asistente por folio (HU-013.6; las 9 del agente legado se retiraron en HU-013.9) solo usan palabras portables y
/// conservan nombres, descripciones, tipos y obligatorios.
/// </summary>
public sealed class clsEsquemaPortableHerramientasTests
{
    private static readonly string[] _cuatroHerramientas =
    [
        "consultar_cotizacion", "proponer_ajuste_margen", "proponer_re_solicitud", "buscar_servicio_conexo",
    ];

    /// <summary>Plugin real del asistente (las dependencias no se invocan: solo se leen los esquemas).</summary>
    private static KernelPlugin Plugin() => KernelPluginFactory.CreateFromObject(
        new clsHerramientasAsistenteFolio(
            new clsContextoAsistenteFolio(),
            Substitute.For<IClsServiciosConexosCosteoService>(),
            Substitute.For<IClsReSolicitudCosteoService>(),
            Substitute.For<IClsCategoriasConexos>()),
        clsNombresHerramientas.PluginAsistente);

    // Contrato sobre lo que recibe el modelo: las declaraciones que arma el runner del asistente.
    [Fact]
    public void LasCuatroHerramientasQueVeElModeloSoloUsanJsonSchemaPortable()
    {
        var declaraciones = Declaraciones();

        declaraciones.Select(d => d.Name).Should().BeEquivalentTo(_cuatroHerramientas);
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
    public void ConservanNombresDescripcionesTiposYObligatoriosDelEsquemaDeSemanticKernel()
    {
        var declaraciones = Declaraciones().ToDictionary(d => d.Name);
        var plugin = Plugin();

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
        var funcion = Plugin()[clsNombresHerramientas.ProponerReSolicitud];

        funcion.JsonSchema.GetProperty("condition").GetString().Should().Be(funcion.Description, "si Semantic Kernel lo corrige, esta prueba avisa");
        funcion.JsonSchema.GetProperty("properties").GetProperty("pesoKg").GetProperty("type").ValueKind.Should().Be(JsonValueKind.Array);

        var portable = clsEsquemaPortableHerramientas.Declarar(funcion);

        portable.JsonSchema.TryGetProperty("condition", out _).Should().BeFalse();
        portable.JsonSchema.GetProperty("properties").GetProperty("pesoKg").GetProperty("type").GetString().Should().Be("number");
        Requeridos(portable.JsonSchema).Should().NotContain("pesoKg");
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
                "destinos": { "type": "array", "items": { "type": "object", "properties": { "ciudad": { "type": ["string", "null"] } }, "required": ["ciudad"], "additionalProperties": false } }
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
        Requeridos(portable).Should().Equal("fecha");
    }

    // Lo que no tiene forma portable falla al declarar (con la ruta del campo), nunca se degrada en silencio (#84, B2).
    [Theory]
    [InlineData("""{"x":{"anyOf":[{"type":"string"},{"type":"number"}],"description":"d"}}""", "x", "anyOf con 2")]
    [InlineData("""{"x":{"anyOf":[{"type":"string"},{"type":"null"}],"oneOf":[{"type":"number"},{"type":"null"}]}}""", "x", "anyOf y oneOf")]
    [InlineData("""{"x":{"type":["string","number"]}}""", "x", "varios tipos")]
    [InlineData("""{"x":{"type":"object","additionalProperties":{"type":"string"}}}""", "x", "diccionario")]
    [InlineData("""{"x":{"type":"array","items":[{"type":"string"}]}}""", "x", "tupla")]
    [InlineData("""{"x":{"$ref":"#/$defs/T"}}""", "x", "$ref")]
    [InlineData("""{"x":{"description":"sin tipo"}}""", "x", "no declara type")]
    [InlineData("""{"x":{"type":"object"}}""", "x", "objeto sin properties")]
    [InlineData("""{"x":{"type":"array","description":"sin items"}}""", "x", "arreglo sin items")]
    [InlineData("""{"x":{"type":"object","properties":{"y":{"type":"array","items":{"type":"object"}}}}}""", "x.y[]", "objeto sin properties")]
    public void UnEsquemaNoPortableFallaConLaRutaDelCampo(string propiedades, string ruta, string motivo)
    {
        var esquema = Esquema($$"""{"type":"object","properties":{{propiedades}}}""");

        var portar = () => clsEsquemaPortableHerramientas.Portable(esquema);

        portar.Should().Throw<InvalidOperationException>().WithMessage($"*'parameters.{ruta}'*{motivo}*");
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

    private static List<AIFunctionDeclaration> Declaraciones() =>
        clsEsquemaPortableHerramientas.Declarar(Plugin()).Cast<AIFunctionDeclaration>().ToList();

    /// <summary>Recorre el esquema: solo palabras portables, <c>type</c> siempre un string y <c>format</c> de la lista segura.</summary>
    private static void RecorrerSoloPortable(JsonElement nodo, string ruta)
    {
        nodo.ValueKind.Should().Be(JsonValueKind.Object, ruta);
        var esRaiz = !ruta.Contains('.', StringComparison.Ordinal) && !ruta.EndsWith("[]", StringComparison.Ordinal);
        if (!esRaiz)
        {
            // Garantías del sanitizador (B2): todo campo con tipo, objetos con propiedades y arreglos con items.
            nodo.TryGetProperty("type", out var tipoNodo).Should().BeTrue($"{ruta}: todo campo declara type");
            if (tipoNodo.GetString() == "object")
            {
                nodo.TryGetProperty("properties", out var hijas).Should().BeTrue($"{ruta}: un objeto declara properties");
                hijas.EnumerateObject().Should().NotBeEmpty($"{ruta}: un objeto declara al menos una propiedad");
            }

            if (tipoNodo.GetString() == "array")
            {
                nodo.TryGetProperty("items", out _).Should().BeTrue($"{ruta}: un arreglo declara items");
            }
        }

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

    private static List<string> Requeridos(JsonElement esquema) =>
        esquema.TryGetProperty("required", out var requeridos) ? requeridos.EnumerateArray().Select(r => r.GetString()!).ToList() : [];

    private static string? Texto(JsonElement nodo, string clave) => nodo.TryGetProperty(clave, out var valor) ? valor.GetString() : null;

    private static JsonElement Esquema(string json)
    {
        using var documento = JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }
}
