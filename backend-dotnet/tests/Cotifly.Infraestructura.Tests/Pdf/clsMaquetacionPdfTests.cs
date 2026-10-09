using System.Globalization;
using System.Text.Json;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// Referencia de maquetación (#92, #95): posición de los bloques clave de cada variante del PDF (encabezado, fecha, folio,
/// asunto, tabla, notas, condiciones y firma) contra <c>tests/fixtures/pdf/maquetacion-referencia.json</c>, con una tolerancia
/// de <see cref="Tolerancia"/> pt. Un bloque que se corre (márgenes, espacios, saltos de página) hace fallar la prueba aunque el
/// texto siga igual.
/// </summary>
/// <remarks>
/// <para>
/// Cuando un cambio de diseño es intencional, la referencia se regenera a mano, nunca en CI:
/// <c>COTIFLY_REGENERAR_MAQUETACION=1 dotnet test --project tests/Cotifly.Infraestructura.Tests --filter-method "*RegeneraLaReferenciaDeMaquetacion"</c>.
/// Reescribe el JSON en el árbol del repo y falla a propósito para que se revise el diff y se versione junto con el cambio de
/// diseño. Con <c>CI</c> definido se niega a regenerar.
/// </para>
/// <para>Las coordenadas son puntos PDF (origen abajo a la izquierda) de la esquina inferior izquierda de la primera palabra.</para>
/// </remarks>
public sealed class clsMaquetacionPdfTests
{
    /// <summary>Desplazamiento máximo (pt) de un bloque respecto a la referencia: 1,5 pt ≈ 0,5 mm.</summary>
    public const double Tolerancia = 1.5;

    private const string Logo = "[imagen]";
    private const string VariableRegenerar = "COTIFLY_REGENERAR_MAQUETACION";
    private const string Archivo = "maquetacion-referencia.json";

    private static readonly clsGeneradorPdfMigraDoc _generador = new();

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>Bloques clave por variante: nombre del bloque y frase con la que empieza ("[imagen]": el logo de la hoja 1).</summary>
    private static readonly Dictionary<string, (string Bloque, string Frase)[]> _bloques = new(StringComparer.Ordinal)
    {
        ["Nacional"] = TarifaUnica("PCN-19032026", "$3.000.000", ("notaTiempos", "Tiempos logísticos:")),
        ["Internacional"] = TarifaUnica("PCN-24092026", "$10.365.854", ("notaAduana", "Datos aduaneros")),
        ["Dolares"] = TarifaUnica("PCN-USD-1", "US$996,35", ("notaTrm", "Esta cotización está bajo")),
        ["Multi"] =
        [
            ("encabezado", Logo),
            ("pie", "Principal: Carrera 42"),
            ("fecha", "Yumbo, 19 de marzo"),
            ("referencia", "PCN-19032026-v2"),
            ("asunto", "Asunto: presentación"),
            ("condiciones", "Facturación y forma"),
            ("tituloTarifa", "NUESTRA TARIFA PARA"),
            ("tramo1", "TRAMO 1"),
            ("filaTramo1", "$3.000.000"),
            ("nota", "Nota: Tarifa sujeta"),
            ("tramo2", "TRAMO 2"),
            ("filaTramo2", "$1.200.000"),
            ("incluyen", "Estas tarifas incluyen"),
            ("firma", "Cordialmente,"),
            ("firmante", "Nathaly Vargas Garcia"),
        ],
        ["Pliego"] =
        [
            ("encabezado", Logo),
            ("pie", "Principal: Carrera 42"),
            ("referencia", "PCN-900555111-AP-01102026-001"),
            ("asunto", "Asunto:"),
            ("condiciones", "Facturación y forma"),
            ("resumen", "RESUMEN DEL PLIEGO"),
            ("total", "TOTAL DEL PLIEGO"),
            ("tablaOrigen", "PLIEGO DE TRANSPORTE"),
            ("conexos", "Cargue y"),
            ("firma", "Cordialmente,"),
            ("firmante", "Ana Pérez"),
        ],
    };

    public static TheoryData<string> Variantes() => [.. _bloques.Keys];

    private static (string Bloque, string Frase)[] TarifaUnica(string folio, string tarifa, (string Bloque, string Frase) notaPropia) =>
    [
        ("encabezado", Logo),
        ("pie", "Principal: Carrera 42"),
        ("fecha", "Yumbo, 19 de marzo"),
        ("referencia", folio),
        ("asunto", "Asunto: presentación"),
        ("tituloTarifa", "NUESTRA TARIFA PARA"),
        ("origenTabla", "ORIGEN Y/O LUGAR"),
        ("encabezadoTabla", "DESTINO TIPO DE"),
        ("filaTarifa", tarifa),
        ("nota", "Nota: Las tarifas"),
        notaPropia,
        ("incluyen", "Estas tarifas incluyen"),
        ("noIncluyen", "Estas tarifas no incluyen"),
        ("condiciones", "Facturación y forma"),
        ("vigencia", "Vigencia"),
        ("firma", "Cordialmente,"),
        ("firmante", "Nathaly Vargas Garcia"),
    ];

    private static clsLectorPdf Leer(string variante)
    {
        if (variante == "Pliego")
        {
            var (cotizacion, _) = clsPdfPliegoTests.Pliego(3);
            return clsPdfPliegoTests.Generar(cotizacion).Lector;
        }

        clsDocumentoCotizacion documento = variante switch
        {
            "Nacional" => clsDocumentosPrueba.Nacional(),
            "Internacional" => clsDocumentosPrueba.Internacional(),
            "Dolares" => clsDocumentosPrueba.Dolares(),
            "Multi" => clsDocumentosPrueba.Multi(),
            _ => throw new ArgumentOutOfRangeException(nameof(variante), variante, "Variante sin documento de prueba."),
        };
        return clsLectorPdf.Leer(_generador.Generar(documento).Contenido);
    }

    /// <summary>Posición de cada bloque de la variante; falla si una frase ya no aparece.</summary>
    private static SortedDictionary<string, clsPosicionBloque> Medir(string variante)
    {
        var lector = Leer(variante);
        var medidas = new SortedDictionary<string, clsPosicionBloque>(StringComparer.Ordinal);
        foreach (var (bloque, frase) in _bloques[variante])
        {
            if (frase == Logo)
            {
                lector.Imagenes.Count.Should().BePositive();
                lector.Imagenes[0].Should().NotBeEmpty($"{variante}: la hoja 1 lleva el logo");
                medidas[bloque] = new clsPosicionBloque(frase, 1, Math.Round(lector.Imagenes[0][0].X, 2), Math.Round(lector.Imagenes[0][0].Y, 2));
                continue;
            }

            var ubicacion = lector.Ubicar(frase);
            ubicacion.Should().NotBeNull($"{variante}: el bloque '{bloque}' empieza con \"{frase}\"");
            var (pagina, x, y) = ubicacion!.Value;
            medidas[bloque] = new clsPosicionBloque(frase, pagina, Math.Round(x, 2), Math.Round(y, 2));
        }

        return medidas;
    }

    /// <summary>Bloques que faltan, cambian de hoja o se corren más de <see cref="Tolerancia"/> pt respecto a la referencia.</summary>
    private static List<string> Desviaciones(IDictionary<string, clsPosicionBloque> medidas, IDictionary<string, clsPosicionBloque> esperadas)
    {
        var desviaciones = new List<string>();
        foreach (var (bloque, esperada) in esperadas)
        {
            if (!medidas.TryGetValue(bloque, out var medida) || medida.Frase != esperada.Frase)
            {
                desviaciones.Add($"{bloque}: no se midió \"{esperada.Frase}\"");
            }
            else if (medida.Pagina != esperada.Pagina || Math.Abs(medida.X - esperada.X) > Tolerancia || Math.Abs(medida.Y - esperada.Y) > Tolerancia)
            {
                desviaciones.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{bloque} (\"{esperada.Frase}\"): hoja {medida.Pagina} ({medida.X}, {medida.Y}) pt, referencia hoja {esperada.Pagina} ({esperada.X}, {esperada.Y}) pt"));
            }
        }

        desviaciones.AddRange(medidas.Keys.Except(esperadas.Keys).Select(b => $"{b}: sin posición de referencia"));
        return desviaciones;
    }

    [Theory]
    [MemberData(nameof(Variantes))]
    public void LosBloquesClaveQuedanDondeDiceLaReferencia(string variante)
    {
        var referencia = LeerReferencia();
        referencia.Should().ContainKey(variante, $"falta la referencia de {variante}: regenérala (ver remarks de la clase)");

        Desviaciones(Medir(variante), referencia[variante]).Should()
            .BeEmpty($"{variante}: ningún bloque clave se corre más de {Tolerancia} pt (si el cambio es intencional, regenera la referencia)");
    }

    [Fact]
    public void UnBloqueCorridoSeDetectaAunqueElTextoSigaIgual()
    {
        // Un contacto más en la carta baja el asunto y lo que sigue unos 14 pt: la referencia lo detecta.
        var payload = clsDocumentosPrueba.Payload();
        payload.Contactos = [.. payload.Contactos!, "Sr. Contacto Adicional"];
        var lector = clsLectorPdf.Leer(_generador.Generar(clsDocumentosPrueba.Documento(payload)).Contenido);
        var (pagina, x, y) = lector.Ubicar("Asunto: presentación")!.Value;
        var medidas = new Dictionary<string, clsPosicionBloque>(LeerReferencia()["Nacional"], StringComparer.Ordinal)
        {
            ["asunto"] = new clsPosicionBloque("Asunto: presentación", pagina, x, y),
        };

        Desviaciones(medidas, LeerReferencia()["Nacional"]).Should().ContainSingle().Which.Should().StartWith("asunto ");
    }

    /// <summary>Regenera la referencia (solo a mano, con <c>COTIFLY_REGENERAR_MAQUETACION=1</c> y fuera de CI). Ver remarks.</summary>
    [Fact]
    public void RegeneraLaReferenciaDeMaquetacion()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(VariableRegenerar) == "1", $"Solo a mano: {VariableRegenerar}=1.");
        Environment.GetEnvironmentVariable("CI").Should().BeNullOrEmpty("la referencia nunca se regenera en CI");

        var nueva = new SortedDictionary<string, SortedDictionary<string, clsPosicionBloque>>(StringComparer.Ordinal);
        foreach (var variante in _bloques.Keys)
        {
            nueva[variante] = Medir(variante);
        }

        var ruta = RutaEnRepo();
        File.WriteAllText(ruta, JsonSerializer.Serialize(nueva, _json) + "\n");
        Assert.Fail($"Referencia de maquetación regenerada en {ruta}: revisa el diff y versiónala con el cambio de diseño.");
    }

    private static Dictionary<string, Dictionary<string, clsPosicionBloque>> LeerReferencia()
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "fixtures", "pdf", Archivo);
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, clsPosicionBloque>>>(File.ReadAllText(ruta))
               ?? throw new InvalidOperationException($"{ruta} está vacío.");
    }

    /// <summary><c>backend-dotnet/tests/fixtures/pdf/maquetacion-referencia.json</c> en el repo (no la copia de bin/).</summary>
    private static string RutaEnRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cotifly.sln")))
            {
                return Path.Combine(Directory.CreateDirectory(Path.Combine(dir.FullName, "tests", "fixtures", "pdf")).FullName, Archivo);
            }
        }

        throw new InvalidOperationException("No se encontró Cotifly.sln: regenera la referencia desde el repo.");
    }
}

/// <summary>Posición de referencia de un bloque: frase con la que empieza, hoja (base 1) y esquina inferior izquierda (pt).</summary>
public sealed record clsPosicionBloque(string Frase, int Pagina, double X, double Y);
