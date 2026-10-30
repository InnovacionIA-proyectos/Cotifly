using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.CatalogosFlete;

/// <summary>
/// HU-013.10 (G1/G2): caché de los catálogos maestros de Nexo y comportamiento sin Nexo. Vigente → no consulta; vencida y Nexo caído →
/// la copia de respaldo hasta <c>Nexo:HorasCatalogosVencidos</c>; después, o sin copia, <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.
/// </summary>
public sealed class clsCatalogosMaestrosEnCacheTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private static readonly clsCatalogosMaestros _catalogos = new([new("3S3", "Tracto")], [new("Cupo", "Cupo")], [], [new("Nacional", "Nacional")]);
    private static readonly clsErrorDominio _caido = new(clsCodigosError.CatalogosFleteNoDisponible, "caído");

    private readonly IClsCatalogosMaestrosProvider _interno = Substitute.For<IClsCatalogosMaestrosProvider>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsCacheCatalogosMaestros _cache = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MientrasLaCopiaEstaVigenteNoVuelveANexo()
    {
        Responde(_catalogos);
        var proveedor = Proveedor();

        await proveedor.ObtenerAsync(_usuarioId, Ct);
        _reloj.Avanzar(TimeSpan.FromMinutes(29));
        var segunda = await proveedor.ObtenerAsync(_usuarioId, Ct);

        segunda.Valor.Should().Be(_catalogos);
        await _interno.Received(1).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        proveedor.UltimaCopia.Should().Be(_catalogos);
    }

    [Fact]
    public async Task ConLaCopiaVencidaYNexoCaidoSirveLaCopiaDeRespaldo()
    {
        Responde(_catalogos);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_caido);
        _reloj.Avanzar(TimeSpan.FromHours(5));
        var resultado = await proveedor.ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(_catalogos);
        await _interno.Received(2).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PasadoElLimiteDeLaCopiaYConNexoCaidoNoHayCatalogos()
    {
        Responde(_catalogos);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_caido);
        _reloj.Avanzar(TimeSpan.FromHours(24));

        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SinCopiaYConNexoCaidoNoHayCatalogosYElFalloNoSeGuarda()
    {
        Responde(_caido);
        var proveedor = Proveedor();

        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        Responde(_catalogos);
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.Should().Be(_catalogos);
    }

    [Fact]
    public async Task UnaRespuestaSinTiposDeVehiculoNoSeGuardaNiSeOfrece()
    {
        Responde(new clsCatalogosMaestros([], [new("Cupo", "Cupo")], [], []));

        var proveedor = Proveedor();
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        proveedor.UltimaCopia.Should().BeNull();
    }

    // Hallazgo M de #179: un fallo pasajero de clases-contenedor no reemplaza a la copia buena con la lista vacía.
    private static readonly clsCatalogosMaestros _conContenedores = _catalogos with { ClasesContenedor = [new("CNT40.HC", "CNT40.HC")] };

    private static readonly clsCatalogosMaestros _contenedoresCaidos =
        _catalogos with { ClasesContenedor = [], LineasNegocio = [], ClasesContenedorNoDisponible = true, LineasNegocioNoDisponible = true };

    [Fact]
    public async Task UnFalloPasajeroDeClasesDeContenedorConservaLasDeLaCopiaAnterior()
    {
        Responde(_conContenedores);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_contenedoresCaidos);
        _reloj.Avanzar(TimeSpan.FromMinutes(31));
        var resultado = await proveedor.ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.ClasesContenedor.Should().Equal(_conContenedores.ClasesContenedor);
        resultado.Valor.ClasesContenedorNoDisponible.Should().BeFalse();
        resultado.Valor.LineasNegocio.Should().Equal(_catalogos.LineasNegocio, "lineas-negocio, el otro opcional, se trata igual");
        proveedor.UltimaCopia!.ClasesContenedor.Should().Equal(_conContenedores.ClasesContenedor);

        // La copia nueva queda vigente con las clases conservadas: no vuelve a Nexo.
        _reloj.Avanzar(TimeSpan.FromMinutes(29));
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().Equal(_conContenedores.ClasesContenedor);
        await _interno.Received(2).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaListaVaciaLegitimaDeNexoReemplazaALasClasesAnteriores()
    {
        Responde(_conContenedores);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_catalogos); // [] real: ClasesContenedorNoDisponible = false
        _reloj.Avanzar(TimeSpan.FromMinutes(31));

        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().BeEmpty();
    }

    [Fact]
    public async Task SinCopiaAnteriorUnFalloDeClasesDeContenedorDejaLaListaVacia()
    {
        Responde(_contenedoresCaidos);

        var resultado = await Proveedor().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClasesContenedor.Should().BeEmpty();
        resultado.Valor.ClasesContenedorNoDisponible.Should().BeTrue();
    }

    [Fact]
    public async Task UnaCopiaAnteriorVencidaNoAportaClasesDeContenedor()
    {
        Responde(_conContenedores);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_contenedoresCaidos);
        _reloj.Avanzar(TimeSpan.FromHours(24));

        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().BeEmpty();
    }

    [Fact]
    public async Task LasClasesConservadasVencenConSuFechaOriginalYNoSeArrastranIndefinidamente()
    {
        Responde(_conContenedores);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        // Fallos repetidos cada 20 h: a las 20 h se conservan; a las 40 h la copia original ya superó las 24 h.
        Responde(_contenedoresCaidos);
        _reloj.Avanzar(TimeSpan.FromHours(20));
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().NotBeEmpty();

        _reloj.Avanzar(TimeSpan.FromHours(20));
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().BeEmpty();
    }

    [Fact]
    public async Task LasClasesConservadasDejanDeServirseAlVencerAunqueLaFotoSigaVigente()
    {
        Responde(_conContenedores);
        var proveedor = Proveedor();
        await proveedor.ObtenerAsync(_usuarioId, Ct);

        Responde(_contenedoresCaidos);
        _reloj.Avanzar(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(50));
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().NotBeEmpty();

        _reloj.Avanzar(TimeSpan.FromMinutes(15)); // la foto nueva sigue vigente (15 < 30 min), las clases ya tienen más de 24 h
        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor.Should().BeEmpty();
        proveedor.UltimaCopia!.ClasesContenedor.Should().BeEmpty();
        await _interno.Received(2).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private void Responde(clsCatalogosMaestros catalogos) =>
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(catalogos));

    private void Responde(clsErrorDominio error) =>
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Fallo(error));

    private clsCatalogosMaestrosEnCache Proveedor() =>
        new(_interno, _cache, _reloj, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoFalso.UrlBase }), NullLogger<clsCatalogosMaestrosEnCache>.Instance);
}
