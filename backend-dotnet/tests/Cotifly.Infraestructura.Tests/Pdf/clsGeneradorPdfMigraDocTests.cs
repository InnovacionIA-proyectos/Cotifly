using System.Text;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// Render real con PDFsharp-MigraDoc y lectura con PdfPig (texto por página, fuentes, imágenes). Port de
/// <c>test_render_pdf_genera_archivo_y_paginas</c> y de las comprobaciones de ADR-006: 3 páginas, datos clave, "Cel" solo
/// con teléfono, bloque aduanero solo en Internacional, variante multi, fuentes embebidas (solo Liberation Sans),
/// texto literal sin HTML. Deja los 3 PDF de la comparación visual de #11 en <c>tests/artefactos/</c> (no versionados).
/// </summary>
public sealed class clsGeneradorPdfMigraDocTests
{
    private static readonly clsGeneradorPdfMigraDoc _generador = new();

    private static (clsPdfGenerado Pdf, clsLectorPdf Lector) Generar(clsDocumentoCotizacion documento)
    {
        var pdf = _generador.Generar(documento);
        return (pdf, clsLectorPdf.Leer(pdf.Contenido));
    }

    // HU-012.3: PDF en dólares con la aclaración del piso mínimo bajo la tabla; en pesos, sin ninguna aclaración de TRM.
    [Fact]
    public void EnDolaresLaTarifaVaEnUsdConLaAclaracionDelPisoMinimo()
    {
        var (pdf, lector) = Generar(clsDocumentosPrueba.Dolares());

        pdf.Paginas.Should().Be(3, "la aclaración cabe en la página de la tarifa");
        var tarifa = lector.Paginas[1];
        tarifa.Should().Contain("US$996,35");
        tarifa.Should().Contain("Cotización en dólares:");
        tarifa.Should().Contain("TRM de $3.306,86 por dólar");
        tarifa.Should().Contain("se ajusta día a día con la TRM vigente");
        tarifa.Should().Contain("el mínimo a pagar es $3.294.798, el valor cotizado originalmente en pesos");
        tarifa.IndexOf("Cotización en dólares:", StringComparison.Ordinal).Should()
            .BeGreaterThan(tarifa.IndexOf("US$996,35", StringComparison.Ordinal), "la aclaración va debajo de la tabla");
    }

    [Fact]
    public void EnPesosElPdfNoTraeAclaracionDeTrm()
    {
        var (_, lector) = Generar(clsDocumentosPrueba.Nacional());

        lector.Texto.Should().NotContain("TRM").And.NotContain("Cotización en dólares").And.NotContain("US$");
    }

    [Fact]
    public void LaVarianteMultiEnDolaresTambienTraeLaAclaracion()
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.Secciones = [payload.Tarifa!];
        payload.Tarifa = null;
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, "PCN-USD-2", clsDocumentosPrueba.Firma, new DateOnly(2026, 9, 27), "USD", 3306.86m, 3_294_798m).Valor;

        var (_, lector) = Generar(documento);

        lector.Texto.Should().Contain("Cotización en dólares:").And.Contain("TRM de $3.306,86");
    }

    // test_render_pdf_genera_archivo_y_paginas
    [Fact]
    public void LaSolicitudDeEjemploTieneTresPaginasConLosBloquesEnSuSitio()
    {
        var (pdf, lector) = Generar(clsDocumentosPrueba.Nacional());

        pdf.Paginas.Should().Be(3);
        lector.Paginas.Should().HaveCount(3);
        pdf.Contenido.AsSpan(0, 5).ToArray().Should().Equal("%PDF-"u8.ToArray());

        var carta = lector.Paginas[0];
        carta.Should().Contain("Yumbo, 19 de marzo de 2026");
        carta.Should().Contain("PCN-19032026");
        carta.Should().Contain("LONGYIN");
        carta.Should().Contain("Sr. Nicolás Alarcon");
        carta.Should().Contain("Asunto: presentación oferta de servicios de Transporte Nacional");
        carta.Should().Contain("Transporte nacional seguro, confiable y a tiempo en todo el país.");

        var tarifa = lector.Paginas[1];
        tarifa.Should().Contain("NUESTRA TARIFA PARA SU SOLICITUD ES:");
        tarifa.Should().Contain("TRANSPORTE NACIONAL EN CALIDAD DE EXPORTACIONES:");
        tarifa.Should().Contain("ORIGEN Y/O LUGAR DE CARGUE: YUMBO");
        tarifa.Should().Contain("TIPO DE VEHÍCULO Y PESO");
        tarifa.Should().Contain("$3.000.000");
        tarifa.Should().Contain("$3.600.000");
        tarifa.Should().Contain("Tiempos logísticos:");
        tarifa.Should().Contain("Decreto 16075");
        tarifa.Should().Contain("Estas tarifas incluyen");
        tarifa.Should().Contain("Candado satelital");
        tarifa.Should().Contain("Estas tarifas no incluyen");

        var condiciones = lector.Paginas[2];
        condiciones.Should().Contain("Facturación y forma de pago");
        condiciones.Should().Contain("Seguro");
        condiciones.Should().Contain("Horas logísticas adicionales");
        condiciones.Should().Contain("Vigencia");
        condiciones.Should().Contain("Para conocer más de nuestra organización, ingresa a:");
        condiciones.Should().Contain("Cordialmente,");
        condiciones.Should().Contain("Nathaly Vargas Garcia");
        condiciones.Should().Contain("Ejecutiva Comercial");
        condiciones.Should().Contain("nvargas@boterosoto.com.co");
        condiciones.Should().Contain("Cel 313 415 6187");

        lector.Paginas.Should().OnlyContain(p => p.Contains("Nit 890.901.321-5", StringComparison.Ordinal), "el pie se repite en cada página");
        lector.Paginas.Should().OnlyContain(p => p.Contains("www.boterosoto.com.co", StringComparison.Ordinal));
    }

    [Fact]
    public void SinTelefonoNoSeImprimeCel()
    {
        var (_, lector) = Generar(clsDocumentosPrueba.Nacional(clsDocumentosPrueba.Firma with { Celular = null }));

        lector.Texto.Should().NotContain("Cel");
        lector.Texto.Should().NotContain("3134156187");
    }

    // test_bloque_aduanero_solo_internacional
    [Fact]
    public void ElBloqueAduaneroSoloApareceEnInternacional()
    {
        Generar(clsDocumentosPrueba.Nacional()).Lector.Texto.Should().NotContain("Datos aduaneros");

        var (_, lector) = Generar(clsDocumentosPrueba.Internacional());

        lector.Texto.Should().Contain("Detalle adicional del servicio");
        lector.Texto.Should().Contain("Datos aduaneros (ANDINO)");
        lector.Texto.Should().Contain("Ipiales - Rumichaca");
        lector.Texto.Should().Contain("Mercancía extradimensionada");
        lector.Texto.Should().Contain("Mercancía refrigerada");
        lector.Texto.Should().Contain("-18 °C");
        lector.Texto.Should().Contain("Levante de contenedor: $350.000");
        lector.Texto.Should().Contain("Flete BOGOTÁ – QUITO");
        lector.Texto.Should().Contain("$10.365.854", "8.500.000 / (1 − 0.18) redondeado half-to-even");
        lector.Texto.Should().Contain("Días libres");
        lector.Texto.Should().Contain("Operación internacional andina con acompañamiento y seguridad en frontera.");
    }

    // test_multi_seccion_usa_armar_multi
    [Fact]
    public void LaVarianteMultiTieneDosPaginasConLasCondicionesEnLaPrimera()
    {
        var (pdf, lector) = Generar(clsDocumentosPrueba.Multi());

        pdf.Paginas.Should().Be(2);
        lector.Paginas[0].Should().Contain("Respaldamos nuestra oferta integral");
        lector.Paginas[0].Should().Contain("Cobertura Integral: Presencia estratégica");
        lector.Paginas[0].Should().NotContain("· Cobertura", "las viñetas del texto se dibujan como viñetas");
        lector.Paginas[0].Should().Contain("Stand by");
        lector.Paginas[0].Should().Contain("Facturación y forma de pago");
        lector.Paginas[1].Should().Contain("TRAMO 1");
        lector.Paginas[1].Should().Contain("TRAMO 2");
        lector.Paginas[1].Should().Contain("DESTINO / SITIO").And.Contain("ENTREGA", "el encabezado de la columna ocupa dos líneas");
        lector.Paginas[1].Should().Contain("Nota: Tarifa sujeta a disponibilidad de equipo.");
        lector.Paginas[1].Should().Contain("$1.200.000");
        lector.Paginas[1].Should().Contain("Cordialmente,");
        lector.Paginas[1].Should().Contain("Para conocer más de nuestra organización, ingresa a: www.boterosoto.com.co");
        lector.Texto.Should().NotContain("Tiempos logísticos:", "la variante multi no lleva la nota de decretos");
    }

    [Fact]
    public void LasFuentesVanEmbebidasYSonSoloLiberationSans()
    {
        var (pdf, lector) = Generar(clsDocumentosPrueba.Nacional());

        lector.Fuentes.Should().NotBeEmpty();
        lector.Fuentes.Should().OnlyContain(f => f.Contains("Liberation Sans", StringComparison.Ordinal), "ninguna fuente base-14 ni del sistema");
        lector.Fuentes.Should().Contain(f => f.EndsWith("Liberation Sans,Bold", StringComparison.Ordinal));
        lector.Fuentes.Should().OnlyContain(f => f.Length > 7 && f[6] == '+', "fuentes embebidas como subconjunto (prefijo ABCDEF+)");
        Encoding.Latin1.GetString(pdf.Contenido).Should().Contain("/FontFile2", "las fuentes TrueType van dentro del PDF");
    }

    [Fact]
    public void ElLogoVaEnCadaPaginaYLaImagenDeLaLineaEnLaCarta()
    {
        var (_, lector) = Generar(clsDocumentosPrueba.Nacional());

        lector.ImagenesPorPagina.Should().Equal(2, 1, 1);
    }

    [Fact]
    public void ElTextoDelPayloadSeImprimeLiteralSinInterpretarse()
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.Cliente = "<script>alert(1)</script>";

        Generar(clsDocumentosPrueba.Documento(payload)).Lector.Texto.Should().Contain("<script>alert(1)</script>");
    }

    [Fact]
    public void UnaTablaLargaContinuaEnLaPaginaSiguienteSinPerderFilas()
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.Tarifa!.Filas = [.. Enumerable.Range(1, 40).Select(i => new clsFilaTarifaPayload { Destino = $"D{i:00}", VehiculoPeso = "Turbo", Tarifa = i * 100_000m, Nota = "Nota de la fila." })];

        var (pdf, lector) = Generar(clsDocumentosPrueba.Documento(payload));

        pdf.Paginas.Should().BeGreaterThan(3);
        foreach (var i in Enumerable.Range(1, 40))
        {
            lector.Texto.Should().Contain($"D{i:00}");
        }
    }

    [Fact]
    public void UnaPalabraDe300CaracteresSeParteSinEsconderLaTarifaNiAgregarPaginas()
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.Tarifa!.Filas!.RemoveAt(1);
        payload.Tarifa.Filas[0].Destino = new string('Ø', 40);
        payload.Tarifa.Filas[0].VehiculoPeso = new string('Þ', 60);
        payload.Tarifa.Filas[0].Nota = new string('Ð', 300);

        var (pdf, lector) = Generar(clsDocumentosPrueba.Documento(payload));

        pdf.Paginas.Should().Be(3, "la fila se parte dentro de sus celdas y la tarifa sigue en la página 2");
        lector.Paginas[1].Should().Contain("$3.000.000");
        Cuenta(lector.Paginas[1], 'Ð').Should().Be(300, "el texto no se pierde, solo se parte en líneas");
        Cuenta(lector.Paginas[1], 'Ø').Should().Be(40);
        Cuenta(lector.Paginas[1], 'Þ').Should().Be(60);
    }

    [Fact]
    public void UnDestinoDe300CaracteresSinEspaciosNoEscondeLaTarifa()
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.Tarifa!.Filas!.RemoveAt(1);
        payload.Tarifa.Filas[0].Destino = new string('Ø', 300);
        payload.Tarifa.Filas[0].VehiculoPeso = new string('Þ', 300);

        var (_, lector) = Generar(clsDocumentosPrueba.Documento(payload));

        // La columna "Destino" es angosta: 300 caracteres ocupan más de media hoja, pero la tarifa no queda tapada.
        lector.Texto.Should().Contain("$3.000.000");
        Cuenta(lector.Texto, 'Ø').Should().Be(300);
    }

    private static int Cuenta(string texto, char letra) => texto.Count(c => c == letra);

    [Fact]
    public void ElRenderEsEstableEnPaginasYTexto()
    {
        var primero = Generar(clsDocumentosPrueba.Nacional()).Lector;
        var segundo = Generar(clsDocumentosPrueba.Nacional()).Lector;

        segundo.Paginas.Should().Equal(primero.Paginas);
    }

    [Fact]
    public async Task GeneraLosPdfDeLaComparacionVisual()
    {
        var carpeta = CarpetaArtefactos();
        var casos = new (string Nombre, clsDocumentoCotizacion Documento)[]
        {
            ("dotnet-1-nacional-PCN-19032026.pdf", clsDocumentosPrueba.Nacional()),
            ("dotnet-2-internacional-PCN-24092026.pdf", clsDocumentosPrueba.Internacional()),
            ("dotnet-3-multiseccion-PCN-19032026-v2.pdf", clsDocumentosPrueba.Multi()),
            ("dotnet-4-dolares-PCN-USD-1.pdf", clsDocumentosPrueba.Dolares()),
        };

        foreach (var (nombre, documento) in casos)
        {
            var pdf = _generador.Generar(documento);
            pdf.Paginas.Should().BeGreaterThan(0);
            if (carpeta is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(carpeta, nombre), pdf.Contenido, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary><c>backend-dotnet/tests/artefactos/</c> si se encuentra la solución (en el repo), si no <c>null</c>.</summary>
    private static string? CarpetaArtefactos()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cotifly.sln")))
            {
                return Directory.CreateDirectory(Path.Combine(dir.FullName, "tests", "artefactos")).FullName;
            }
        }

        return null;
    }
}
