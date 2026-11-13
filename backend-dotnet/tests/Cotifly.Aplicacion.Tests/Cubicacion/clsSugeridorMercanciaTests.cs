using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Services;

namespace Cotifly.Aplicacion.Tests.Cubicacion;

/// <summary>
/// <see cref="clsSugeridorMercancia"/> (la misma lógica que usa la licitación): el tipo de mercancía más
/// parecido a un texto libre, sin tildes ni mayúsculas; nada si no se parece lo suficiente, si los números no coinciden o si la mejor
/// coincidencia es ambigua.
/// </summary>
public sealed class clsSugeridorMercanciaTests
{
    /// <summary>Catálogo con la forma del cubicador (nombres y códigos verosímiles; datos ficticios).</summary>
    private static readonly IReadOnlyList<clsTipoMercanciaCubicador> _catalogo =
    [
        Tipo("CAFE_EN_ESTIBAS", "Café en estibas"),
        Tipo("BOBINAS_ACERO", "Bobinas de acero"),
        Tipo("QUIMICOS", "Tambor de químicos"),
        Tipo("BEBIDAS", "Bebidas"),
        Tipo("MAQUINARIA", "Maquinaria industrial"),
        Tipo("CONTENEDOR_20", "Contenedor 20 pies"),
        Tipo("CONTENEDOR_40", "Contenedor 40 pies"),
        Tipo("CARGA_GENERAL", "Carga general"),
        Tipo("CAJAS", "Cajas"),
        Tipo("ALIMENTOS_REFRIGERADOS", "Alimentos refrigerados"),
        Tipo("LLANTAS", "Llantas"),
        Tipo("PAPEL", "Rollos de papel"),
        Tipo("CEMENTO", "Cemento en sacos"),
        Tipo("RESINAS", "Resinas plásticas"),
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
    [InlineData("bobinas_acero", "BOBINAS_ACERO")]
    [InlineData("Contenedor 40 pies", "CONTENEDOR_40")]
    [InlineData("contenedor de 20", "CONTENEDOR_20")]
    public void ResuelveSinTildesNiMayusculas(string texto, string esperado) => Esperar(texto, esperado);

    /// <summary>Descripciones como las escribe un comercial (ficticias), con palabras de más, plurales y pesos.</summary>
    [Theory]
    [InlineData("Café verde en estibas", "CAFE_EN_ESTIBAS")]
    [InlineData("Bobinas de acero laminado en frío", "BOBINAS_ACERO")]
    [InlineData("Tambores de químicos industriales", "QUIMICOS")]
    [InlineData("Bebidas gaseosas en canastas", "BEBIDAS")]
    [InlineData("Maquinaria industrial pesada", "MAQUINARIA")]
    [InlineData("Rollos de papel kraft", "PAPEL")]
    [InlineData("Cemento gris en sacos de 50 kg", "CEMENTO")]
    [InlineData("Alimentos refrigerados (cárnicos)", "ALIMENTOS_REFRIGERADOS")]
    [InlineData("Resina plástica en big bags", "RESINAS")]
    [InlineData("Llantas para camión", "LLANTAS")]
    [InlineData("Cajas de cartón vacías", "CAJAS")]
    public void ResuelveDescripcionesReales(string texto, string esperado) => Esperar(texto, esperado);

    /// <summary>Estas tres coincidencias equivocadas no se sugieren.</summary>
    [Theory]
    [InlineData("Contenedor 40 pies", "CONTENEDOR_20")]
    [InlineData("Carga seca", "CARGA_GENERAL")]
    [InlineData("Electrodomésticos en cajas", "CAJAS")]
    public void NoSugiereLasCoincidenciasEquivocadasDeLaValidacion(string texto, string equivocado)
    {
        var sugerida = clsSugeridorMercancia.Sugerir(texto, _catalogo);

        (sugerida?.Tipo.Codigo).Should().NotBe(equivocado);
        // Sin el tipo correcto en el catálogo, tampoco sugiere el equivocado.
        clsSugeridorMercancia.Sugerir(texto, [.. _catalogo.Where(t => t.Codigo != "CONTENEDOR_40")])
            .Should().Match<clsMercanciaSugerida?>(s => s == null || s.Tipo.Codigo != equivocado);
    }

    [Theory]
    [InlineData("Carga seca")]
    [InlineData("Electrodomésticos en cajas")]
    [InlineData("Muebles de oficina")]
    [InlineData("Contenedor 45 pies")]
    [InlineData("Electrodomésticos")]
    [InlineData("zzz")]
    [InlineData("de la en")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SinCoincidenciaSuficienteNoSugiereNada(string? texto) =>
        clsSugeridorMercancia.Sugerir(texto, _catalogo).Should().BeNull();

    [Fact]
    public void LosNumerosDelTipoTienenQueCoincidirConLosDelTexto()
    {
        clsSugeridorMercancia.Sugerir("Contenedor 40 pies", [Tipo("CONTENEDOR_20", "Contenedor 20 pies")]).Should().BeNull();
        clsSugeridorMercancia.Sugerir("Contenedor 20 pies", [Tipo("CONTENEDOR_20", "Contenedor 20 pies")])!.Tipo.Codigo.Should().Be("CONTENEDOR_20");
        clsSugeridorMercancia.Sugerir("Contenedor 20 y 40", [Tipo("CONTENEDOR_20", "Contenedor 20 pies")])
            .Should().BeNull("el texto trae números que el tipo no tiene");
    }

    [Fact]
    public void UnTipoSinNumerosNoSeDescartaPorLosPesosDelTexto() =>
        clsSugeridorMercancia.Sugerir("Cemento en sacos de 50 kg", _catalogo)!.Tipo.Codigo.Should().Be("CEMENTO");

    [Fact]
    public void UnaCoincidenciaAmbiguaNoSugiereNada()
    {
        clsSugeridorMercancia.Sugerir("Contenedor", [Tipo("CONT_SECO", "Contenedor seco"), Tipo("CONT_REEFER", "Contenedor refrigerado")])
            .Should().BeNull("los dos contenedores empatan");
        clsSugeridorMercancia.Sugerir("acero", [Tipo("ACERO_B", "Acero"), Tipo("ACERO_A", "Acero")]).Should().BeNull();
    }

    [Fact]
    public void ElNombreExactoDaParecidoUno()
    {
        clsSugeridorMercancia.Sugerir("Bebidas", _catalogo)!.Parecido.Should().Be(1);
        clsSugeridorMercancia.Sugerir("BEBIDAS", _catalogo)!.Parecido.Should().Be(1);
    }

    [Fact]
    public void NoSugiereUnTipoInactivo() =>
        clsSugeridorMercancia.Sugerir("Carbón a granel", _catalogo).Should().BeNull("CARBON está inactivo");

    [Fact]
    public void PrefiereElMasParecido() =>
        clsSugeridorMercancia.Sugerir("Bobinas de acero galvanizado", [Tipo("ACERO", "Acero"), Tipo("BOBINAS_ACERO", "Bobinas de acero")])!
            .Tipo.Codigo.Should().Be("BOBINAS_ACERO");

    [Fact]
    public void LosCriteriosSonConfigurables()
    {
        // Con un umbral más alto, una coincidencia parcial deja de sugerirse; con margen 0 nada es ambiguo salvo el empate exacto.
        clsSugeridorMercancia.Sugerir("Café verde en estibas", _catalogo, 0.99, 0.05).Should().BeNull();
        clsSugeridorMercancia.Sugerir("Café verde en estibas", _catalogo, 0.6, 0.05)!.Tipo.Codigo.Should().Be("CAFE_EN_ESTIBAS");
        clsSugeridorMercancia.Sugerir("acero", [Tipo("ACERO_B", "Acero"), Tipo("ACERO_A", "Acero")], 0.7, 0)
            .Should().BeNull("el empate exacto sigue siendo ambiguo");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.3)]
    [InlineData(1.1)]
    public void ElParecidoMinimoDebeSerMayorQueUnMedio(double minimo)
    {
        var accion = () => clsSugeridorMercancia.Sugerir("café", _catalogo, minimo, 0.05);

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Café en_estibas!", "CAFE EN ESTIBAS")]
    [InlineData("  ÁÉÍÓÚ ñ  ", "AEIOU N")]
    [InlineData(null, "")]
    public void NormalizaSinTildesNiSignos(string? texto, string esperado) =>
        clsSugeridorMercancia.Normalizar(texto).Should().Be(esperado);

    private static void Esperar(string texto, string esperado)
    {
        var sugerida = clsSugeridorMercancia.Sugerir(texto, _catalogo);

        sugerida.Should().NotBeNull(texto);
        sugerida!.Tipo.Codigo.Should().Be(esperado, texto);
        sugerida.Parecido.Should().BeGreaterThan(0.5).And.BeLessThanOrEqualTo(1);
    }
}
