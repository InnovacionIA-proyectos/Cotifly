using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-013.10 (#106, G2, G11 y G15): solo códigos de catálogo hacia Nexo. Tipo de vehículo y clase de viaje contra el catálogo maestro
/// de Nexo; carrocería, tipología y clase de contenedor en código (con la lista provisional de extradimensionado).
/// </summary>
public sealed class clsValidadorCatalogosCapturaTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static readonly clsCatalogosMaestros _catalogos = new(
        [new("2S3", "Tractocamión 2 ejes, semi 3"), new("3S3", "Tractocamión 3 ejes, semi 3")],
        [new("Contenedor", "Contenedor"), new("Cupo", "Cupo"), new("Carga Suelta", "Carga Suelta"), new("Extradimensionado", "Extradimensionado")],
        [new("CNT45.XX", "CNT45.XX")],
        [new("Oferta de Expedición Puerto a interior", "Oferta de Expedición Puerto a interior"), new("Nacional", "Nacional")]);

    private readonly IClsCatalogosMaestrosProvider _maestros = Substitute.For<IClsCatalogosMaestrosProvider>();

    public clsValidadorCatalogosCapturaTests() =>
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnaLineaConCodigosDeCatalogoEsValida() =>
        (await Validador().ValidarAsync(_usuarioId, Linea(), Ct)).Should().BeNull();

    [Theory]
    [InlineData("Tractomula")]
    [InlineData("tractocamión 3 ejes semi 3")]
    [InlineData("3s3")]
    [InlineData("3S3 refrigerado")]
    public async Task UnTipoDeVehiculoQueNoEsCodigoSeRechaza(string texto)
    {
        var error = await Validador().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = texto }, Ct);

        error!.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        error.Mensaje.Should().Contain("tipo_vehiculo");
    }

    [Theory]
    [InlineData("tipologia", "Mercancía general")]
    [InlineData("carroceria", "Furgón")]
    [InlineData("clase_viaje", "CargaSuelta")]
    [InlineData("clase_contenedor", "CNT40")]
    public async Task OtrosCatalogosFueraDeCodigoSeRechazan(string campo, string valor)
    {
        var linea = campo switch
        {
            "tipologia" => Linea() with { Tipologia = valor },
            "carroceria" => Linea() with { Carroceria = valor },
            "clase_viaje" => Linea() with { ClaseViaje = valor },
            _ => Linea() with { ClaseContenedor = valor },
        };

        (await Validador().ValidarAsync(_usuarioId, linea, Ct))!.Mensaje.Should().Contain(campo);
    }

    [Theory]
    [InlineData("CNT20.FT")]
    [InlineData("CNT20.OP")]
    [InlineData("CNT40.FT")]
    [InlineData("CNT40.OP")]
    public async Task LasClasesDeContenedorProvisionalesSeAceptan(string clase) =>
        (await Validador().ValidarAsync(_usuarioId, Linea() with { ClaseContenedor = clase }, Ct)).Should().BeNull();

    [Fact]
    public async Task UnaClaseDeContenedorDelCatalogoMaestroEnMemoriaSeAcepta()
    {
        _maestros.UltimaCopia.Returns(_catalogos);

        (await Validador().ValidarAsync(_usuarioId, Linea() with { ClaseContenedor = "CNT45.XX" }, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SinCatalogoMaestroSeRechazaConCatalogosNoDisponibles()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        (await Validador().ValidarAsync(_usuarioId, Linea(), Ct))!.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SinTipoDeVehiculoNiClaseDeViajeNoConsultaNexo()
    {
        var linea = Linea() with { TipoVehiculo = null, ClaseViaje = null, ClaseContenedor = null };

        (await Validador().ValidarAsync(_usuarioId, linea, Ct)).Should().BeNull();
        await _maestros.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    [Theory]
    [InlineData("PuertoAInterior", true)]
    [InlineData("Nacional", true)]
    [InlineData("Andino", false)]
    public void ElMapeoDeLaLineaSeRevisaContraLaCopiaDelCatalogoDeLineas(string mapeo, bool esperado)
    {
        _maestros.UltimaCopia.Returns(_catalogos);
        var logger = new clsLoggerFalso<clsValidadorCatalogosCaptura>();
        var politica = clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: mapeo).Valor;

        new clsValidadorCatalogosCaptura(_maestros, logger).RevisarMapeoLineaNexo(politica).Should().Be(esperado);
        logger.Registros.Count(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning).Should().Be(esperado ? 0 : 1);
    }

    [Fact]
    public void SinCopiaDelCatalogoElMapeoNoSeBloquea() =>
        Validador().RevisarMapeoLineaNexo(clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "Andino").Valor).Should().BeTrue();

    [Fact]
    public async Task LasFilasDeUnaLicitacionReportanTodosSusCamposFueraDeCatalogoConsultandoNexoUnaVez()
    {
        clsLineaSolicitudCosteo[] lineas =
        [
            Linea(),
            Linea() with { TipoVehiculo = "3s3", Carroceria = "Furgón" },
            Linea() with { Tipologia = "carga seca", ClaseViaje = "contenedor", ClaseContenedor = "CNT40" },
            Linea() with { ClaseContenedor = " CNT40.OP " },
            null!,
        ];

        var resultado = await Validador().ValidarLineasAsync(_usuarioId, lineas, Ct);

        resultado.NoDisponible.Should().BeNull();
        resultado.Errores.Select(l => l.Select(e => (e.Campo, e.Codigo)).ToArray()).Should().BeEquivalentTo(new (string, string)[][]
        {
            [],
            [("carroceria", clsCodigosLineaSolicitud.CodigoNoCatalogo), ("tipo_vehiculo", clsCodigosLineaSolicitud.CodigoNoCatalogo)],
            [("tipologia", clsCodigosLineaSolicitud.CodigoNoCatalogo), ("clase_contenedor", clsCodigosLineaSolicitud.CodigoNoCatalogo),
                ("clase_viaje", clsCodigosLineaSolicitud.CodigoNoCatalogo)],
            [],
            [],
        }, o => o.WithStrictOrdering());
        await _maestros.Received(1).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinCatalogoMaestroSeAvisaQueNoEstaDisponibleYSeReportaIgualLoQueSeValidaEnCodigo()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        var resultado = await Validador().ValidarLineasAsync(
            _usuarioId, [Linea() with { TipoVehiculo = "Tractomula" }, Linea() with { Tipologia = "x", ClaseContenedor = "CNT40" }], Ct);

        resultado.NoDisponible!.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        resultado.Errores[0].Should().BeEmpty("el tipo de vehículo depende del catálogo de Nexo: no se puede juzgar");
        resultado.Errores[1].Select(e => e.Campo).Should().Equal("tipologia", "clase_contenedor");
    }

    [Fact]
    public async Task FilasSinTipoDeVehiculoNiClaseDeViajeNoConsultanNexo()
    {
        var linea = Linea() with { TipoVehiculo = null, ClaseViaje = null, ClaseContenedor = null };

        var resultado = await Validador().ValidarLineasAsync(_usuarioId, [linea, linea with { Carroceria = "VOLCO" }], Ct);

        resultado.Errores.Should().OnlyContain(e => e.Count == 0);
        resultado.NoDisponible.Should().BeNull();
        await _maestros.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    private static clsLineaSolicitudCosteo Linea() => clsSolicitudCosteoMuestra.Crear().ALinea();

    private clsValidadorCatalogosCaptura Validador() => new(_maestros, NullLogger<clsValidadorCatalogosCaptura>.Instance);
}
