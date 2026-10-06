using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// Almacén en volumen (port de <c>AlmacenVolumen</c>): nombres <c>^[A-Za-z0-9_-]+\.pdf$</c>, sin salir de la carpeta, sin
/// reemplazar un PDF existente; y recursos embebidos del PDF (fuentes, licencia, logo, imágenes por línea).
/// </summary>
public sealed class clsAlmacenYRecursosPdfTests : IDisposable
{
    private readonly string _carpeta = clsServiciosPruebas.CarpetaPdfsTemporal();
    private readonly clsAlmacenPdfVolumen _almacen;

    public clsAlmacenYRecursosPdfTests() =>
        _almacen = new clsAlmacenPdfVolumen(Options.Create(new clsOpcionesAlmacenPdf { StorageDir = _carpeta }), new clsLoggerFalso<clsAlmacenPdfVolumen>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_carpeta))
        {
            Directory.Delete(_carpeta, recursive: true);
        }
    }

    [Fact]
    public async Task GuardaUnPdfNuevoYLoLeePorNombreYPorReferencia()
    {
        var ruta = await _almacen.GuardarNuevoAsync("PCN-19032026.pdf", [1, 2, 3], Ct);

        ruta.Should().Be(Path.Combine(Path.GetFullPath(_carpeta), "PCN-19032026.pdf"));
        (await _almacen.LeerAsync("PCN-19032026.pdf", Ct)).Should().Equal(1, 2, 3);
        (await _almacen.LeerPorReferenciaAsync(ruta, Ct)).Should().Equal(1, 2, 3);
        (await _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-19032026.pdf", Ct)).Should().Equal(1, 2, 3); // solo cuenta el nombre
        (await _almacen.LeerPorReferenciaAsync(@"D:\otra\maquina\PCN-19032026.pdf", Ct)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task NuncaReemplazaUnPdfExistente()
    {
        await _almacen.GuardarNuevoAsync("PCN-1.pdf", [1], Ct);

        var segundo = await _almacen.GuardarNuevoAsync("PCN-1.pdf", [9, 9], Ct);

        segundo.Should().BeNull();
        (await _almacen.LeerAsync("PCN-1.pdf", Ct)).Should().Equal(1);
    }

    [Theory]
    [InlineData("../PCN-1.pdf")]
    [InlineData("..\\PCN-1.pdf")]
    [InlineData("sub/PCN-1.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("PCN 1.pdf")]
    [InlineData("PCN-1.PDF")]
    [InlineData("PCN-1.pdf\n")]
    [InlineData(".pdf")]
    [InlineData("")]
    public async Task UnNombreInvalidoNoSeGuardaNiSeLee(string nombre)
    {
        var guardar = () => _almacen.GuardarNuevoAsync(nombre, [1], Ct);

        await guardar.Should().ThrowAsync<ArgumentException>();
        (await _almacen.LeerAsync(nombre, Ct)).Should().BeNull();
        clsAlmacenPdfVolumen.EsNombreValido(nombre).Should().BeFalse();
    }

    [Fact]
    public async Task UnaReferenciaConRutaManipuladaSoloLeeDentroDelAlmacen()
    {
        var fuera = Path.Combine(Path.GetTempPath(), $"fuera-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(fuera, [7], Ct);
        try
        {
            (await _almacen.LeerPorReferenciaAsync(fuera, Ct)).Should().BeNull("el archivo existe, pero fuera del almacén");
            (await _almacen.LeerPorReferenciaAsync("../../etc/passwd", Ct)).Should().BeNull();
            (await _almacen.LeerPorReferenciaAsync(null, Ct)).Should().BeNull();
        }
        finally
        {
            File.Delete(fuera);
        }
    }

    [Fact]
    public async Task EliminarBorraElPdfYNoFallaSiNoExiste()
    {
        await _almacen.GuardarNuevoAsync("PCN-2.pdf", [1], Ct);

        await _almacen.EliminarAsync("PCN-2.pdf", Ct);
        await _almacen.EliminarAsync("PCN-2.pdf", Ct);
        await _almacen.EliminarAsync("../x.pdf", Ct);

        (await _almacen.LeerAsync("PCN-2.pdf", Ct)).Should().BeNull();
    }

    [Fact]
    public void LosRecursosDelPdfVanEmbebidos()
    {
        clsRecursosPdf.Nombres().Should().BeEquivalentTo(
        [
            "Cotifly.Pdf.Fuentes.LiberationSans-Bold.ttf",
            "Cotifly.Pdf.Fuentes.LiberationSans-BoldItalic.ttf",
            "Cotifly.Pdf.Fuentes.LiberationSans-Italic.ttf",
            "Cotifly.Pdf.Fuentes.LiberationSans-Regular.ttf",
            "Cotifly.Pdf.Imagenes.almacenamiento.jpg",
            "Cotifly.Pdf.Imagenes.andino.jpg",
            "Cotifly.Pdf.Imagenes.distribucion.jpg",
            "Cotifly.Pdf.Imagenes.exportacion.jpg",
            "Cotifly.Pdf.Imagenes.importacion.jpg",
            "Cotifly.Pdf.Imagenes.itr.jpg",
            "Cotifly.Pdf.Imagenes.logo_botero_soto.png",
            "Cotifly.Pdf.Imagenes.nacional.jpg",
        ]);
        clsRecursosPdf.ImagenLogo().AsSpan(1, 3).ToArray().Should().Equal("PNG"u8.ToArray());
        clsRecursosPdf.ImagenLinea("andino").AsSpan(0, 2).ToArray().Should().Equal(0xFF, 0xD8);
    }

    [Fact]
    public void CadaImagenPorLineaTieneSuRecurso()
    {
        foreach (var clave in Aplicacion.Pdf.Services.clsTextosEstandar.ImagenPorLinea.Values.Distinct())
        {
            clsRecursosPdf.ImagenLinea(clave).Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("Liberation Sans", false, false, "LiberationSans-Regular")]
    [InlineData("Liberation Sans", true, false, "LiberationSans-Bold")]
    [InlineData("Arial", false, true, "LiberationSans-Italic")]
    [InlineData("Courier New", true, true, "LiberationSans-BoldItalic")]
    [InlineData("Symbol", false, false, "LiberationSans-Regular")]
    public void CualquierFamiliaSeResuelveALiberationSansConSuEstilo(string familia, bool negrita, bool cursiva, string cara)
    {
        var resolvedor = new clsResolvedorFuentes();

        var info = resolvedor.ResolveTypeface(familia, negrita, cursiva);

        info.FaceName.Should().Be(cara);
        resolvedor.GetFont(cara).AsSpan(0, 4).ToArray().Should().Equal(0x00, 0x01, 0x00, 0x00);
    }

    [Fact]
    public void LaLicenciaDeLaFuenteViajaConLaAplicacion()
    {
        var licencia = Path.Combine(AppContext.BaseDirectory, "licencias", "LiberationSans-OFL.txt");

        File.Exists(licencia).Should().BeTrue();
        File.ReadAllText(licencia).Should().Contain("SIL Open Font License");
    }
}
