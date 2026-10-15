using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-023.5 (#160): el código de mercancía de cada fila del análisis del Excel: la columna de la mercancía y la del código por su
/// encabezado (sin tildes ni mayúsculas), la sugerencia por parecido (con tildes, mayúsculas y sin coincidencia) y la columna de código
/// usada tal cual.
/// </summary>
public sealed class clsMercanciasLicitacionTests
{
    private static readonly IReadOnlyList<clsTipoMercanciaCubicador> _catalogo =
    [
        new("CAFE_EN_ESTIBAS", "Café en estibas", "GENERAL", Cubicable: true, CondicionadaCubicacion: false, null, Activo: true),
        new("BOBINAS_DE_ACERO", "Bobinas de acero", "GENERAL", Cubicable: true, CondicionadaCubicacion: false, null, Activo: true),
        new("EXPLOSIVOS", "Explosivos", "PELIGROSA", Cubicable: false, CondicionadaCubicacion: false, null, Activo: true),
        new("ARROZ", "Arroz", "GENERAL", Cubicable: true, CondicionadaCubicacion: false, null, Activo: false),
    ];

    [Theory]
    [InlineData(new[] { "ITEM", "ORIGEN", "Tipo de MERCANCÍA" }, 2)]
    [InlineData(new[] { "Descripción", "Producto" }, 1)]
    [InlineData(new[] { "TIPO DE CARGA", "Peso" }, 0)]
    [InlineData(new[] { "Código mercancía", "Descripción de la carga" }, 1)]
    [InlineData(new[] { "ORIGEN", "DESTINO", "TON" }, null)]
    public void LaColumnaDeLaMercanciaSeReconocePorSuEncabezadoSinTildesNiMayusculas(string[] encabezados, int? esperada) =>
        clsLicitacionService.ColumnaMercancia(encabezados).Should().Be(esperada);

    [Theory]
    [InlineData(new[] { "Mercancía", "Código mercancía" }, 1)]
    [InlineData(new[] { "Cód. producto", "Producto" }, 0)]
    [InlineData(new[] { "codigo_mercancia" }, 0)]
    [InlineData(new[] { "Mercancía", "Código postal" }, null)]
    public void LaColumnaDelCodigoSeReconocePorSuEncabezado(string[] encabezados, int? esperada) =>
        clsLicitacionService.ColumnaCodigoMercancia(encabezados).Should().Be(esperada);

    [Fact]
    public void CadaFilaTraeElCodigoMasParecidoMarcadoComoSugeridoOVacioSiNadaSeParece()
    {
        var hoja = Hoja(["CAFÉ EN ESTIBAS"], ["cafe en estiba"], ["Bobina de Acero"], ["Tornillos galvanizados"], [""], ["explosivos"], ["arroz"]);

        var filas = clsLicitacionService.MercanciasDeHoja(hoja, columnaTexto: 0, columnaCodigo: null, _catalogo);

        filas.Select(f => (f.Fila, f.CodigoMercancia, f.Sugerido)).Should().Equal(
            (0, "CAFE_EN_ESTIBAS", true),
            (1, "CAFE_EN_ESTIBAS", true),
            (2, "BOBINAS_DE_ACERO", true),
            (3, (string?)null, false),
            (5, (string?)null, false),
            (6, (string?)null, false));
        filas[0].Similitud.Should().Be(1m, "igual al nombre sin tildes ni mayúsculas");
        filas[0].Origen.Should().Be(clsCodigoMercanciaFila.OrigenParecido);
        filas[0].NombreMercancia.Should().Be("Café en estibas");
        filas[1].Similitud.Should().BeGreaterThanOrEqualTo(0.5m, "\"estiba\" casa con \"estibas\"");
        filas[3].Origen.Should().BeNull();
    }

    [Fact]
    public void LaColumnaDeCodigoDelExcelSeUsaTalCualAunqueHayaDescripcion()
    {
        var hoja = Hoja(["Gaseosas", "CAFE_EN_ESTIBAS"], ["Café en estibas", "OTRO_CODIGO"], ["Bobinas de acero", ""]);

        var filas = clsLicitacionService.MercanciasDeHoja(
            hoja, columnaTexto: 0, columnaCodigo: 1, _catalogo, _catalogo.ToDictionary(t => t.Codigo, StringComparer.OrdinalIgnoreCase));

        filas[0].Should().Be(new clsCodigoMercanciaFila(0, "Gaseosas", "CAFE_EN_ESTIBAS", "Café en estibas", false, clsCodigoMercanciaFila.OrigenExcel, null));
        filas[1].CodigoMercancia.Should().Be("OTRO_CODIGO", "tal cual: la validación de la licitación lo revisa contra el catálogo");
        filas[1].NombreMercancia.Should().BeNull();
        filas[2].Should().Match<clsCodigoMercanciaFila>(f => f.CodigoMercancia == "BOBINAS_DE_ACERO" && f.Sugerido,
            "sin código en la fila, se sugiere por la descripción");
    }

    [Fact]
    public void SinCatalogoNoHaySugerenciasPeroSiElCodigoDelExcel()
    {
        var hoja = Hoja(["Café en estibas", ""], ["Bebidas", "BEBIDAS"]);

        var filas = clsLicitacionService.MercanciasDeHoja(hoja, columnaTexto: 0, columnaCodigo: 1, catalogo: null);

        filas.Select(f => f.CodigoMercancia).Should().Equal(null, "BEBIDAS");
    }

    private static clsHojaExcel Hoja(params string[][] filas) =>
        new("Pliego", 1, [.. Enumerable.Range(0, filas.Max(f => f.Length)).Select(i => $"C{i}")], filas, filas.Length);
}
