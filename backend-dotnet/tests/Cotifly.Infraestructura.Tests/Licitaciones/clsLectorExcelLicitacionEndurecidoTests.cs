using System.Diagnostics;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Mercancias.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// HU-035.2 (#213): el lector del pliego de licitación pasa por los mismos controles del Excel subido que el de mercancías (#202), con la
/// pieza compartida <see cref="clsLecturaExcelAcotada"/>: bomba zip, libro denso, XXE y <c>.xls</c> renombrado terminan en un error de
/// negocio con su motivo, rápido y sin cargar el libro en ClosedXML; el cupo de lecturas es uno para los dos lectores y la cancelación
/// lo libera.
/// </summary>
public sealed class clsLectorExcelLicitacionEndurecidoTests : IDisposable
{
    private static readonly TimeSpan _tiempoMaximo = TimeSpan.FromSeconds(10);
    private readonly clsLecturaExcelAcotada _lectura = new(NullLogger<clsLecturaExcelAcotada>.Instance);
    private readonly clsLectorExcelLicitacionClosedXml _lector;

    public clsLectorExcelLicitacionEndurecidoTests() => _lector = new(_lectura);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lectura.Dispose();

    /// <summary>Lectura y lectores (licitación y mercancías) con un cupo propio de <paramref name="simultaneos"/> y 50 ms de espera.</summary>
    private static (clsLecturaExcelAcotada Lectura, clsLectorExcelLicitacionClosedXml Licitacion, clsLectorGrillaExcelClosedXml Mercancias) ConCupo(int simultaneos)
    {
        var lectura = new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance, TimeSpan.FromMilliseconds(50), simultaneos);
        return (lectura, new clsLectorExcelLicitacionClosedXml(lectura),
            new clsLectorGrillaExcelClosedXml(lectura, NullLogger<clsLectorGrillaExcelClosedXml>.Instance));
    }

    public static TheoryData<string, string, string> Maliciosos() => new()
    {
        { "bomba zip", clsCodigosError.ArchivoDemasiadoGrande, clsLecturaExcelAcotada.MotivoDescomprimido },
        { "libro denso", clsCodigosError.ArchivoDemasiadoGrande, clsLecturaExcelAcotada.MotivoDescomprimido },
        { "xxe", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "xls renombrado", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoCifradoOXls },
        { "texto", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoNoEsXlsx },
        { "zip sin libro", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoSinLibro },
        { "truncado", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoZipInvalido },
        { "libro ilegible", clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoLibroIlegible },
    };

    private static byte[] Malicioso(string caso) => caso switch
    {
        "bomba zip" => clsExcelMercanciasMuestra.BombaZip(),
        "libro denso" => clsExcelMercanciasMuestra.Densa(4_000_000),
        "xxe" => clsExcelMercanciasMuestra.ConEntidadExterna(),
        "xls renombrado" => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0],
        "texto" => clsExcelMercanciasMuestra.TextoRenombrado(),
        "zip sin libro" => clsExcelMercanciasMuestra.ZipSinLibro(),
        "truncado" => clsExcelMercanciasMuestra.Truncado(),
        "libro ilegible" => clsExcelMercanciasMuestra.LibroIlegible(),
        _ => throw new ArgumentOutOfRangeException(nameof(caso), caso, "Caso desconocido."),
    };

    [Theory]
    [MemberData(nameof(Maliciosos))]
    public async Task UnPliegoMaliciosoEsUnErrorDeNegocioConSuMotivoEnElAnalisis(string caso, string codigo, string motivo)
    {
        var archivo = Malicioso(caso);
        var reloj = Stopwatch.StartNew();

        var resultado = await _lector.FilasDeDatosPorHojaAsync(archivo, Ct);

        reloj.Elapsed.Should().BeLessThan(_tiempoMaximo);
        resultado.EsFallo.Should().BeTrue(caso);
        (resultado.Error.Codigo, resultado.Error.Detalle).Should().Be((codigo, motivo), caso);
    }

    [Theory]
    [MemberData(nameof(Maliciosos))]
    public async Task UnPliegoMaliciosoEsUnErrorDeNegocioConSuMotivoAlCrearLaLicitacion(string caso, string codigo, string motivo)
    {
        var resultado = await _lector.FilasDeDatosAsync(Malicioso(caso), clsPliegoMuestra.Hoja, Ct);

        resultado.EsFallo.Should().BeTrue(caso);
        (resultado.Error.Codigo, resultado.Error.Detalle).Should().Be((codigo, motivo), caso);
    }

    [Fact]
    public async Task UnLibroDensoSeRechazaSinCargarloEnClosedXml()
    {
        var densa = clsExcelMercanciasMuestra.Densa(4_000_000);
        densa.Length.Should().BeLessThan(1024 * 1024);
        clsLecturaExcelAcotada.VerificarPaquete(densa, Ct).Should().BeNull("los bytes descomprimidos caben en los 64 MB");
        GC.Collect();
        var antes = GC.GetAllocatedBytesForCurrentThread();

        var resultado = await _lector.FilasDeDatosPorHojaAsync(densa, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ArchivoDemasiadoGrande);
        (GC.GetAllocatedBytesForCurrentThread() - antes).Should().BeLessThan(256L * 1024 * 1024, "se cuenta en flujo, sin abrir el libro en ClosedXML");
    }

    [Fact]
    public async Task UnPliegoNormalSigueUbicandoSusFilas()
    {
        var porHoja = await _lector.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct);
        var filas = await _lector.FilasDeDatosAsync(clsPliegoMuestra.Excel(3), clsPliegoMuestra.Hoja, Ct);

        porHoja.Valor[clsPliegoMuestra.Hoja].Should().HaveCount(3);
        filas.Valor.Should().Equal(porHoja.Valor[clsPliegoMuestra.Hoja]);
    }

    /// <summary>
    /// Validación de #213: un libro válido cuyo libro no está en <c>xl/workbook.xml</c> (aquí un <c>.xlsm</c> del SDK de Open XML, con el
    /// libro en <c>xl/workbook2.xml</c>) pasa los controles: el libro se busca por la relación <c>officeDocument</c>, como ClosedXML.
    /// Antes era <c>SIN_LIBRO</c> y, en la exportación, un original que siempre se había exportado dejaba de exportarse.
    /// </summary>
    [Fact]
    public async Task UnLibroValidoFueraDeXlWorkbookXmlPasaLosControlesEnLosDosLectores()
    {
        var pliego = clsExcelMercanciasMuestra.XlsmConLibroEnOtraParte(clsPliegoMuestra.Excel(3));
        var mercancias = clsExcelMercanciasMuestra.XlsmConLibroEnOtraParte(clsExcelMercanciasMuestra.Excel(2));
        var (lectura, _, lectorMercancias) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;

        clsLecturaExcelAcotada.Verificar(pliego, Ct).Should().BeNull();
        (await _lector.FilasDeDatosPorHojaAsync(pliego, Ct)).Valor[clsPliegoMuestra.Hoja].Should().HaveCount(3);
        (await _lector.FilasDeDatosAsync(pliego, clsPliegoMuestra.Hoja, Ct)).Valor.Should().HaveCount(3);
        (await lectorMercancias.LeerAsync("m.xlsm", mercancias, Ct)).Valor.Hojas.Single().Filas.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElAnalisisUbicaASoloLasPrimerasVeinteHojas()
    {
        using var libro = new ClosedXML.Excel.XLWorkbook();
        for (var h = 1; h <= clsLectorExcelLicitacionClosedXml.MaximoHojas + 2; h++)
        {
            var ws = libro.Worksheets.Add($"H{h}");
            ws.Cell("A1").Value = "Origen";
            ws.Cell("B1").Value = "Destino";
            ws.Cell("C1").Value = "Peso";
            ws.Cell("A2").Value = "Cali";
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);

        var porHoja = await _lector.FilasDeDatosPorHojaAsync(memoria.ToArray(), Ct);

        porHoja.Valor.Keys.Should().HaveCount(clsLectorExcelLicitacionClosedXml.MaximoHojas).And.NotContain("H21").And.NotContain("H22");
    }

    [Fact]
    public async Task ConElCupoLlenoRespondeServicioOcupadoSinLeer()
    {
        var (lectura, licitacion, _) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        await lectura.Cupo.WaitAsync(Ct);

        var ocupado = await licitacion.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct);

        ocupado.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        lectura.Cupo.Release();
        (await licitacion.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct)).EsExitoso.Should().BeTrue("al liberarse el cupo se lee");
    }

    [Fact]
    public async Task ElCupoEsUnoParaElPliegoYElExcelDeMercancias()
    {
        var (lectura, licitacion, mercancias) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        using var liberar = new ManualResetEventSlim();
        var dentro = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Una lectura (en otro hilo) que ocupa el único lugar del cupo hasta que la prueba la suelte.
        var ocupando = Task.Run(() => lectura.LeerAsync(clsPliegoMuestra.Excel(1), "prueba", _ =>
        {
            dentro.SetResult();
            liberar.Wait(Ct);
            return clsResultado<bool>.Exito(true);
        }, Ct), Ct);
        await dentro.Task.WaitAsync(Ct);

        (await licitacion.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct)).Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        (await mercancias.LeerAsync("m.xlsx", clsExcelMercanciasMuestra.Excel(1), Ct)).Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);

        liberar.Set();
        (await ocupando).EsExitoso.Should().BeTrue();
        (await licitacion.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task LaCancelacionDuranteLaLecturaLiberaElCupo()
    {
        var (lectura, licitacion, _) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        var excel = clsExcelMercanciasMuestra.Densa(300_000);
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelacion.CancelAfter(TimeSpan.FromMilliseconds(30));

        var leer = () => licitacion.FilasDeDatosPorHojaAsync(excel, cancelacion.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>();
        (await licitacion.FilasDeDatosPorHojaAsync(clsPliegoMuestra.Excel(3), Ct)).EsExitoso.Should().BeTrue("el cupo de 1 se liberó");
    }

    [Fact]
    public async Task UnaCancelacionPreviaNoOcupaElCupo()
    {
        var (lectura, licitacion, _) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();

        var leer = () => licitacion.FilasDeDatosAsync(clsPliegoMuestra.Excel(3), clsPliegoMuestra.Hoja, cancelado.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>();
        lectura.Cupo.CurrentCount.Should().Be(1);
    }
}
