using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-013.11 (#108): el reintento y la re-solicitud de una licitación vuelven a pasar sus filas guardadas por todas las reglas de
/// captura (forma, G3, catálogos y municipios). Lo que ya no pasa pide una carga nueva; nunca llega texto libre a Nexo.
/// </summary>
public sealed class clsValidadorFilasLicitacionTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private static readonly clsEncabezadoSolicitudCosteo _encabezado = new() { Tipo = TipoSolicitud.Licitacion };
    private readonly IClsCatalogosMaestrosProvider _maestros = Substitute.For<IClsCatalogosMaestrosProvider>();
    private readonly IClsMunicipiosProvider _municipios = Substitute.For<IClsMunicipiosProvider>();

    public clsValidadorFilasLicitacionTests()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(new(
            [new("3S3", "Tractocamión")], [new("Contenedor", "Contenedor")], [new("CNT40.HC", "CNT40.HC")], [])));
        _municipios.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(
        [
            new(76109, "76109", "Buenaventura", "Valle del Cauca", "Colombia"),
            new(11001, "11001", "Bogotá, D.C.", "Bogotá, D.C.", "Colombia"),
            new(5400, "05400", "La Unión", "Antioquia", "Colombia"),
            new(52399, "52399", "La Unión", "Nariño", "Colombia"),
        ]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsLineaSolicitudCosteo Linea() =>
        new() { Origen = "BUENAVENTURA", Destino = "BOGOTA, D.C.", TipoVehiculo = "3S3", Tipologia = "CargaSeca", PesoKg = 9000m };

    [Fact]
    public async Task LasFilasGuardadasSinIdsSeResuelvenYSeDevuelvenConEllos()
    {
        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, "L-1", _encabezado, [Linea(), Linea() with { OrigenMunicipioId = 5400 }], [0, 1], Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Select(l => (l.OrigenMunicipioId, l.DestinoMunicipioId)).Should().Equal(((int?)76109, (int?)11001), ((int?)5400, (int?)11001));
    }

    [Fact]
    public async Task UnaFilaGuardadaConTextoLibreG3RotoOMunicipioAmbiguoPideUnaCargaNuevaConTodasLasFilas()
    {
        clsLineaSolicitudCosteo[] guardadas =
        [
            Linea() with { TipoVehiculo = "Tractomula" },
            Linea() with { ClaseContenedor = "CNT40.HC" },
            Linea() with { Origen = "La Unión" },
        ];

        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, "L-1", _encabezado, guardadas, [3, 4, 5], Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.LicitacionInvalida);
        resultado.Error.Detalle.Should().Be(clsValidadorFilasLicitacion.MotivoRequiereNuevaCarga);
        resultado.Error.Mensaje.Should().Contain("POST /web/licitaciones");
        var errores = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)resultado.Error.Datos!["errores"]!;
        errores.Select(e => ((int?)e["indice"], (int?)e["fila"], (string?)e["campo"], (string?)e["codigo"])).Should().Equal(
            ((int?)0, (int?)3, "tipo_vehiculo", clsCodigosLineaSolicitud.CodigoNoCatalogo),
            ((int?)1, (int?)4, "clase_contenedor", clsCodigosLineaSolicitud.CampoNoAplica),
            ((int?)2, (int?)5, "origen", clsCodigosLineaSolicitud.MunicipioAmbiguo));
    }

    [Fact]
    public async Task ConElCatalogoMaestroCaidoYFilasValidasEsCatalogosNoDisponibles()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, "L-1", _encabezado, [Linea()], [0], Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task ConElCatalogoMaestroCaidoUnErrorQueNoDependeDeNexoIgualPideUnaCargaNueva()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, "L-1", _encabezado, [Linea() with { Tipologia = "Granel" }], [0], Ct);

        resultado.Error.Detalle.Should().Be(clsValidadorFilasLicitacion.MotivoRequiereNuevaCarga);
    }

    [Fact]
    public async Task SinCatalogoDeMunicipiosYFilasSinIdEsMunicipiosNoDisponibles()
    {
        _municipios.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Fallo(new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, "caído")));

        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, "L-1", _encabezado, [Linea()], [0], Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
    }

    /// <summary>
    /// HU-014 (#188, revisión de M1): el reintento y la re-solicitud de una licitación cubicada revalidan lo guardado. El camión del
    /// cubicador trae un código que Nexo no conoce (<c>2S2</c>, <c>CAMA_BAJA</c>): no es dato digitado y lo que viaja lo decide
    /// <c>Nexo:EnviarCodigosCubicador</c>, así que no debe pedir una carga nueva.
    /// </summary>
    [Fact]
    public async Task HU014ElVehiculoDelCamionDelCubicadorNoBloqueaElReintentoDeLaLicitacion()
    {
        var cotizacion = LicitacionCon(new clsCamionPlaneado("2S2", "CAMA_BAJA", 9000m, Guid.NewGuid()));

        var lineas = clsEnvioCosteoService.LineasGuardadas(cotizacion);
        var resultado = await Validador().RevalidarGuardadasAsync(_usuarioId, cotizacion.Folio, _encabezado, lineas, [0], Ct);

        lineas[0].TipoVehiculo.Should().BeNull("el código del cubicador no se revalida contra el catálogo de Nexo");
        lineas[0].Carroceria.Should().BeNull();
        resultado.EsExitoso.Should().BeTrue();
        cotizacion.PrimeraRuta!.Vehiculos[0].TipoVehiculo.Should().Be("2S2", "el camión guardado no cambia");
    }

    /// <summary>HU-014 (#188, M1): el vehículo digitado del respaldo (sin recomendación) sí se revalida contra Nexo.</summary>
    [Fact]
    public async Task HU014ElVehiculoDigitadoDelRespaldoSiSeRevalidaContraNexo()
    {
        var cotizacion = LicitacionCon(new clsCamionPlaneado("2S2", null, 9000m));

        var resultado = await Validador().RevalidarGuardadasAsync(
            _usuarioId, cotizacion.Folio, _encabezado, clsEnvioCosteoService.LineasGuardadas(cotizacion), [0], Ct);

        resultado.Error.Detalle.Should().Be(clsValidadorFilasLicitacion.MotivoRequiereNuevaCarga);
    }

    private static clsCotizacionSolicitud LicitacionCon(clsCamionPlaneado camion)
    {
        var cotizacion = Pruebas.Comun.clsCotizacionMuestra.Cabecera();
        cotizacion.RegistrarCaptura(_encabezado,
            [new clsRutaCapturada(Linea() with { OrigenMunicipioId = 76109, DestinoMunicipioId = 11001, TipoVehiculo = null }, 3, 0, [camion], [])]);
        return cotizacion;
    }

    private clsValidadorFilasLicitacion Validador() =>
        new(new clsValidadorCatalogosCaptura(_maestros, NullLogger<clsValidadorCatalogosCaptura>.Instance), _municipios);
}
