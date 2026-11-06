using System.IO.Compression;
using System.Text;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Mercancias.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotifly.Infraestructura.Tests.Comun;

/// <summary>
/// HU-035.3 (#277): <see cref="clsLecturaExcelAcotada.VerificarElementos"/> cuenta como XML solo las partes cuyo tipo (sin parámetros)
/// es XML (<c>+xml</c>, <c>application/xml</c>, <c>text/xml</c>) y el dibujo VML que ClosedXML también lee. Antes leía como XML toda
/// parte con «xml» en el tipo, también las binarias de <c>openxmlformats</c> como <c>printerSettings*.bin</c>, y un libro con la
/// impresión configurada era <c>LIBRO_ILEGIBLE</c>. Los topes y el rechazo del DTD no cambian: las partes XML ilegibles, con DTD o
/// demasiado densas se siguen rechazando, también si el paquete le quita el tipo XML a una parte que ClosedXML lee como XML.
/// </summary>
public sealed class clsLecturaExcelAcotadaTests : IDisposable
{
    private const string TipoImpresora = "application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings";

    private readonly clsLecturaExcelAcotada _lectura = new(NullLogger<clsLecturaExcelAcotada>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lectura.Dispose();

    [Fact]
    public void UnLibroConConfiguracionDeImpresionPasaLosControles()
    {
        var libro = clsExcelMercanciasMuestra.ConConfiguracionDeImpresion(clsPliegoMuestra.Excel(3));

        using (var zip = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        {
            zip.Entries.Should().Contain(e => e.FullName.StartsWith("xl/printerSettings/", StringComparison.Ordinal), "la muestra es como la guarda Excel");
            using var tipos = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
            tipos.ReadToEnd().Should().Contain(TipoImpresora);
            using var nombres = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
            nombres.ReadToEnd().Should().Contain("_xlnm.Print_Area").And.Contain("_xlnm.Print_Titles");
        }

        clsLecturaExcelAcotada.Verificar(libro, Ct).Should().BeNull();
    }

    [Fact]
    public async Task UnPliegoConConfiguracionDeImpresionSeAnalizaYSeLee()
    {
        var pliego = clsExcelMercanciasMuestra.ConConfiguracionDeImpresion(clsPliegoMuestra.Excel(3));
        var lector = new clsLectorExcelLicitacionClosedXml(_lectura);

        var porHoja = await lector.FilasDeDatosPorHojaAsync(pliego, Ct);
        var filas = await lector.FilasDeDatosAsync(pliego, clsPliegoMuestra.Hoja, Ct);

        porHoja.EsExitoso.Should().BeTrue(porHoja.EsFallo ? porHoja.Error.Detalle : null);
        porHoja.Valor[clsPliegoMuestra.Hoja].Should().HaveCount(3);
        filas.Valor.Should().HaveCount(3);
    }

    [Fact]
    public async Task UnExcelDeMercanciasConConfiguracionDeImpresionSeLee()
    {
        var excel = clsExcelMercanciasMuestra.ConConfiguracionDeImpresion(clsExcelMercanciasMuestra.Excel(2));
        var lector = new clsLectorGrillaExcelClosedXml(_lectura, NullLogger<clsLectorGrillaExcelClosedXml>.Instance);

        var resultado = await lector.LeerAsync("m.xlsx", excel, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Detalle : null);
        resultado.Valor.Hojas.Single().Filas.Should().HaveCount(2);
    }

    /// <summary>Partes binarias que Excel guarda en un libro válido: se saltan como hoy se saltan las imágenes o el proyecto VBA.</summary>
    public static TheoryData<string> PartesBinarias() => new()
    {
        TipoImpresora,
        "application/vnd.openxmlformats-officedocument.oleObject",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.customProperty",
        "application/vnd.ms-office.activeX",
        "application/vnd.ms-office.vbaProject",
        "image/png",
        "application/octet-stream",
    };

    [Theory]
    [MemberData(nameof(PartesBinarias))]
    public void UnaParteBinariaNoSeLeeComoXml(string tipo)
    {
        var libro = clsExcelMercanciasMuestra.ConParteExtra(clsPliegoMuestra.Excel(3), tipo, clsExcelMercanciasMuestra.DevmodeFicticio());

        clsLecturaExcelAcotada.Verificar(libro, Ct).Should().BeNull(tipo);
    }

    public static TheoryData<string, string, string, string> PartesXmlMaliciosas() => new()
    {
        { "+xml ilegible", "application/vnd.openxmlformats-officedocument.spreadsheetml.customXmlMappings+xml", "esto no es xml", clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "+XML en mayúsculas ilegible", "application/VND.CUSTOM+XML", "\u0000\u0001binario", clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "+xml con parámetros ilegible", "application/vnd.custom+xml; charset=utf-8", "<a><b></a>", clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "application/xml con DTD", "application/xml", Xxe, clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "text/xml con DTD", "text/xml", Xxe, clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "vml ilegible", "application/vnd.openxmlformats-officedocument.vmlDrawing", "<xml><v:shape>", clsLecturaExcelAcotada.MotivoLibroIlegible },
        { "+xml denso", "application/vnd.custom+xml", _denso, clsLecturaExcelAcotada.MotivoDescomprimido },
        { "vml denso", "application/vnd.openxmlformats-officedocument.vmlDrawing", _denso, clsLecturaExcelAcotada.MotivoDescomprimido },
    };

    private const string Xxe = "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><a>&e;</a>";

    private static readonly string _denso = "<a>" + string.Concat(Enumerable.Repeat("<b/>", clsLecturaExcelAcotada.MaximoElementosXml + 1)) + "</a>";

    [Theory]
    [MemberData(nameof(PartesXmlMaliciosas))]
    public void UnaParteXmlIlegibleConDtdODensaSeSigueRechazando(string caso, string tipo, string contenido, string motivo)
    {
        var libro = clsExcelMercanciasMuestra.ConParteExtra(clsPliegoMuestra.Excel(3), tipo, Encoding.UTF8.GetBytes(contenido));

        var rechazo = clsLecturaExcelAcotada.Verificar(libro, Ct);

        rechazo.Should().NotBeNull(caso);
        rechazo!.Detalle.Should().Be(motivo, caso);
    }

    [Fact]
    public void UnaHojaIlegibleSigueSiendoLibroIlegible()
    {
        var libro = clsExcelMercanciasMuestra.ConHojaXml(clsPliegoMuestra.Excel(3), "esto no es xml");

        var rechazo = clsLecturaExcelAcotada.Verificar(libro, Ct);

        (rechazo?.Codigo, rechazo?.Detalle).Should().Be((clsCodigosError.ExcelInvalido, clsLecturaExcelAcotada.MotivoLibroIlegible));
    }

    /// <summary>
    /// Un paquete que declara su hoja con un tipo no XML no se salta los controles: o el SDK no lo abre (<c>LIBRO_ILEGIBLE</c>) o la hoja
    /// se cuenta igual. Un libro denso o con DTD no llega a ClosedXML.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet")]
    [InlineData("application/octet-stream")]
    [InlineData(TipoImpresora)]
    public void UnaHojaConTipoNoXmlNoSeSaltaLosControles(string tipo)
    {
        var densa = clsExcelMercanciasMuestra.ConTipoDeHoja(clsExcelMercanciasMuestra.Densa(4_000_000), tipo);
        var conDtd = clsExcelMercanciasMuestra.ConTipoDeHoja(clsExcelMercanciasMuestra.ConEntidadExterna(), tipo);

        clsLecturaExcelAcotada.Verificar(densa, Ct).Should().NotBeNull(tipo);
        clsLecturaExcelAcotada.Verificar(conDtd, Ct).Should().NotBeNull(tipo);
    }
}
