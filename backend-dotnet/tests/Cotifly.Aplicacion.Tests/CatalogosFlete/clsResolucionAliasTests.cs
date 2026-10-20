using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.CatalogosFlete.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.CatalogosFlete;

/// <summary>
/// HU-031.1 (#194): el diccionario de alias de Nexo (NEXO #321) con la misma normalización que Nexo, la resolución catálogo → alias y
/// <c>GET /web/alias/resolver</c> (<see cref="clsResolucionAliasService"/>).
/// </summary>
public sealed class clsResolucionAliasTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static readonly clsCatalogosMaestros _catalogos = new(
        [new("2", "Camión 2 ejes"), new("2L2", "Doble troque"), new("3S3", "Tractocamión 3 ejes, semi 3")],
        [new("Contenedor", "Contenedor")],
        [new("CNT20.ST", "CNT20.ST"), new("CNT40.HC", "CNT40.HC")],
        []);

    private static readonly clsDiccionarioAliasNexo _diccionario = new(
    [
        new(TipoAliasNexo.Vehiculo, "TB", "TB", "2L2"),
        new(TipoAliasNexo.Vehiculo, "T.B", "T.B", "2L2"),
        new(TipoAliasNexo.Vehiculo, "Turbo", "TURBO", "2l2"),
        new(TipoAliasNexo.Vehiculo, "C2", "C2", "2"),
        new(TipoAliasNexo.Contenedor, "40 HC", "40 HC", "CNT40.HC"),
        new(TipoAliasNexo.Contenedor, "20 STD", "20 STD", "CNT20.ST"),
        new(TipoAliasNexo.Contenedor, "40 Flat", "40 FLAT", "CNT40.FT"),
    ]);

    private readonly IClsCatalogosMaestrosProvider _maestros = Substitute.For<IClsCatalogosMaestrosProvider>();
    private readonly IClsAliasNexoProvider _alias = Substitute.For<IClsAliasNexoProvider>();

    public clsResolucionAliasTests()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos));
        _alias.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsDiccionarioAliasNexo>.Exito(_diccionario));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("  t  .b ", "T.B")]
    [InlineData("Furgón_Refrigerado", "FURGON REFRIGERADO")]
    [InlineData("40-hc", "40 HC")]
    [InlineData("camión  2 , ejes", "CAMION 2, EJES")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizaComoNexo(string? texto, string esperado) => clsDiccionarioAliasNexo.Normalizar(texto).Should().Be(esperado);

    [Fact]
    public void ElDiccionarioSeparaLosTiposYNoResuelveUnTextoDesconocido()
    {
        _diccionario.Resolver(TipoAliasNexo.Vehiculo, "t.b").Should().Be("2L2");
        _diccionario.Resolver(TipoAliasNexo.Vehiculo, "turbo").Should().Be("2l2");
        _diccionario.Resolver(TipoAliasNexo.Contenedor, "40hc").Should().BeNull("Nexo no colapsa los espacios internos: \"40HC\" es otro alias");
        _diccionario.Resolver(TipoAliasNexo.Contenedor, "40 hc").Should().Be("CNT40.HC");
        _diccionario.Resolver(TipoAliasNexo.Contenedor, "TB").Should().BeNull("TB es un alias de vehículo");
        _diccionario.Resolver(TipoAliasNexo.Vehiculo, "   ").Should().BeNull();
        clsDiccionarioAliasNexo.Vacio.Resolver(TipoAliasNexo.Vehiculo, "TB").Should().BeNull();
    }

    [Fact]
    public void PrimeroElCatalogoDespuesElAliasYElCodigoEscritoComoEnElCatalogo()
    {
        var tipos = _catalogos.TiposVehiculo.Select(t => t.Valor);

        clsResolucionCodigosNexo.Vehiculo(tipos, _diccionario, "3s3").Should().Be(new clsCodigoResueltoNexo("3S3", FuenteCodigoNexo.Catalogo));
        clsResolucionCodigosNexo.Vehiculo(tipos, _diccionario, "Turbo").Should().Be(new clsCodigoResueltoNexo("2L2", FuenteCodigoNexo.Alias));
        clsResolucionCodigosNexo.Vehiculo(tipos, _diccionario, "C2").Should().Be(new clsCodigoResueltoNexo("2", FuenteCodigoNexo.Alias));
        clsResolucionCodigosNexo.Vehiculo(tipos, null, "C2").Should().BeNull();
        clsResolucionCodigosNexo.Vehiculo(null, _diccionario, "C2").Should().Be(new clsCodigoResueltoNexo("2", FuenteCodigoNexo.Alias),
            "sin catálogo, el código del alias (Nexo solo deja crear alias hacia códigos oficiales)");
        clsResolucionCodigosNexo.Vehiculo(tipos, _diccionario, "Tractomula").Should().BeNull();
    }

    [Fact]
    public async Task ResuelveEnLoteEnElMismoOrdenConLaFuente()
    {
        var resultado = await Servicio().ResolverAsync(_usuarioId, "Vehículo", ["TB", "3s3", "Tractomula", "TB"], Ct);

        resultado.Valor.Tipo.Should().Be("vehiculo");
        resultado.Valor.Resultados.Should().Equal(
            new clsAliasResuelto("TB", true, "2L2", "alias"),
            new clsAliasResuelto("3s3", true, "3S3", "catalogo"),
            new clsAliasResuelto("Tractomula", false, null, null),
            new clsAliasResuelto("TB", true, "2L2", "alias"));
        await _alias.Received(1).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResuelveClasesDeContenedorConElCatalogoLaListaProvisionalYElAlias()
    {
        var resultado = await Servicio().ResolverAsync(_usuarioId, "contenedor", ["CNT40.HC", "CNT20.FT", "20 std", "40 Flat", "45 XX"], Ct);

        resultado.Valor.Resultados.Select(r => (r.Codigo, r.Fuente)).Should().Equal(
            ("CNT40.HC", "catalogo"), ("CNT20.FT", "catalogo"), ("CNT20.ST", "alias"), ("CNT40.FT", "alias"), (null, null));
    }

    [Fact]
    public async Task SiTodoEsDelCatalogoNoConsultaElDiccionario()
    {
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", ["2", "3S3"], Ct)).Valor.Resultados.Should().OnlyContain(r => r.Fuente == "catalogo");

        await _alias.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    [Theory]
    [InlineData("camion")]
    [InlineData(null)]
    public async Task UnTipoInvalidoEsSolicitudInvalida(string? tipo) =>
        (await Servicio().ResolverAsync(_usuarioId, tipo, ["TB"], Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);

    [Fact]
    public async Task SinTextosConUnoVacioDemasiadosOMuyLargosEsSolicitudInvalida()
    {
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", [], Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", ["TB", " "], Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", [.. Enumerable.Repeat("TB", 201)], Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", [new string('x', 101)], Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _maestros.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    [Fact]
    public async Task SinElDiccionarioOSinElCatalogoNoHayRespuesta()
    {
        _alias.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsDiccionarioAliasNexo>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", ["TB"], Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);

        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos with { ClasesContenedor = [] }));
        (await Servicio().ResolverAsync(_usuarioId, "contenedor", ["CNT40.HC"], Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);

        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));
        (await Servicio().ResolverAsync(_usuarioId, "vehiculo", ["3S3"], Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    private clsResolucionAliasService Servicio() => new(_maestros, _alias);
}
