using System.Diagnostics;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Mercancias.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.Mercancias;

/// <summary>
/// Lectura local del Excel de mercancías (ADR-024): la grilla cruda con formato estable, el encabezado detectado, los
/// topes con su truncado y los archivos corruptos o maliciosos, que terminan en un error de negocio (nunca una excepción) rápido y con
/// memoria acotada.
/// </summary>
public sealed class clsLectorGrillaExcelTests : IDisposable
{
    private static readonly TimeSpan _tiempoMaximo = TimeSpan.FromSeconds(10);
    private readonly clsLecturaExcelAcotada _lectura = new(NullLogger<clsLecturaExcelAcotada>.Instance);
    private readonly clsLectorGrillaExcelClosedXml _lector;

    public clsLectorGrillaExcelTests() => _lector = new(_lectura, NullLogger<clsLectorGrillaExcelClosedXml>.Instance);

    /// <summary>Un lector con su propio cupo de <paramref name="simultaneos"/> y una espera de 50 ms (pruebas del cupo).</summary>
    private static (clsLecturaExcelAcotada Lectura, clsLectorGrillaExcelClosedXml Lector) ConCupo(int simultaneos)
    {
        var lectura = new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance, TimeSpan.FromMilliseconds(50), simultaneos);
        return (lectura, new clsLectorGrillaExcelClosedXml(lectura, NullLogger<clsLectorGrillaExcelClosedXml>.Instance));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lectura.Dispose();

    private async Task<clsAnalisisExcelMercancias> LeerAsync(byte[] excel, string nombre = "mercancias.xlsx")
    {
        var resultado = await _lector.LeerAsync(nombre, excel, Ct);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? $"{resultado.Error.Codigo}: {resultado.Error.Mensaje} ({resultado.Error.Detalle})" : string.Empty);
        return resultado.Valor;
    }

    private async Task<clsErrorDominio> FallaAsync(byte[] archivo)
    {
        var reloj = Stopwatch.StartNew();
        var resultado = await _lector.LeerAsync("mercancias.xlsx", archivo, Ct);
        reloj.Elapsed.Should().BeLessThan(_tiempoMaximo);
        resultado.EsFallo.Should().BeTrue();
        return resultado.Error;
    }

    private static byte[] Libro(Action<XLWorkbook> armar)
    {
        using var libro = new XLWorkbook();
        armar(libro);
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    [Fact]
    public async Task LeeLaGrillaConElEncabezadoLasFilasYSusNumerosDeFilaDeExcel()
    {
        var analisis = await LeerAsync(clsExcelMercanciasMuestra.Excel(3));

        analisis.Archivo.Should().Be("mercancias.xlsx");
        analisis.TotalHojas.Should().Be(1);
        analisis.HojasTruncadas.Should().BeFalse();
        analisis.CatalogoMercanciasDisponible.Should().BeFalse("el lector no consulta el catálogo");
        var hoja = analisis.Hojas.Single();
        hoja.Nombre.Should().Be(clsExcelMercanciasMuestra.Hoja);
        hoja.FilaEncabezado.Should().Be(1);
        hoja.Encabezados.Should().Equal(clsExcelMercanciasMuestra.Encabezados);
        hoja.Filas.Should().HaveCount(3);
        hoja.Filas[0].Should().Equal("Cafe en estiba", "10", "1.2", "1", "1.5", "850.5");
        hoja.Filas[2].Should().Equal("Cajas de electrodomésticos", "40", "0.8", "0.6", "0.9", "25");
        hoja.FilasExcel.Should().Equal(2, 3, 4);
        (hoja.TotalFilas, hoja.FilasTruncadas, hoja.TotalColumnas, hoja.ColumnasTruncadas, hoja.CeldasRecortadas).Should().Be((3, false, 6, false, false));
        hoja.Mercancias.Should().BeNull();
    }

    [Fact]
    public async Task SaltaElTituloLasFilasVaciasYLasHojasSinDatosYRellenaLasCeldasVacias()
    {
        var excel = Libro(l =>
        {
            var ws = l.Worksheets.Add("Pedido");
            ws.Cell("B1").Value = "MERCANCÍAS DEL PEDIDO 4512";
            ws.Range("B1:F1").Merge();
            ws.Cell("B3").Value = "Producto";
            ws.Cell("C3").Value = "Cant.";
            ws.Cell("D3").Value = "Peso";
            ws.Cell("F3").Value = "Notas";
            ws.Cell("B4").Value = "Tubos";
            ws.Cell("D4").Value = 120;
            ws.Cell("B7").Value = "Láminas";
            ws.Cell("C7").Value = 3;
            l.Worksheets.Add("Vacía");
        });

        var analisis = await LeerAsync(excel);

        analisis.TotalHojas.Should().Be(2);
        var hoja = analisis.Hojas.Should().ContainSingle("la hoja vacía no aparece").Subject;
        hoja.FilaEncabezado.Should().Be(3, "el título combinado tiene un solo valor");
        hoja.Encabezados.Should().Equal("Producto", "Cant.", "Peso", string.Empty, "Notas");
        hoja.Filas.Should().HaveCount(2);
        hoja.Filas[0].Should().Equal("Tubos", string.Empty, "120", string.Empty, string.Empty);
        hoja.Filas[1].Should().Equal("Láminas", "3", string.Empty, string.Empty, string.Empty);
        hoja.FilasExcel.Should().Equal(4, 7);
    }

    [Fact]
    public async Task SinFilaDeTresValoresElEncabezadoEsLaPrimeraFilaConDatos()
    {
        var excel = Libro(l =>
        {
            var ws = l.Worksheets.Add("Dos columnas");
            ws.Cell("A1").Value = "Tipo";
            ws.Cell("B1").Value = "Peso";
            ws.Cell("A2").Value = "Café";
            ws.Cell("B2").Value = 10;
        });

        var hoja = (await LeerAsync(excel)).Hojas.Single();

        hoja.FilaEncabezado.Should().Be(1);
        hoja.Encabezados.Should().Equal("Tipo", "Peso");
        hoja.Filas.Single().Should().Equal("Café", "10");
    }

    [Fact]
    public async Task UnaFormulaTraeElValorGuardadoYLasFechasNumerosYTextosTienenFormatoEstable()
    {
        var hoja = (await LeerAsync(clsExcelMercanciasMuestra.ConFormulaYFecha())).Hojas.Single();

        hoja.Filas.Single().Should().Equal("Bobinas", "2", "3", "2", "1", "2000", "2026-10-20");
    }

    [Fact]
    public async Task UnaFormulaSinValorGuardadoNoSeEvaluaYLlegaVacia()
    {
        var excel = clsExcelMercanciasMuestra.ConFormula(clsExcelMercanciasMuestra.Excel(1), clsExcelMercanciasMuestra.Hoja, "F2", "B2*1000", conValorGuardado: false);

        var hoja = (await LeerAsync(excel)).Hojas.Single();

        hoja.Filas.Single()[5].Should().BeEmpty("Cotifly nunca evalúa fórmulas: solo devuelve el valor que Excel guardó");
    }

    [Fact]
    public async Task CadaTipoDeCeldaTieneUnTextoEstable()
    {
        var excel = Libro(l =>
        {
            var ws = l.Worksheets.Add("Tipos");
            string[] encabezados = ["Texto", "Decimal", "Porcentaje", "Grande", "Booleano", "Fecha y hora", "Duración", "Negativo"];
            for (var c = 0; c < encabezados.Length; c++)
            {
                ws.Cell(1, c + 1).Value = encabezados[c];
            }

            ws.Cell(2, 1).Value = "  Peso\r\n(kg)\tbruto  ";
            ws.Cell(2, 2).Value = 0.1 + 0.2;
            ws.Cell(2, 2).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(2, 3).Value = 0.15;
            ws.Cell(2, 3).Style.NumberFormat.Format = "0%";
            ws.Cell(2, 4).Value = 1234567.5;
            ws.Cell(2, 5).Value = true;
            ws.Cell(2, 6).Value = new DateTime(2026, 10, 20, 14, 30, 5, DateTimeKind.Unspecified);
            ws.Cell(2, 7).Value = TimeSpan.FromMinutes(90);
            ws.Cell(2, 8).Value = -42;
        });

        var fila = (await LeerAsync(excel)).Hojas.Single().Filas.Single();

        fila.Should().Equal("Peso (kg) bruto", "0.3", "0.15", "1234567.5", "true", "2026-10-20T14:30:05", "01:30:00", "-42");
    }

    [Fact]
    public async Task UnaHojaDeMasDeMilFilasLlegaTruncadaConElTotalReal()
    {
        var hoja = (await LeerAsync(clsExcelMercanciasMuestra.Excel(1005))).Hojas.Single();

        hoja.Filas.Should().HaveCount(clsLectorGrillaExcelClosedXml.MaximoFilas);
        hoja.FilasExcel[^1].Should().Be(1001);
        hoja.TotalFilas.Should().Be(1005);
        hoja.FilasTruncadas.Should().BeTrue();
    }

    [Fact]
    public async Task LasColumnasPasadasDelTopeYLasCeldasLargasSeRecortanYLoDice()
    {
        var excel = Libro(l =>
        {
            var ws = l.Worksheets.Add("Ancha");
            for (var c = 1; c <= 60; c++)
            {
                ws.Cell(1, c).Value = $"Col {c}";
                ws.Cell(2, c).Value = c;
            }

            ws.Cell(2, 1).Value = new string('x', 2000);
        });

        var hoja = (await LeerAsync(excel)).Hojas.Single();

        hoja.Encabezados.Should().HaveCount(clsLectorGrillaExcelClosedXml.MaximoColumnas);
        hoja.Encabezados[^1].Should().Be("Col 50");
        hoja.TotalColumnas.Should().Be(60);
        hoja.ColumnasTruncadas.Should().BeTrue();
        hoja.Filas.Single()[0].Should().HaveLength(clsLectorGrillaExcelClosedXml.MaximoLargoCelda);
        hoja.CeldasRecortadas.Should().BeTrue();
    }

    [Fact]
    public async Task UnLibroDeMasDeVeinteHojasSoloLeeLasVeintePrimeras()
    {
        var excel = Libro(l =>
        {
            for (var h = 1; h <= 23; h++)
            {
                var ws = l.Worksheets.Add($"H{h}");
                ws.Cell("A1").Value = "Tipo";
                ws.Cell("B1").Value = "Cantidad";
                ws.Cell("C1").Value = "Peso";
                ws.Cell("A2").Value = $"Mercancía {h}";
            }
        });

        var analisis = await LeerAsync(excel);

        analisis.Hojas.Should().HaveCount(clsLectorGrillaExcelClosedXml.MaximoHojas);
        analisis.Hojas[^1].Nombre.Should().Be("H20");
        analisis.TotalHojas.Should().Be(23);
        analisis.HojasTruncadas.Should().BeTrue();
    }

    [Fact]
    public void ElPresupuestoDeCeldasCortaLasFilasDeLasHojasSiguientes()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsExcelMercanciasMuestra.Excel(10)));
        var presupuesto = new clsLectorGrillaExcelClosedXml.clsPresupuesto(6 + (6 * 4));

        var hoja = clsLectorGrillaExcelClosedXml.LeerHoja(libro.Worksheet(clsExcelMercanciasMuestra.Hoja), presupuesto, Ct)!;

        hoja.Filas.Should().HaveCount(4, "el encabezado y 4 filas de 6 celdas agotan las 30 celdas");
        hoja.TotalFilas.Should().Be(10);
        hoja.FilasTruncadas.Should().BeTrue();
        presupuesto.Restantes.Should().Be(0);
    }

    [Fact]
    public async Task UnLibroSinDatosEsExcelSinDatos() =>
        (await FallaAsync(clsExcelMercanciasMuestra.SinDatos())).Codigo.Should().Be(clsCodigosError.ExcelSinDatos);

    public static TheoryData<string, string, string> Corruptos() => new()
    {
        { "texto", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoNoEsXlsx },
        { "ole", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoCifradoOXls },
        { "zip sin libro", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoSinLibro },
        { "truncado", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoZipInvalido },
        { "libro ilegible", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "bomba zip", clsCodigosError.ArchivoDemasiadoGrande, clsLecturaExcelAcotada.MotivoDescomprimido },
    };

    [Theory]
    [MemberData(nameof(Corruptos))]
    public async Task UnArchivoCorruptoOMaliciosoEsUnErrorDeNegocioConSuMotivo(string caso, string codigo, string motivo)
    {
        byte[] archivo = caso switch
        {
            "texto" => clsExcelMercanciasMuestra.TextoRenombrado(),
            "ole" => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0],
            "zip sin libro" => clsExcelMercanciasMuestra.ZipSinLibro(),
            "truncado" => clsExcelMercanciasMuestra.Truncado(),
            "libro ilegible" => clsExcelMercanciasMuestra.LibroIlegible(),
            _ => clsExcelMercanciasMuestra.BombaZip(),
        };

        var error = await FallaAsync(archivo);

        error.Codigo.Should().Be(codigo, caso);
        error.Detalle.Should().Be(motivo, caso);
    }

    [Fact]
    public void LaBombaZipSeDetieneAlPasarElTopeSinCargarElLibro()
    {
        var bomba = clsExcelMercanciasMuestra.BombaZip(200);
        bomba.Length.Should().BeLessThan(1024 * 1024, "comprimida ocupa poco: es la gracia de una bomba zip");
        GC.Collect();
        var antes = GC.GetAllocatedBytesForCurrentThread();

        var error = clsLecturaExcelAcotada.VerificarPaquete(bomba, Ct);

        error!.Codigo.Should().Be(clsCodigosError.ArchivoDemasiadoGrande);
        (GC.GetAllocatedBytesForCurrentThread() - antes).Should().BeLessThan(16L * 1024 * 1024, "se descomprime por bloques, sin guardar el contenido");
    }

    [Fact]
    public async Task ConElCupoLlenoRespondeServicioOcupadoSinLeer()
    {
        var (lectura, lector) = ConCupo(simultaneos: 1);
        using var _ = lectura;
        var cupo = lectura.Cupo;
        await cupo.WaitAsync(Ct);

        var resultado = await lector.LeerAsync("mercancias.xlsx", clsExcelMercanciasMuestra.Excel(1), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        cupo.Release();
        (await lector.LeerAsync("mercancias.xlsx", clsExcelMercanciasMuestra.Excel(1), Ct)).EsExitoso.Should().BeTrue("al liberarse el cupo se lee");
    }

    // ---- Validación: densidad, XXE, cupo con cancelación, fechas 1904, combinadas, hojas ocultas y recorte con emoji.

    [Fact]
    public async Task UnLibroPequenoPeroMuyDensoSeRechazaSinCargarloEnClosedXml()
    {
        // 4 millones de celdas mínimas: unos 200 KB comprimidos, 60 MB descomprimidos (pasa el tope de bytes) y más de 1 GB en ClosedXML.
        var densa = clsExcelMercanciasMuestra.Densa(4_000_000);
        densa.Length.Should().BeLessThan(1024 * 1024);
        clsLecturaExcelAcotada.VerificarPaquete(densa, Ct).Should().BeNull("los bytes descomprimidos caben en los 64 MB");
        GC.Collect();
        var antes = GC.GetAllocatedBytesForCurrentThread();

        var error = await FallaAsync(densa);

        error.Codigo.Should().Be(clsCodigosError.ArchivoDemasiadoGrande);
        error.Detalle.Should().Be(clsLecturaExcelAcotada.MotivoDescomprimido);
        (GC.GetAllocatedBytesForCurrentThread() - antes).Should().BeLessThan(256L * 1024 * 1024, "se cuenta en flujo, sin abrir el libro en ClosedXML");
    }

    [Fact]
    public async Task UnLibroDensoDentroDelTopeSeLee()
    {
        var hoja = (await LeerAsync(clsExcelMercanciasMuestra.Densa(200_000))).Hojas.Single();

        (hoja.TotalFilas, hoja.Filas.Count, hoja.FilasTruncadas, hoja.TotalColumnas).Should().Be((3_999, 1_000, true, 50));
    }

    [Fact]
    public void ElConteoDeElementosNoResuelveEntidadesExternas() =>
        clsLecturaExcelAcotada.VerificarElementos(clsExcelMercanciasMuestra.ConEntidadExterna(), Ct)!.Detalle
            .Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible, "un DTD no se procesa: el archivo no se lee");

    [Fact]
    public async Task UnaEntidadExternaNuncaLlegaALaRespuesta()
    {
        var resultado = await _lector.LeerAsync("mercancias.xlsx", clsExcelMercanciasMuestra.ConEntidadExterna(), Ct);

        resultado.EsFallo.Should().BeTrue();
        (resultado.Error.Codigo, resultado.Error.Detalle).Should().Be((clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoLibroIlegible));
    }

    [Fact]
    public async Task LaCancelacionDuranteLaLecturaLiberaElCupo()
    {
        var (lectura, lector) = ConCupo(simultaneos: 1);
        using var _ = lectura;
        var excel = clsExcelMercanciasMuestra.Densa(300_000);
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelacion.CancelAfter(TimeSpan.FromMilliseconds(30));

        var leer = () => lector.LeerAsync("mercancias.xlsx", excel, cancelacion.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>();
        (await lector.LeerAsync("mercancias.xlsx", clsExcelMercanciasMuestra.Excel(1), Ct)).EsExitoso.Should().BeTrue("el cupo de 1 se liberó");
    }

    [Fact]
    public async Task UnLibroConElSistemaDeFechas1904TraeLaFechaCorrecta()
    {
        var excel = clsExcelMercanciasMuestra.ConFormulaYFecha();
        using var memoria = new MemoryStream();
        memoria.Write(excel);
        using (var documento = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(memoria, true))
        {
            var libro = documento.WorkbookPart!.Workbook;
            libro.WorkbookProperties ??= new DocumentFormat.OpenXml.Spreadsheet.WorkbookProperties();
            libro.WorkbookProperties.Date1904 = true;
            libro.Save();
        }

        var hoja = (await LeerAsync(memoria.ToArray())).Hojas.Single();

        hoja.Filas.Single()[6].Should().Be("2030-10-21", "el mismo número de serie, contado desde 1904, son 1462 días más");
    }

    [Fact]
    public async Task LasCeldasCombinadasTraenElValorSoloEnLaPrimeraYLasHojasOcultasSeLeenMarcadas()
    {
        var excel = Libro(l =>
        {
            var oculta = l.Worksheets.Add("Listas");
            oculta.Cell("A1").Value = "Tipo";
            oculta.Cell("B1").Value = "Código";
            oculta.Cell("C1").Value = "Grupo";
            oculta.Cell("A2").Value = "Caja";
            oculta.Visibility = XLWorksheetVisibility.VeryHidden;
            var escondida = l.Worksheets.Add("Auxiliar");
            escondida.Cell("A1").Value = "Unidad";
            escondida.Cell("A2").Value = "kg";
            escondida.Visibility = XLWorksheetVisibility.Hidden;
            var ws = l.Worksheets.Add("Mercancías");
            ws.Cell("A1").Value = "Tipo";
            ws.Cell("B1").Value = "Medidas (m)";
            ws.Range("B1:D1").Merge();
            ws.Cell("E1").Value = "Peso";
            ws.Cell("A2").Value = "Caja";
            ws.Cell("B2").Value = 1;
            ws.Cell("D2").Value = 3;
            ws.Cell("E2").Value = 0.1 + 0.2;
        });

        var analisis = await LeerAsync(excel);

        analisis.Hojas.Select(h => (h.Nombre, h.Oculta)).Should().Equal(
            [("Listas", true), ("Auxiliar", true), ("Mercancías", false)], "las ocultas (veryHidden y hidden) se leen y vienen marcadas");
        var hoja = analisis.Hojas[2];
        hoja.Encabezados.Should().Equal("Tipo", "Medidas (m)", string.Empty, string.Empty, "Peso");
        hoja.Filas.Single().Should().Equal("Caja", "1", string.Empty, "3", "0.3");
    }

    [Fact]
    public async Task UnaCeldaLargaConEmojiSeRecortaSinPartirElEmoji()
    {
        var texto = new string('a', clsLectorGrillaExcelClosedXml.MaximoLargoCelda - 1) + "🚚 y más";
        var excel = Libro(l =>
        {
            var ws = l.Worksheets.Add("M");
            ws.Cell("A1").Value = "Tipo";
            ws.Cell("B1").Value = "Cantidad";
            ws.Cell("C1").Value = "Peso";
            ws.Cell("A2").Value = texto;
        });

        var hoja = (await LeerAsync(excel)).Hojas.Single();

        hoja.CeldasRecortadas.Should().BeTrue();
        hoja.Filas.Single()[0].Should().Be(new string('a', clsLectorGrillaExcelClosedXml.MaximoLargoCelda - 1));
    }
}
