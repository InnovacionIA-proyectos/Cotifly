using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Services;

namespace Cotifly.Aplicacion.Tests.Cubicacion;

/// <summary>
/// <see cref="clsSugeridorMercancia"/> (HU-023.2, #157; la misma lógica que usará la licitación, HU-023.5, #160): el tipo de mercancía más
/// parecido a un texto libre, sin tildes ni mayúsculas, y nada si no se parece a ninguno.
/// </summary>
public sealed class clsSugeridorMercanciaTests
{
    private static readonly IReadOnlyList<clsTipoMercanciaCubicador> _catalogo =
    [
        Tipo("CAFE_EN_ESTIBAS", "Café en estibas"),
        Tipo("BOBINAS_ACERO", "Bobinas de acero"),
        Tipo("QUIMICOS", "Tambor de químicos"),
        Tipo("BEBIDAS", "Bebidas"),
        Tipo("MAQUINARIA", "Maquinaria industrial"),
        Tipo("CARBON", "Carbón a granel", activo: false),
    ];

    private static clsTipoMercanciaCubicador Tipo(string codigo, string nombre, bool activo = true) =>
        new(codigo, nombre, "GENERAL", true, false, null, activo);

    [Theory]
    [InlineData("Café en estibas", "CAFE_EN_ESTIBAS")]
    [InlineData("CAFE EN ESTIBAS", "CAFE_EN_ESTIBAS")]
    [InlineData("  café   EN estibas ", "CAFE_EN_ESTIBAS")]
    [InlineData("cafe", "CAFE_EN_ESTIBAS")]
    [InlineData("Químicos", "QUIMICOS")]
    [InlineData("tambor de QUIMICOS", "QUIMICOS")]
    [InlineData("BOBINAS DE ACERO", "BOBINAS_ACERO")]
    [InlineData("bobina acero", "BOBINAS_ACERO")]
    [InlineData("bebida", "BEBIDAS")]
    [InlineData("maquinaria", "MAQUINARIA")]
    [InlineData("Maquinária industrial pesada", "MAQUINARIA")]
    [InlineData("bobinas_acero", "BOBINAS_ACERO")]
    public void ResuelveSinTildesNiMayusculas(string texto, string esperado)
    {
        var sugerida = clsSugeridorMercancia.Sugerir(texto, _catalogo);

        sugerida.Should().NotBeNull();
        sugerida!.Tipo.Codigo.Should().Be(esperado);
        sugerida.Parecido.Should().BeInRange(clsSugeridorMercancia.ParecidoMinimo, 1);
    }

    [Fact]
    public void ElNombreExactoDaParecidoUno()
    {
        clsSugeridorMercancia.Sugerir("Bebidas", _catalogo)!.Parecido.Should().Be(1);
        clsSugeridorMercancia.Sugerir("BEBIDAS", _catalogo)!.Parecido.Should().Be(1);
    }

    [Theory]
    [InlineData("Electrodomésticos")]
    [InlineData("Llantas")]
    [InlineData("zzz")]
    [InlineData("de la en")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SinCoincidenciaNoSugiereNada(string? texto) =>
        clsSugeridorMercancia.Sugerir(texto, _catalogo).Should().BeNull();

    [Fact]
    public void NoSugiereUnTipoInactivo() =>
        clsSugeridorMercancia.Sugerir("Carbón a granel", _catalogo).Should().BeNull("CARBON está inactivo");

    [Fact]
    public void ElEmpateSeResuelveIgualSiempre()
    {
        IReadOnlyList<clsTipoMercanciaCubicador> catalogo = [Tipo("ACERO_B", "Acero"), Tipo("ACERO_A", "Acero")];

        clsSugeridorMercancia.Sugerir("acero", catalogo)!.Tipo.Codigo.Should().Be("ACERO_A");
        clsSugeridorMercancia.Sugerir("acero", [.. catalogo.Reverse()])!.Tipo.Codigo.Should().Be("ACERO_A");
    }

    [Fact]
    public void PrefiereElMasParecido() =>
        clsSugeridorMercancia.Sugerir("Bobinas de acero galvanizado", [Tipo("ACERO", "Acero"), Tipo("BOBINAS_ACERO", "Bobinas de acero")])!
            .Tipo.Codigo.Should().Be("BOBINAS_ACERO");

    [Theory]
    [InlineData("Café en_estibas!", "CAFE EN ESTIBAS")]
    [InlineData("  ÁÉÍÓÚ ñ  ", "AEIOU N")]
    [InlineData(null, "")]
    public void NormalizaSinTildesNiSignos(string? texto, string esperado) =>
        clsSugeridorMercancia.Normalizar(texto).Should().Be(esperado);
}
