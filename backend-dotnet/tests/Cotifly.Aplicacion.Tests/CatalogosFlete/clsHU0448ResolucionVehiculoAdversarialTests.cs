using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Services;

namespace Cotifly.Aplicacion.Tests.CatalogosFlete;

/// <summary>
/// HU-044.8 (#291) — pruebas ADVERSARIALES del validador: el objetivo de negocio es que a NEXO NUNCA le llegue «2»/«3»
/// (una solicitud con «2»/«3» nace en cuarentena en NEXO y Plaza busca mal). Aquí se tensan los caminos por los que un
/// «2»/«3» podría colarse pese a la equivalencia de <see cref="Cotifly.Dominio.Cotizaciones.clsEquivalenciaVehiculoNexo"/>.
/// </summary>
public sealed class clsHU0448ResolucionVehiculoAdversarialTests
{
    // El catálogo maestro de vehículos de NEXO trae los códigos de SAP: «2», «3» (más los que no cambian).
    private static readonly string[] _catalogoSap = ["2", "3", "2L2", "2S3", "3S3", "2L3"];

    [Theory]
    [InlineData("2", "C2")]
    [InlineData("3", "C3")]
    [InlineData("C2", "C2")]
    [InlineData("C3", "C3")]
    [InlineData("c2", "C2")]
    [InlineData(" 3 ", "C3")]
    public void UnCodigoDirectoSapONexoViajaSiempreComoCodigoNexo(string entrada, string esperado) =>
        clsResolucionCodigosNexo.Vehiculo(_catalogoSap, null, entrada)!.Codigo.Should().Be(esperado,
            "el envío a NEXO nunca debe llevar «2»/«3»");

    [Theory]
    [InlineData("2L2")]
    [InlineData("2S3")]
    [InlineData("3S3")]
    [InlineData("2L3")]
    public void LosCodigosQueNoCambianEntreSapYNexoViajanTalCualDelCatalogo(string codigo) =>
        clsResolucionCodigosNexo.Vehiculo(_catalogoSap, null, codigo).Should()
            .Be(new clsCodigoResueltoNexo(codigo, FuenteCodigoNexo.Catalogo));

    [Fact]
    public void ConElCatalogoDeNexoYaMapeadoAC2ElCodigoNexoSigueSiendoC2()
    {
        // Si por configuración el catálogo que llega ya trae C2/C3 (clsCatalogosFleteNexo los mapea), la resolución no debe
        // revertirlos a «2»/«3» ni dejarlos sin resolver.
        string[] catalogoNexo = ["C2", "C3", "2L2"];
        clsResolucionCodigosNexo.Vehiculo(catalogoNexo, null, "C2")!.Codigo.Should().Be("C2");
        clsResolucionCodigosNexo.Vehiculo(catalogoNexo, null, "2")!.Codigo.Should().Be("C2");
    }

    [Fact]
    public void ElAliasQueApuntaAlPropioC2NoRevierteAlCodigoSap()
    {
        // El alias C2 → «2» (el caso que menciona el issue): la equivalencia corre ANTES del alias, así que «C2» viaja como C2.
        var dic = new clsDiccionarioAliasNexo([new(TipoAliasNexo.Vehiculo, "C2", "C2", "2")]);
        clsResolucionCodigosNexo.Vehiculo(_catalogoSap, dic, "C2").Should()
            .Be(new clsCodigoResueltoNexo("C2", FuenteCodigoNexo.Catalogo), "el alias C2 → «2» queda sin efecto");
    }

    [Fact]
    public void UnAliasDeTextoLibreQueNexoApuntaAlCodigoSapNoDebeFiltrar2ANexo()
    {
        // ADVERSARIAL / caso del issue: la licitación trae «Sencillo». NEXO solo deja crear alias hacia códigos de SU catálogo,
        // y su catálogo (de SAP) usa «2» para el C2, así que un alias «Sencillo» de NEXO apunta a «2». El issue exige que esa
        // licitación llegue a NEXO como C2 (manual: "una licitación con «Sencillo» o «2» llega a NEXO como C2"). Si el código del
        // alias no pasa por la equivalencia, viaja «2» → cuarentena.
        var dic = new clsDiccionarioAliasNexo([new(TipoAliasNexo.Vehiculo, "Sencillo", "SENCILLO", "2")]);

        var resuelto = clsResolucionCodigosNexo.Vehiculo(_catalogoSap, dic, "Sencillo");

        resuelto!.Codigo.Should().Be("C2", "a NEXO NUNCA le puede llegar «2» (cuarentena), ni siquiera vía alias");
    }

    [Fact]
    public void UnAliasDeTextoLibreQueNexoApuntaAlCodigoSap3NoDebeFiltrar3ANexo()
    {
        var dic = new clsDiccionarioAliasNexo([new(TipoAliasNexo.Vehiculo, "Doble troque", "DOBLE TROQUE", "3")]);

        var resuelto = clsResolucionCodigosNexo.Vehiculo(_catalogoSap, dic, "Doble troque");

        resuelto!.Codigo.Should().Be("C3", "a NEXO NUNCA le puede llegar «3» (cuarentena), ni siquiera vía alias");
    }

    [Fact]
    public void UnAliasHaciaUnCodigoQueNoCambiaSigueResolviendoPorAlias()
    {
        // Control: un alias legítimo hacia un código que no difiere entre SAP y NEXO (2L2) se sigue resolviendo por alias.
        var dic = new clsDiccionarioAliasNexo([new(TipoAliasNexo.Vehiculo, "Turbo", "TURBO", "2L2")]);
        clsResolucionCodigosNexo.Vehiculo(_catalogoSap, dic, "Turbo").Should()
            .Be(new clsCodigoResueltoNexo("2L2", FuenteCodigoNexo.Alias));
    }
}
