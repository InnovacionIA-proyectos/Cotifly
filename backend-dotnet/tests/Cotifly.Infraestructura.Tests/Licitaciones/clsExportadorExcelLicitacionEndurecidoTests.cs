using System.Diagnostics;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// HU-035.2 (#213), validación: la exportación abre con ClosedXML el Excel original GUARDADO de la licitación. Los originales guardados
/// antes de los controles nunca los pasaron, así que la exportación pasa por la misma pieza (<see cref="clsLecturaExcelAcotada"/>): un
/// original malicioso es un error de negocio con su motivo (400, nunca un 500 ni un cuelgue), el cupo lleno es <c>SERVICIO_OCUPADO</c>, y
/// el cupo se libera siempre (también si ClosedXML lanza o el llamador cancela). Sin original no se toma el cupo.
/// </summary>
public sealed class clsExportadorExcelLicitacionEndurecidoTests
{
    private static readonly TimeSpan _tiempoMaximo = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsExportacionLicitacion Datos(byte[]? original) => new(
        "PCN-900555111-DG-01102026-001", "Agencia JF", "900555111", "COP", 0m, null, clsPliegoMuestra.Hoja, original,
        [new clsLineaExportacion(1, 0, "BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32000m, "Costeada", 1_176_471m)],
        [],
        new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero));

    private static (clsLecturaExcelAcotada Lectura, clsExportadorExcelLicitacionClosedXml Exportador) ConCupo(int simultaneos)
    {
        var lectura = new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance, TimeSpan.FromMilliseconds(50), simultaneos);
        return (lectura, new clsExportadorExcelLicitacionClosedXml(lectura));
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
    public async Task UnOriginalGuardadoMaliciosoEsUnErrorDeNegocioConSuMotivoYLiberaElCupo(string caso, string codigo, string motivo)
    {
        var (lectura, exportador) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        var reloj = Stopwatch.StartNew();

        var resultado = await exportador.GenerarAsync(Datos(Malicioso(caso)), Ct);

        reloj.Elapsed.Should().BeLessThan(_tiempoMaximo, caso);
        resultado.EsFallo.Should().BeTrue(caso);
        (resultado.Error.Codigo, resultado.Error.Detalle).Should().Be((codigo, motivo), caso);
        lectura.Cupo.CurrentCount.Should().Be(1, "el cupo se libera tras el rechazo ({0})", caso);
    }

    [Fact]
    public async Task UnOriginalValidoSeExportaIgualDentroDelCupo()
    {
        var (lectura, exportador) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;

        var asincrono = await exportador.GenerarAsync(Datos(clsPliegoMuestra.Excel(3)), Ct);
        var sincrono = clsExportadorExcelLicitacionClosedXml.Generar(Datos(clsPliegoMuestra.Excel(3)));

        asincrono.EsExitoso.Should().BeTrue();
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(asincrono.Valor));
        libro.Worksheet(clsPliegoMuestra.Hoja).Cell(clsPliegoMuestra.FilaExcel(0), 7).GetValue<decimal>().Should().Be(1_176_471m);
        libro.Worksheets.Select(h => h.Name).Should().Contain(clsExportadorExcelLicitacionClosedXml.HojaResumen);
        sincrono.EsExitoso.Should().BeTrue();
        lectura.Cupo.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task ConElCupoLlenoElOriginalEsServicioOcupadoYSinOriginalSeExportaIgual()
    {
        var (lectura, exportador) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        await lectura.Cupo.WaitAsync(Ct);

        var conOriginal = await exportador.GenerarAsync(Datos(clsPliegoMuestra.Excel(3)), Ct);
        var sinOriginal = await exportador.GenerarAsync(Datos(null), Ct);

        conOriginal.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        sinOriginal.EsExitoso.Should().BeTrue("sin original no se abre nada del cliente y no se toma el cupo");
        lectura.Cupo.Release();
        (await exportador.GenerarAsync(Datos(clsPliegoMuestra.Excel(3)), Ct)).EsExitoso.Should().BeTrue("al liberarse el cupo se exporta");
    }

    [Fact]
    public async Task UnaCancelacionPreviaNoOcupaElCupo()
    {
        var (lectura, exportador) = ConCupo(simultaneos: 1);
        using var cupoPropio = lectura;
        using var cancelado = new CancellationTokenSource();
        await cancelado.CancelAsync();

        var exportar = () => exportador.GenerarAsync(Datos(clsPliegoMuestra.Excel(3)), cancelado.Token);

        await exportar.Should().ThrowAsync<OperationCanceledException>();
        lectura.Cupo.CurrentCount.Should().Be(1);
    }

    [Fact]
    public async Task UnaExcepcionDentroDeLaLecturaLiberaElCupo()
    {
        using var lectura = new clsLecturaExcelAcotada(NullLogger<clsLecturaExcelAcotada>.Instance, TimeSpan.FromMilliseconds(50), 1);

        var leer = () => lectura.LeerAsync<bool>(clsPliegoMuestra.Excel(1), "prueba", _ => throw new InvalidOperationException("ClosedXML"), Ct);

        await leer.Should().ThrowAsync<InvalidOperationException>();
        lectura.Cupo.CurrentCount.Should().Be(1, "el finally devuelve el lugar aunque la lectura lance");
    }
}
