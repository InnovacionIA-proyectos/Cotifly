using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Comun;

/// <summary>
/// Las reglas genéricas del Excel subido (presente, tamaño y extensión), en un lugar neutro y compartidas por el pliego
/// de licitaciones y el Excel de mercancías: el pliego responde exactamente lo mismo que con sus reglas propias.
/// </summary>
public sealed class clsArchivoExcelTests
{
    [Theory]
    [InlineData("mercancias.xlsx", 10, null)]
    [InlineData("MERCANCIAS.XLSM", 10, null)]
    [InlineData(@"C:\Users\ana\mercancias.xlsx", 10, null)]
    [InlineData("mercancias.xlsx", 0, clsCodigosError.ArchivoRequerido)]
    [InlineData("mercancias.xls", 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData("mercancias.xlsx.exe", 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData(null, 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData("mercancias.xlsx", clsArchivoExcel.TamanoMaximoBytes + 1, clsCodigosError.ArchivoDemasiadoGrande)]
    public void ValidaPresenciaTamanoYExtensionIgualQueElPliego(string? nombre, int bytes, string? codigo)
    {
        var contenido = new byte[bytes];

        clsArchivoExcel.ValidarArchivo(nombre, contenido)?.Codigo.Should().Be(codigo);
        (clsArchivoExcel.ValidarArchivo(nombre, contenido) is null).Should().Be(codigo is null);
        clsArchivoLicitacion.ValidarArchivo(nombre, contenido)?.Codigo.Should().Be(codigo, "el pliego delega en la regla genérica");
    }

    [Fact]
    public void ElTopeEsElMismoDelPliegoYElNombreSeLimpiaDeRutas()
    {
        clsArchivoLicitacion.TamanoMaximoBytes.Should().Be(clsArchivoExcel.TamanoMaximoBytes).And.Be(10_420_224);
        clsArchivoExcel.NombreLimpio(@" C:\Users\ana/Escritorio\mercancias.xlsx ").Should().Be("mercancias.xlsx");
        clsArchivoExcel.ValidarArchivo("m.xlsx", null)!.Codigo.Should().Be(clsCodigosError.ArchivoRequerido);
    }
}
