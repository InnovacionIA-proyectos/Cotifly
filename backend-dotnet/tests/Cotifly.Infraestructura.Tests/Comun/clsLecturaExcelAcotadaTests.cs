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
    /// Un paquete que declara su hoja con un tipo no XML no se salta los controles: la hoja es una parte de tipo fijo en el SDK de Open
    /// XML, que no la abre con otro tipo (<c>LIBRO_ILEGIBLE</c>). Un libro denso o con DTD no llega a ClosedXML.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet")]
    [InlineData("application/octet-stream")]
    [InlineData(TipoImpresora)]
    public void UnaHojaConTipoNoXmlNoSeSaltaLosControles(string tipo)
    {
        var densa = clsExcelMercanciasMuestra.ConTipoDeHoja(clsExcelMercanciasMuestra.Densa(4_000_000), tipo);
        var conDtd = clsExcelMercanciasMuestra.ConTipoDeHoja(clsExcelMercanciasMuestra.ConEntidadExterna(), tipo);

        (clsLecturaExcelAcotada.Verificar(densa, Ct)?.Detalle).Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible, tipo);
        (clsLecturaExcelAcotada.Verificar(conDtd, Ct)?.Detalle).Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible, tipo);
    }

    /// <summary>
    /// Auditoría de #277 (M1): la parte principal (<c>xl/workbook.xml</c>) no tiene tipo fijo en el SDK de Open XML, que la abre con
    /// cualquier tipo, y ClosedXML la lee igual. Se cuenta siempre: un libro principal denso es <c>DESCOMPRIMIDO_EXCEDE_LIMITE</c> y con
    /// DTD es <c>LIBRO_ILEGIBLE</c>, declare el tipo que declare. Con <c>...sheet.main</c> sin <c>+xml</c> era una regresión de esta rama.
    /// </summary>
    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main")]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    public void UnLibroPrincipalConTipoNoXmlNoSeSaltaLosControles(string tipo)
    {
        var denso = clsExcelMercanciasMuestra.ConTipoDeLibro(
            clsExcelMercanciasMuestra.ConLibroDenso(clsPliegoMuestra.Excel(1), clsLecturaExcelAcotada.MaximoElementosXml + 1), tipo);
        var conDtd = clsExcelMercanciasMuestra.ConTipoDeLibro(clsExcelMercanciasMuestra.ConLibroConDtd(clsPliegoMuestra.Excel(1)), tipo);
        var valido = clsExcelMercanciasMuestra.ConTipoDeLibro(clsPliegoMuestra.Excel(1), tipo);

        clsLecturaExcelAcotada.Verificar(valido, Ct).Should().BeNull("el SDK abre el libro con ese tipo: la prueba es de una evasión real ({0})", tipo);
        (clsLecturaExcelAcotada.Verificar(denso, Ct)?.Codigo, clsLecturaExcelAcotada.Verificar(denso, Ct)?.Detalle).Should()
            .Be((clsCodigosError.ArchivoDemasiadoGrande, clsLecturaExcelAcotada.MotivoDescomprimido), tipo);
        (clsLecturaExcelAcotada.Verificar(conDtd, Ct)?.Detalle).Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible, tipo);
    }

    /// <summary>
    /// Validación de #277 (hueco anterior): las relaciones (<c>.rels</c>) y <c>[Content_Types].xml</c> no son partes y no entraban en el
    /// conteo, aunque System.IO.Packaging las lee enteras al abrir el paquete. Ahora se cuentan antes de abrirlo, en el mismo tope: una
    /// hoja de 525.000 elementos más 500.000 relaciones pasa de 1.000.000 y es <c>DESCOMPRIMIDO_EXCEDE_LIMITE</c>.
    /// </summary>
    [Fact]
    public void LasRelacionesEntranEnElConteoDeElementos()
    {
        var libro = clsExcelMercanciasMuestra.ConRelacionesDelLibro(clsExcelMercanciasMuestra.Densa(260_000), 500_000);

        clsLecturaExcelAcotada.VerificarPaquete(libro, Ct).Should().BeNull("cabe en los 64 MB: lo frena el conteo");
        var rechazo = clsLecturaExcelAcotada.Verificar(libro, Ct);

        (rechazo?.Codigo, rechazo?.Detalle).Should().Be((clsCodigosError.ArchivoDemasiadoGrande, clsLecturaExcelAcotada.MotivoDescomprimido));
        clsLecturaExcelAcotada.Verificar(clsExcelMercanciasMuestra.ConRelacionesDelLibro(clsExcelMercanciasMuestra.Excel(3), 50), Ct)
            .Should().BeNull("unas pocas relaciones más no cambian nada");
    }

    /// <summary>Las relaciones con DTD siguen siendo <c>LIBRO_ILEGIBLE</c> (ya lo eran: las rechaza también System.IO.Packaging).</summary>
    [Fact]
    public void UnasRelacionesConDtdSonLibroIlegible()
    {
        var conDtd = ReemplazarRelaciones(clsExcelMercanciasMuestra.Excel(1), xml => "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY e SYSTEM \"file:///etc/passwd\">]>"
            + xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..]);

        (clsLecturaExcelAcotada.Verificar(conDtd, Ct)?.Detalle).Should().Be(clsLecturaExcelAcotada.MotivoLibroIlegible);
    }

    private static byte[] ReemplazarRelaciones(byte[] libro, Func<string, string> cambio)
    {
        using var salida = new MemoryStream();
        using (var origen = new ZipArchive(new MemoryStream(libro), ZipArchiveMode.Read))
        using (var destino = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entrada in origen.Entries)
            {
                using var escritura = destino.CreateEntry(entrada.FullName).Open();
                using var lectura = entrada.Open();
                if (entrada.FullName == "xl/_rels/workbook.xml.rels")
                {
                    using var texto = new StreamReader(lectura);
                    escritura.Write(Encoding.UTF8.GetBytes(cambio(texto.ReadToEnd())));
                }
                else
                {
                    lectura.CopyTo(escritura);
                }
            }
        }

        return salida.ToArray();
    }
}
