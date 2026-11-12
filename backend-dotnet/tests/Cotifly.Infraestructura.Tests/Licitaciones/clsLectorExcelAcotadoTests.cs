using System.Diagnostics;
using ClosedXML.Excel;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Lector del Excel de licitaciones contra archivos maliciosos (ADR-017 §4) y paridad con la detección de Nexo: una celda
/// lejana o una combinada enorme terminan rápido y con memoria acotada (o con un error de negocio), nunca tumban el proceso.
/// </summary>
public sealed class clsLectorExcelAcotadoTests
{
    private const long MemoriaMaxima = 400L * 1024 * 1024;
    private static readonly TimeSpan _tiempoMaximo = TimeSpan.FromSeconds(10);

    /// <summary>Agrega a la hoja del pliego una combinada (con valor en su primera celda) o una celda lejana, editando el XML.</summary>
    private static byte[] Malicioso(string? combinada = null, string? celdaLejana = null)
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        if (combinada is not null)
        {
            libro.Worksheet(clsPliegoMuestra.Hoja).Cell(combinada.Split(':')[0]).Value = "COMBINADA";
        }

        if (celdaLejana is not null)
        {
            libro.Worksheet(clsPliegoMuestra.Hoja).Cell(celdaLejana).Value = "LEJOS";
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        if (combinada is null)
        {
            return memoria.ToArray();
        }

        using (var documento = SpreadsheetDocument.Open(memoria, true))
        {
            var parte = documento.WorkbookPart!;
            var hoja = parte.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == clsPliegoMuestra.Hoja);
            var worksheet = ((WorksheetPart)parte.GetPartById(hoja.Id!)).Worksheet;
            var combinadas = worksheet.Elements<MergeCells>().Single();
            combinadas.Append(new MergeCell { Reference = combinada });
            worksheet.Save();
        }

        return memoria.ToArray();
    }

    private (clsResultado<IReadOnlyList<int>> Resultado, TimeSpan Tiempo, long Memoria) Medir(byte[] archivo)
    {
        GC.Collect();
        var antes = GC.GetAllocatedBytesForCurrentThread();
        var reloj = Stopwatch.StartNew();
        var resultado = clsLectorExcelLicitacionClosedXml.FilasDeDatos(archivo, clsPliegoMuestra.Hoja, TestContext.Current.CancellationToken);
        reloj.Stop();
        return (resultado, reloj.Elapsed, GC.GetAllocatedBytesForCurrentThread() - antes);
    }

    [Fact]
    public void UnaCeldaLejanaNoHaceRecorrerTodaLaHoja()
    {
        var (resultado, tiempo, memoria) = Medir(Malicioso(celdaLejana: "XFD3000"));

        tiempo.Should().BeLessThan(_tiempoMaximo);
        memoria.Should().BeLessThan(MemoriaMaxima);
        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Equal(Enumerable.Range(0, 3).Select(clsPliegoMuestra.FilaExcel), "la celda lejana queda fuera de las columnas leídas");
    }

    [Theory]
    [InlineData("A2:XFD400")]
    [InlineData("A2:XFD1048576")]
    public void UnaCombinadaEnormeTerminaRapidoYConMemoriaAcotada(string rango)
    {
        var (resultado, tiempo, memoria) = Medir(Malicioso(combinada: rango));

        tiempo.Should().BeLessThan(_tiempoMaximo);
        memoria.Should().BeLessThan(MemoriaMaxima);
        if (resultado.EsFallo)
        {
            resultado.Error.Codigo.Should().BeOneOf(clsCodigosError.ExcelInvalido, clsCodigosError.ExcelSinDatos);
        }
        else
        {
            resultado.Valor.Count.Should().BeLessThanOrEqualTo(clsLectorExcelLicitacionClosedXml.MaximoFilasDatos);
        }
    }

    [Fact]
    public void LaCancelacionDelLlamadorSeRespeta()
    {
        using var cancelado = new CancellationTokenSource();
        cancelado.Cancel();

        var leer = () => clsLectorExcelLicitacionClosedXml.FilasDeDatos(clsPliegoMuestra.Excel(3), clsPliegoMuestra.Hoja, cancelado.Token);

        leer.Should().Throw<OperationCanceledException>();
    }

    public static TheoryData<string> Pliegos() => new() { "sintetico", "subencabezado", "nexo-300" };

    /// <summary>
    /// Paridad con Nexo: las filas que ubica Cotifly son exactamente las de <c>hojas[h].filas</c> del analizador de Nexo
    /// (<see cref="clsAnalizadorNexoReferencia"/>, copia del <c>ExcelSolicitudesAdapter</c> de Nexo), en el mismo orden, con el pliego
    /// de 300 líneas de Nexo (<c>tests/fixtures/licitaciones/pliego-300-lineas.xlsx</c>), uno sintético y uno con subencabezado.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pliegos))]
    public void LasFilasUbicadasSonLasMismasQueDevuelveElAnalisisDeNexo(string caso)
    {
        var (archivo, hoja) = caso switch
        {
            "sintetico" => (clsPliegoMuestra.Excel(12), clsPliegoMuestra.Hoja),
            "subencabezado" => Subencabezado(),
            _ => (File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "licitaciones", "pliego-300-lineas.xlsx")), "Pliego"),
        };

        var nexo = clsAnalizadorNexoReferencia.Analizar(archivo).Single(h => h.Nombre == hoja);
        var filas = clsLectorExcelLicitacionClosedXml.FilasDeDatos(archivo, hoja, TestContext.Current.CancellationToken);

        filas.EsExitoso.Should().BeTrue();
        filas.Valor.Should().HaveCount(nexo.Filas.Count);
        using var libro = new XLWorkbook(new MemoryStream(archivo));
        var ws = libro.Worksheet(hoja);
        for (var i = 0; i < nexo.Filas.Count; i++)
        {
            var esperada = nexo.Filas[i];
            var columnaInicial = ws.RangeUsed()!.FirstColumn().ColumnNumber();
            var leida = Enumerable.Range(0, esperada.Count).Select(c => ws.Cell(filas.Valor[i], columnaInicial + c).GetFormattedString().Trim()).ToList();
            leida.Should().Equal(esperada, $"la fila {i} del análisis de Nexo es la fila {filas.Valor[i]} del archivo");
        }
    }

    private static (byte[] Archivo, string Hoja) Subencabezado()
    {
        using var libro = new XLWorkbook();
        var ws = libro.Worksheets.Add("H");
        string[] encabezado = ["RUTA", "RUTA", "VEHÍCULO", "PESO"];
        string[] sub = ["Origen", "Destino", "Tipo", "Kg"];
        for (var c = 0; c < 4; c++)
        {
            ws.Cell(2, c + 1).Value = encabezado[c];
            ws.Cell(3, c + 1).Value = sub[c];
            ws.Cell(4, c + 1).Value = $"a{c}";
            ws.Cell(6, c + 1).Value = $"b{c}";
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return (memoria.ToArray(), "H");
    }
}
