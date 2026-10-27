using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Aplicacion.Mercancias.Interfaces;
using Cotifly.Aplicacion.Mercancias.Services;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Mercancias;

/// <summary>
/// HU-035.1 (#202, ADR-024): el análisis del Excel de mercancías valida el archivo antes de leerlo, nunca depende de Nexo y agrega el
/// código de mercancía sugerido por fila con el catálogo del cubicador, que es opcional: caído o con una excepción, el análisis sigue.
/// </summary>
public sealed class clsAnalisisExcelMercanciasServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private static readonly byte[] _excel = [0x50, 0x4B, 0x03, 0x04];

    private readonly IClsLectorGrillaExcel _lector = Substitute.For<IClsLectorGrillaExcel>();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsAnalisisExcelMercanciasServiceTests()
    {
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(
        [
            new("CAFE_EN_ESTIBAS", "Café en estibas", "GENERAL", Cubicable: true, CondicionadaCubicacion: false, null, Activo: true),
            new("BEBIDAS", "Bebidas", "GENERAL", Cubicable: true, CondicionadaCubicacion: false, null, Activo: true),
        ]));
    }

    private clsAnalisisExcelMercanciasService Servicio(bool conCubicador = true) =>
        new(_lector, NullLogger<clsAnalisisExcelMercanciasService>.Instance, conCubicador ? _cubicador : null);

    private void LectorDevuelve(params clsHojaExcelMercancias[] hojas) =>
        _lector.LeerAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsAnalisisExcelMercancias>.Exito(new clsAnalisisExcelMercancias("mercancias.xlsx", hojas, hojas.Length, false)));

    private static clsHojaExcelMercancias Hoja(string[] encabezados, params string[][] filas) =>
        new("Mercancías", 1, encabezados, [.. filas.Select(f => (IReadOnlyList<string>)f)], [.. Enumerable.Range(2, filas.Length)],
            filas.Length, false, encabezados.Length, false, false, false);

    [Theory]
    [InlineData(null, 0, clsCodigosError.ArchivoRequerido)]
    [InlineData(null, 3, clsCodigosError.FormatoNoSoportado)]
    [InlineData("mercancias.xlsx", 0, clsCodigosError.ArchivoRequerido)]
    [InlineData("mercancias.csv", 3, clsCodigosError.FormatoNoSoportado)]
    [InlineData("mercancias.xls", 3, clsCodigosError.FormatoNoSoportado)]
    [InlineData("mercancias.xlsx", clsArchivoExcel.TamanoMaximoBytes + 1, clsCodigosError.ArchivoDemasiadoGrande)]
    public async Task UnArchivoInvalidoSeRechazaSinLeerlo(string? nombre, int bytes, string codigo)
    {
        var resultado = await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido(nombre, new byte[bytes]), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        await _lector.DidNotReceiveWithAnyArgs().LeerAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task SinUsuarioEsErrorInterno() =>
        (await Servicio().AnalizarAsync(Guid.Empty, new clsArchivoSubido("m.xlsx", _excel), Ct)).Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);

    [Fact]
    public async Task ElNombreLlegaAlLectorSinLasRutasDelCliente()
    {
        LectorDevuelve(Hoja(["Peso", "Largo", "Alto"], ["1", "2", "3"]));

        await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido(@"C:\Users\ana\Desktop\mercancias.xlsm", _excel), Ct);

        await _lector.Received(1).LeerAsync("mercancias.xlsm", _excel, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnErrorDelLectorPasaTalCual()
    {
        _lector.LeerAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ExcelInvalido, "No se pudo leer.", "NO_ES_XLSX"));

        var resultado = await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct);

        (resultado.Error.Codigo, resultado.Error.Detalle).Should().Be((clsCodigosError.ExcelInvalido, "NO_ES_XLSX"));
    }

    [Fact]
    public async Task ConColumnaDeMercanciaSugiereElCodigoDelCatalogoYUsaLaColumnaDeCodigoTalCual()
    {
        LectorDevuelve(
            Hoja(["Tipo de mercancía", "Cantidad", "Peso"], ["Cafe en estiba", "10", "850"], ["", "1", "2"], ["Tornillos", "3", "4"]),
            Hoja(["Descripción", "Cód. mercancía", "Peso"], ["Gaseosas", "BEBIDAS", "10"]));

        var analisis = (await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct)).Valor;

        analisis.CatalogoMercanciasDisponible.Should().BeTrue();
        var sugeridas = analisis.Hojas[0];
        sugeridas.ColumnaMercancia.Should().Be("Tipo de mercancía");
        sugeridas.ColumnaCodigoMercancia.Should().BeNull();
        sugeridas.Mercancias!.Select(m => (m.Fila, m.CodigoMercancia, m.Sugerido)).Should().Equal((0, "CAFE_EN_ESTIBAS", true), (2, (string?)null, false));
        var deExcel = analisis.Hojas[1].Mercancias!.Single();
        (deExcel.CodigoMercancia, deExcel.NombreMercancia, deExcel.Origen).Should().Be(("BEBIDAS", "Bebidas", clsCodigoMercanciaFila.OrigenExcel));
    }

    [Fact]
    public async Task SinColumnaDeMercanciaNoConsultaElCatalogo()
    {
        LectorDevuelve(Hoja(["Largo", "Ancho", "Alto"], ["1", "2", "3"]));

        var analisis = (await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct)).Valor;

        analisis.CatalogoMercanciasDisponible.Should().BeFalse();
        analisis.Hojas[0].Mercancias.Should().BeNull();
        await _cubicador.DidNotReceiveWithAnyArgs().ListarTiposMercanciaAsync(Ct);
    }

    [Fact]
    public async Task ConElCatalogoCaidoElAnalisisSigueSinSugerencias()
    {
        LectorDevuelve(Hoja(["Mercancía", "Cantidad", "Peso"], ["Cafe en estiba", "10", "850"]));
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído"));

        var resultado = await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.CatalogoMercanciasDisponible.Should().BeFalse();
        resultado.Valor.Hojas[0].Mercancias!.Single().CodigoMercancia.Should().BeNull();
    }

    [Fact]
    public async Task UnaExcepcionDelCubicadorTampocoTumbaElAnalisis()
    {
        LectorDevuelve(Hoja(["Mercancía", "Cantidad", "Peso"], ["Cafe en estiba", "10", "850"]));
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("red"));

        var resultado = await Servicio().AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.CatalogoMercanciasDisponible.Should().BeFalse();
    }

    [Fact]
    public async Task SinCubicadorConfiguradoNoHayCatalogo()
    {
        LectorDevuelve(Hoja(["Mercancía", "Cantidad", "Peso"], ["Cafe en estiba", "10", "850"]));

        var analisis = (await Servicio(conCubicador: false).AnalizarAsync(_usuario, new clsArchivoSubido("m.xlsx", _excel), Ct)).Valor;

        analisis.CatalogoMercanciasDisponible.Should().BeFalse();
        analisis.Hojas[0].ColumnaMercancia.Should().Be("Mercancía");
    }
}
