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
/// HU-013.10 (#106, G2, G11 y G15): solo códigos de catálogo hacia Nexo. Tipo de vehículo, clase de viaje y clase de contenedor contra el
/// catálogo maestro de Nexo (HU-023.1, #156: sin la lista fija de clases de contenedor, solo la provisional de extradimensionado);
/// carrocería y tipología en código. HU-014 (#188, M1): el vehículo y la carrocería digitados (respaldo) deben ser códigos que Nexo
/// conozca (ya no basta el catálogo del cubicador), con la equivalencia de Nexo (sin tildes ni mayúsculas, <c>_</c>/<c>-</c> = espacio).
/// </summary>
public sealed class clsValidadorCatalogosCapturaTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static readonly clsCatalogosMaestros _catalogos = new(
        [new("2S3", "Tractocamión 2 ejes, semi 3"), new("3S3", "Tractocamión 3 ejes, semi 3")],
        [new("Contenedor", "Contenedor"), new("Cupo", "Cupo"), new("Carga Suelta", "Carga Suelta"), new("Extradimensionado", "Extradimensionado")],
        [new("CNT40.HC", "CNT40.HC"), new("CNT45.XX", "CNT45.XX")],
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
    [InlineData("Tractocamión 3 ejes, semi 3")]
    [InlineData("3S3 refrigerado")]
    [InlineData("3 S 3")]
    [InlineData("2S2")]
    public async Task UnTipoDeVehiculoQueNoEsCodigoSeRechaza(string texto)
    {
        var error = await Validador().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = texto }, Ct);

        error!.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        error.Mensaje.Should().Contain("tipo_vehiculo");
    }

    [Theory]
    [InlineData("tipologia", "Mercancía general")]
    [InlineData("carroceria", "Tolva")]
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
    public async Task UnaClaseDeContenedorSeValidaContraElCatalogoMaestroDeNexoYNoContraUnaListaFija()
    {
        // HU-023.1 (#156; lo que le quedaba a HU-015.1): CNT45.XX solo existe en el catálogo de Nexo; CNT20.RE era de la lista fija.
        (await Validador().ValidarAsync(_usuarioId, Linea() with { ClaseContenedor = "CNT45.XX" }, Ct)).Should().BeNull();
        (await Validador().ValidarAsync(_usuarioId, Linea() with { ClaseContenedor = "CNT20.RE" }, Ct))!.Mensaje.Should().Contain("clase_contenedor");
    }

    [Fact]
    public async Task SinElCatalogoDeClasesDeContenedorDeNexoNoSeAceptaTextoLibre()
    {
        // clases-contenedor es un catálogo opcional de Nexo: si falló, llega vacío y ya no hay lista fija de respaldo.
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos with { ClasesContenedor = [] }));

        var una = await Validador().ValidarAsync(_usuarioId, Linea(), Ct);
        var filas = await Validador().ValidarLineasAsync(_usuarioId, [Linea(), Linea() with { ClaseContenedor = "CNT40.OP" }], Ct);

        una!.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        filas.NoDisponible!.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        filas.Errores.Should().OnlyContain(e => e.Count == 0);
    }

    [Fact]
    public async Task UnaClaseDeContenedorProvisionalNoConsultaNexo()
    {
        var linea = Linea() with { TipoVehiculo = null, ClaseViaje = null, ClaseContenedor = "CNT40.OP" };

        (await Validador().ValidarAsync(_usuarioId, linea, Ct)).Should().BeNull();
        await _maestros.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    [Theory]
    [InlineData("3s3", "FURGON")]
    [InlineData(" 3S3 ", "furgón")]
    [InlineData("2s3", "FURGON_REFRIGERADO")]
    [InlineData("2S3", "Furgon-Refrigerado")]
    [InlineData("3S3", "estibas")]
    public async Task HU014_LaMismaSiglaEscritaDeOtraFormaEsUnCodigoConocido(string tipo, string carroceria) =>
        (await Validador().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = tipo, Carroceria = carroceria }, Ct)).Should().BeNull();

    [Fact]
    public async Task HU014_M1_UnCodigoQueSoloConoceElCubicadorSeRechazaConUn400Claro()
    {
        // M1 del auditor de #188: C2 y CAMA_BAJA son del catálogo del cubicador pero Nexo no los conoce; antes viajaban y la línea nacía en
        // cuarentena. Ahora la captura los rechaza (CODIGO_NO_CATALOGO → 400) y dice qué espera.
        var vehiculo = await Validador().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = "C2" }, Ct);
        var carroceria = await Validador().ValidarAsync(_usuarioId, Linea() with { Carroceria = "CAMA_BAJA" }, Ct);

        vehiculo!.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        vehiculo.Mensaje.Should().Contain("tipo_vehiculo").And.Contain("'C2'").And.Contain("catálogo de Nexo");
        carroceria!.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        carroceria.Mensaje.Should().Contain("carroceria").And.Contain("FURGON REFRIGERADO", "las opciones son las carrocerías de Nexo");
    }

    [Fact]
    public async Task HU014_UnTipoDeVehiculoSiempreSeValidaContraNexoYSinCatalogoNoSeAcepta()
    {
        // Antes, un código del cubicador no consultaba Nexo. Ahora todo tipo_vehiculo se valida contra Nexo: sin catálogo no se acepta.
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));
        var linea = Linea() with { TipoVehiculo = "3S3", ClaseViaje = null, ClaseContenedor = null };

        (await Validador().ValidarAsync(_usuarioId, linea, Ct))!.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
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
            Linea() with { TipoVehiculo = "3S3 refrigerado", Carroceria = "Tolva" },
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
            [("tipologia", clsCodigosLineaSolicitud.CodigoNoCatalogo), ("clase_viaje", clsCodigosLineaSolicitud.CodigoNoCatalogo),
                ("clase_contenedor", clsCodigosLineaSolicitud.CodigoNoCatalogo)],
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
        resultado.Errores[1].Select(e => e.Campo).Should().Equal(["tipologia"], "la clase de contenedor también depende del catálogo de Nexo (HU-023.1)");
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

    // ------------------------------------------------------------------ HU-031.1 (#194): alias de Nexo

    private readonly IClsAliasNexoProvider _alias = Substitute.For<IClsAliasNexoProvider>();

    private void ConAlias(params clsFilaAliasNexo[] filas) =>
        _alias.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsDiccionarioAliasNexo>.Exito(new clsDiccionarioAliasNexo(filas)));

    [Theory]
    [InlineData("TB")]
    [InlineData("T.B")]
    [InlineData("t .b")]
    public async Task HU0311_UnVehiculoDigitadoQueResuelveElAliasSeAcepta(string texto)
    {
        ConAlias(new clsFilaAliasNexo(TipoAliasNexo.Vehiculo, "TB", "TB", "3S3"), new clsFilaAliasNexo(TipoAliasNexo.Vehiculo, "T.B", "T.B", "3S3"));

        (await ValidadorConAlias().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = texto }, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task HU0311_UnVehiculoQueNiElCatalogoNiElAliasConocenSigueRechazandose()
    {
        ConAlias(new clsFilaAliasNexo(TipoAliasNexo.Vehiculo, "TB", "TB", "3S3"));

        var error = await ValidadorConAlias().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = "Tractomula" }, Ct);

        error!.Mensaje.Should().Contain("tipo_vehiculo");
    }

    [Fact]
    public async Task HU0311_SinElDiccionarioSeValidaComoAntesYNoFalla()
    {
        _alias.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsDiccionarioAliasNexo>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        (await ValidadorConAlias().ValidarAsync(_usuarioId, Linea() with { TipoVehiculo = "TB" }, Ct))!.Mensaje.Should().Contain("tipo_vehiculo");
        (await ValidadorConAlias().ValidarAsync(_usuarioId, Linea(), Ct)).Should().BeNull("una línea con códigos oficiales no necesita el diccionario");
        await _alias.Received(1).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HU0311_UnaClaseDeContenedorQueResuelveElAliasSeAceptaYSeGuardaConElCodigoOficial()
    {
        ConAlias(new clsFilaAliasNexo(TipoAliasNexo.Contenedor, "40 HC", "40 HC", "cnt40.hc"), new clsFilaAliasNexo(TipoAliasNexo.Contenedor, "40 FT", "40 FT", "CNT40.FT"));
        var lineas = new[] { Linea() with { ClaseContenedor = "40 hc" }, Linea() with { ClaseContenedor = "40-FT" }, Linea() with { ClaseContenedor = "45 XX" } };

        var validacion = await ValidadorConAlias().ValidarLineasAsync(_usuarioId, lineas, Ct);

        validacion.Errores[0].Should().BeEmpty();
        validacion.Errores[1].Should().BeEmpty("el alias apunta a una clase provisional (G15)");
        validacion.Errores[2].Should().ContainSingle(e => e.Campo == "clase_contenedor");
        validacion.Canonicas!.Select(l => l.ClaseContenedor).Should().Equal("CNT40.HC", "CNT40.FT", "45 XX");
    }

    [Fact]
    public async Task HU0311_ElVehiculoDigitadoPorAliasSeGuardaConElCodigoOficial()
    {
        ConAlias(new clsFilaAliasNexo(TipoAliasNexo.Vehiculo, "Turbo", "TURBO", "2s3"));

        var validacion = await ValidadorConAlias().ValidarLineasAsync(_usuarioId, [Linea() with { TipoVehiculo = "turbo" }, Linea() with { TipoVehiculo = "3s3" }], Ct);

        validacion.Errores.Should().OnlyContain(e => e.Count == 0);
        validacion.Canonicas!.Select(l => l.TipoVehiculo).Should().Equal("2S3", "3S3");
    }

    [Fact]
    public async Task HU0311_UnAliasDeVehiculoNoSirveParaLaClaseDeContenedor()
    {
        ConAlias(new clsFilaAliasNexo(TipoAliasNexo.Vehiculo, "40HC", "40HC", "3S3"));

        (await ValidadorConAlias().ValidarAsync(_usuarioId, Linea() with { ClaseContenedor = "40HC" }, Ct))!.Mensaje.Should().Contain("clase_contenedor");
    }

    private static clsLineaSolicitudCosteo Linea() => clsSolicitudCosteoMuestra.Crear().ALinea();

    private clsValidadorCatalogosCaptura Validador() => new(_maestros, NullLogger<clsValidadorCatalogosCaptura>.Instance);

    private clsValidadorCatalogosCaptura ValidadorConAlias() => new(_maestros, NullLogger<clsValidadorCatalogosCaptura>.Instance, _alias);
}
