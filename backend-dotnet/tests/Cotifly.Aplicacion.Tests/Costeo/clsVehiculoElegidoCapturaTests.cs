using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-032 (#197): la validación de la captura (<see cref="clsCapturaRutasService.ValidarAsync"/>) con el vehículo elegido: obligatorio sin
/// mercancías cubicables salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>), validado contra Nexo antes de guardar solo si se va a usar, y las
/// mercancías sin medidas ni código aceptadas aunque el cubicador exija el código.
/// </summary>
public sealed class clsVehiculoElegidoCapturaTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsValidadorFilasLicitacion _filas = Substitute.For<IClsValidadorFilasLicitacion>();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();
    private IReadOnlyList<clsLineaLicitacionEntrada?> _validadas = [];

    public clsVehiculoElegidoCapturaTests()
    {
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "sin catálogo en estas pruebas"));
        _filas.ValidarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaLicitacionEntrada?>>(), Arg.Any<IReadOnlyList<clsErrorFilaLicitacion>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _validadas = ci.Arg<IReadOnlyList<clsLineaLicitacionEntrada?>>();
                return new clsFilasLicitacionValidadas([.. _validadas.Select(l => l!)], ci.Arg<IReadOnlyList<clsErrorFilaLicitacion>>(), null);
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private List<string>? _clasesSinVehiculo;

    private clsCapturaRutasService Servicio(bool aceptarVehiculo = true) => new(
        _filas, Substitute.For<IClsCostosConexosNexo>(), new clsPlanificadorCamionSinVehiculo(), NullLogger<clsCapturaRutasService>.Instance,
        _cubicador, Options.Create(new clsOpcionesCaptura { AceptarVehiculoCliente = aceptarVehiculo, ClasesSinVehiculoObligatorio = _clasesSinVehiculo }));

    private static clsLineaSolicitudCosteo Cupo(string? vehiculo, params clsPosicionCarga[] mercancias) =>
        clsSolicitudCosteoMuestra.RutaCaptura() with
        {
            ClaseViaje = "Cupo", ClaseContenedor = null, TipoVehiculo = vehiculo, Carroceria = null, PesoKg = 1_000m, Posiciones = mercancias,
        };

    private static clsPosicionCarga Cubicable() => new("Café", 1, 100m, 100m, 100m, 1_000m, CodigoMercancia: "CAFE");

    private static clsPosicionCarga SinMedidas() => new("Bultos", 1, 0m, 0m, 0m, 1_000m);

    private Task<clsFilasLicitacionValidadas> ValidarAsync(clsLineaSolicitudCosteo linea, bool aceptarVehiculo = true) =>
        Servicio(aceptarVehiculo).ValidarAsync(_usuario, [new clsLineaLicitacionEntrada(null, linea)], [], Ct);

    [Fact]
    public async Task SinMercanciasCubicablesNiVehiculoEnCupoEsVehiculoRequeridoEnLaRuta()
    {
        var validadas = await ValidarAsync(Cupo(null));

        var error = validadas.Errores.Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido).Subject;
        (error.Indice, error.Campo).Should().Be((0, "tipo_vehiculo"));
    }

    [Fact]
    public async Task UnaRutaMixtaSinVehiculoTambienLoRequiere()
    {
        (await ValidarAsync(Cupo(null, Cubicable(), SinMedidas()) with { PesoKg = 2_000m })).Errores
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido);
    }

    [Fact]
    public async Task EnContenedorSinVehiculoSeAcepta()
    {
        var contenedor = Cupo(null) with { ClaseViaje = "Contenedor", ClaseContenedor = "CNT40.HC" };

        (await ValidarAsync(contenedor)).Errores.Should().NotContain(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido);
    }

    [Fact]
    public async Task ConTodasCubicablesElVehiculoNoSeExige()
    {
        (await ValidarAsync(Cupo(null, Cubicable()))).Errores.Should().BeEmpty();
    }

    [Fact]
    public async Task ConLaBanderaApagadaNoSeExigeElVehiculo()
    {
        (await ValidarAsync(Cupo(null), aceptarVehiculo: false)).Errores.Should().BeEmpty();
    }

    [Fact]
    public async Task UnaMercanciaSinMedidasNiCodigoSeAceptaAunqueElCubicadorExijaElCodigo()
    {
        (await ValidarAsync(Cupo("3S3", SinMedidas()))).Errores.Should().BeEmpty("es una mercancía no cubicable: no es CAMPO_REQUERIDO");
    }

    [Fact]
    public async Task UnaMercanciaConMedidasYSinCodigoSigueExigiendoElCodigoConElCubicador()
    {
        (await ValidarAsync(Cupo("3S3", new clsPosicionCarga("Caja", 1, 10m, 10m, 10m, 1_000m)))).Errores
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.CampoRequerido && e.Campo == "mercancias[0].codigo_mercancia");
    }

    [Fact]
    public async Task SinPosicionesCubicablesElVehiculoSeValidaContraNexoAntesDeGuardar()
    {
        await ValidarAsync(Cupo("NO-EXISTE"));

        _validadas.Should().ContainSingle().Which!.Linea.TipoVehiculo.Should().Be("NO-EXISTE", "el validador de catálogos lo revisa (CODIGO_NO_CATALOGO)");
    }

    [Fact]
    public async Task ConPosicionesCubicablesElVehiculoNoSeValidaAntesYVuelveTalCual()
    {
        var validadas = await ValidarAsync(Cupo(" NO-EXISTE ", Cubicable()) with { Carroceria = "nave" });

        _validadas.Should().ContainSingle().Which!.Linea.Should().Match<clsLineaSolicitudCosteo>(l => l.TipoVehiculo == null && l.Carroceria == null,
            "va al cubicador: el elegido es el respaldo y se valida tarde, si se usa");
        validadas.Lineas.Should().ContainSingle().Which.Linea.Should().Match<clsLineaSolicitudCosteo>(l => l.TipoVehiculo == "NO-EXISTE" && l.Carroceria == "nave");
    }

    [Fact]
    public async Task SinCubicadorConfiguradoElVehiculoSeValidaAntesAunqueLaRutaSeaCubicable()
    {
        _cubicador.EstaConfigurado.Returns(false);

        await ValidarAsync(Cupo("3S3", Cubicable()));

        _validadas.Should().ContainSingle().Which!.Linea.TipoVehiculo.Should().Be("3S3", "sin cubicador el elegido se usa seguro");
    }

    // --- Captura:ClasesSinVehiculoObligatorio (decisión del usuario del 2026-10-21) ---

    [Fact]
    public async Task PorDefectoCargaSueltaSinVehiculoEsVehiculoRequerido()
    {
        (await ValidarAsync(Cupo(null) with { ClaseViaje = "Carga Suelta" })).Errores
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido);
    }

    [Fact]
    public async Task ConCargaSueltaConfiguradaComoExentaSeAceptaSinVehiculoYCupoLoSigueExigiendo()
    {
        _clasesSinVehiculo = ["Contenedor", "Carga Suelta"];

        (await ValidarAsync(Cupo(null) with { ClaseViaje = " Carga Suelta " })).Errores.Should().BeEmpty();
        (await ValidarAsync(Cupo(null))).Errores.Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido);
    }

    [Fact]
    public async Task UnaListaConfiguradaSinContenedorLoVuelveObligatorioEnContenedor()
    {
        _clasesSinVehiculo = ["Carga Suelta"];

        (await ValidarAsync(Cupo(null) with { ClaseViaje = "Contenedor", ClaseContenedor = "CNT40.HC" })).Errores
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido, "la lista configurada reemplaza la de por defecto");
    }

    [Fact]
    public async Task ConLaClaseDeViajeMalEscritaSoloSeReportaElErrorDeLaClaseSinVehiculoRequerido()
    {
        // B4: la exención depende de la clase de viaje; si la clase ya tiene un error (CODIGO_NO_CATALOGO del validador de catálogos), la
        // ruta no se juzga: el comercial ve un error, no dos.
        _filas.ValidarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaLicitacionEntrada?>>(), Arg.Any<IReadOnlyList<clsErrorFilaLicitacion>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new clsFilasLicitacionValidadas(
                [.. ci.Arg<IReadOnlyList<clsLineaLicitacionEntrada?>>().Select(l => l!)],
                [new clsErrorFilaLicitacion(0, null, "clase_viaje", clsCodigosLineaSolicitud.CodigoNoCatalogo, "'Contenedr' no es una clase de viaje.")],
                null));

        var validadas = await ValidarAsync(Cupo(null) with { ClaseViaje = "Contenedr" });

        validadas.Errores.Should().ContainSingle().Which.Campo.Should().Be("clase_viaje");
    }

    [Fact]
    public async Task UnErrorDeClaseDeViajeEnOtraRutaNoQuitaElVehiculoRequeridoDeEstaRuta()
    {
        _filas.ValidarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaLicitacionEntrada?>>(), Arg.Any<IReadOnlyList<clsErrorFilaLicitacion>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new clsFilasLicitacionValidadas(
                [.. ci.Arg<IReadOnlyList<clsLineaLicitacionEntrada?>>().Select(l => l!)],
                [new clsErrorFilaLicitacion(1, null, "clase_viaje", clsCodigosLineaSolicitud.CodigoNoCatalogo, "mal escrita")],
                null));

        var validadas = await Servicio().ValidarAsync(
            _usuario, [new clsLineaLicitacionEntrada(null, Cupo(null)), new clsLineaLicitacionEntrada(null, Cupo(null) with { ClaseViaje = "Cpo" })], [], Ct);

        validadas.Errores.Where(e => e.Codigo == clsCodigosLineaSolicitud.VehiculoRequerido).Select(e => e.Indice).Should().Equal(0);
    }

    [Fact]
    public void LaReglaExcluyeUnaMercanciaConMedidasYSinCodigoSoloConElCubicadorConfigurado()
    {
        var ruta = Cupo(null, new clsPosicionCarga("Caja", 1, 10m, 10m, 10m, 1_000m));

        clsCapturaRutasService.FaltaVehiculo(ruta, null, cubicadorConfigurado: true).Should().BeFalse("ya trae CAMPO_REQUERIDO en su código");
        clsCapturaRutasService.FaltaVehiculo(ruta, null, cubicadorConfigurado: false).Should().BeTrue();
    }
}
