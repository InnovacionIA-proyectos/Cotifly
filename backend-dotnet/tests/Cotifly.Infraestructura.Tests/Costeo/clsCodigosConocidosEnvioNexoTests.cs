using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// HU-014 (#188, decisión del usuario del 2026-10-19: "enviar solo códigos conocidos"). <c>Nexo:EnviarCodigosCubicador</c> con tres modos
/// (<c>solo_conocidos</c> por defecto, <c>siempre</c>, <c>nunca</c>): el vehículo de un camión del cubicador viaja solo si su código está
/// en <c>GET /api/catalogos/tipos-vehiculo</c> de Nexo (con la equivalencia de <c>MismoCodigo</c>), escrito como en Nexo; la carrocería,
/// solo con el vehículo y si está en la lista de carrocerías de Nexo. Sin catálogo, ningún código es conocido.
/// </summary>
public sealed class clsCodigosConocidosEnvioNexoTests : IDisposable
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private const string Creada =
        """{"id":"3f2b8c1e-0000-4000-8000-000000000001","lineas":5,"rutasCreadas":0,"vehiculosCreados":0,"scorePrioridad":0.5,"fletesCalculados":0,"lineasPorCostear":5,"costeoEnProceso":false}""";

    /// <summary>Catálogo de tipos de vehículo de Nexo de estas pruebas (con una escritura con espacio y otra con tilde).</summary>
    private static readonly clsCatalogosMaestros _catalogos = new(
        [new("2S3", "Tractocamión 2 ejes, semi 3"), new("3S3", "Tractocamión 3 ejes, semi 3"), new("CAMA BAJA", "Cama baja"), new("Tractomula", "Tractomula")],
        [new("Cupo", "Cupo")], [], []);

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsCatalogosMaestrosProvider _maestros = Substitute.For<IClsCatalogosMaestrosProvider>();

    public clsCodigosConocidosEnvioNexoTests()
    {
        _http = new HttpClient(_nexo);
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos));
        _nexo.Responder(HttpStatusCode.Created, Creada);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConSoloConocidosViajanLosCodigosQueNexoConoceEscritosComoEnNexoYLosDemasNo()
    {
        var creada = await Adaptador(null).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        Vehiculos().Should().Equal(
            ("3S3", "FURGON"),
            (null, null),                                  // 2S2: Nexo no lo conoce → sin vehículo ni carrocería (sin cuarentena)
            ("CAMA BAJA", "FURGON REFRIGERADO"),           // cama_baja / FURGON_REFRIGERADO: equivalentes, en la forma de Nexo
            ("Tractomula", null),                          // TRACTOMÚLA: conocido; TOLVA no está en la lista de carrocerías de Nexo
            ("3S3", "FURGON"));                            // respaldo (sin recomendación, 3s3 / Furgón): viaja en la forma de Nexo
        LineasJson().Take(4).Select(l => l.TryGetProperty("idRecomendacionVehiculo", out _)).Should().OnlyContain(tiene => tiene,
            "la línea sin vehículo sigue llevando su camión del cubicador para emparejarla");
        creada.Valor.CodigosCubicadorEnviados.Should().BeFalse("no viajaron todos: el emparejador no compara el vehículo de esos camiones");
    }

    [Fact]
    public async Task UnCodigoAmbiguoEnElCatalogoDeNexoNoEsConocidoSalvoQueCoincidaExacto()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Exito(_catalogos with { TiposVehiculo = [new("C-2", "C dos"), new("C 2", "C dos bis")] }));

        await Adaptador("solo_conocidos").EnviarSolicitudAsync(_usuarioId, Solicitud([("C_2", "FURGON"), ("C 2", "FURGON")]), Ct);

        Vehiculos().Take(2).Should().Equal((null, null), ("C 2", "FURGON"));
    }

    [Fact]
    public async Task SinElCatalogoDeNexoNingunCodigoDelCubicadorEsConocidoYElRespaldoViajaComoSeCapturo()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        var creada = await Adaptador("solo_conocidos").EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        creada.EsExitoso.Should().BeTrue("el catálogo caído nunca impide el envío");
        Vehiculos().Should().Equal((null, null), (null, null), (null, null), (null, null), ("3s3", "FURGON"));
    }

    [Fact]
    public async Task SiLeerElCatalogoLanzaElEnvioSigueSinVehiculos()
    {
        _maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns<Task<clsResultado<clsCatalogosMaestros>>>(_ => throw new HttpRequestException("red"));

        (await Adaptador(null).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).EsExitoso.Should().BeTrue();

        Vehiculos().Take(4).Should().OnlyContain(v => v.Item1 == null && v.Item2 == null);
    }

    [Fact]
    public async Task ConSiempreViajanTodosLosConocidosEnLaFormaDeNexoYLosDesconocidosTalCual()
    {
        var creada = await Adaptador("siempre").EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        Vehiculos().Should().Equal(("3S3", "FURGON"), ("2S2", "ESTACAS"), ("CAMA BAJA", "FURGON REFRIGERADO"), ("Tractomula", "TOLVA"), ("3S3", "FURGON"));
        creada.Valor.CodigosCubicadorEnviados.Should().BeTrue();
    }

    [Fact]
    public async Task ConNuncaNingunCamionDelCubicadorLlevaVehiculoPeroElRespaldoSi()
    {
        var creada = await Adaptador("nunca").EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        Vehiculos().Should().Equal((null, null), (null, null), (null, null), (null, null), ("3S3", "FURGON"));
        creada.Valor.CodigosCubicadorEnviados.Should().BeFalse();
    }

    [Fact]
    public async Task ConNuncaYSinRespaldoConVehiculoNoSeConsultaElCatalogo()
    {
        await Adaptador("nunca").EnviarSolicitudAsync(_usuarioId, Solicitud([("3S3", "FURGON")], respaldo: false), Ct);

        await _maestros.DidNotReceiveWithAnyArgs().ObtenerAsync(default, Ct);
    }

    [Theory]
    [InlineData(null, ModoCodigosCubicador.SoloConocidos)]
    [InlineData("", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("solo_conocidos", ModoCodigosCubicador.SoloConocidos)]
    [InlineData(" Solo-Conocidos ", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("SOLO CONOCIDOS", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("false", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("False", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("true", ModoCodigosCubicador.Siempre)]
    [InlineData("TRUE", ModoCodigosCubicador.Siempre)]
    [InlineData("siempre", ModoCodigosCubicador.Siempre)]
    [InlineData("Nunca", ModoCodigosCubicador.Nunca)]
    public void LaBanderaAceptaLosTresModosYElBooleanoAnterior(string? valor, ModoCodigosCubicador esperado)
    {
        var opciones = new clsOpcionesNexo { EnviarCodigosCubicador = valor };

        opciones.ModoCodigosCubicador.Should().Be(esperado);
        Errores(opciones).Should().BeEmpty();
    }

    [Theory]
    [InlineData("quizas")]
    [InlineData("1")]
    [InlineData("solo")]
    public void UnModoDesconocidoNoDejaArrancar(string valor)
    {
        Errores(new clsOpcionesNexo { EnviarCodigosCubicador = valor }).Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("Nexo:EnviarCodigosCubicador").And.Contain("solo_conocidos");

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nexo:EnviarCodigosCubicador"] = valor }).Build();
        using var proveedor = new ServiceCollection().AddOptions<clsOpcionesNexo>()
            .Bind(configuracion.GetSection(clsOpcionesNexo.Seccion)).ValidateDataAnnotations().Services.BuildServiceProvider();
        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesNexo>>().Value;
        leer.Should().Throw<OptionsValidationException>();
    }

    [Theory]
    [InlineData("true", ModoCodigosCubicador.Siempre)]
    [InlineData("false", ModoCodigosCubicador.SoloConocidos)]
    [InlineData("nunca", ModoCodigosCubicador.Nunca)]
    public void ElValorDeLaVariableDeEntornoSeEnlaza(string valor, ModoCodigosCubicador esperado)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nexo:EnviarCodigosCubicador"] = valor }).Build();

        configuracion.GetSection(clsOpcionesNexo.Seccion).Get<clsOpcionesNexo>()!.ModoCodigosCubicador.Should().Be(esperado);
    }

    private static List<ValidationResult> Errores(clsOpcionesNexo opciones)
    {
        var errores = new List<ValidationResult>();
        Validator.TryValidateObject(opciones, new ValidationContext(opciones), errores, validateAllProperties: true);
        return errores;
    }

    private clsCosteoNexo Adaptador(string? modo) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase, EnviarCodigosCubicador = modo }), _usuarios,
            NullLogger<clsCosteoNexo>.Instance, catalogos: _maestros);

    private List<JsonElement> LineasJson()
    {
        using var json = JsonDocument.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Post).Cuerpo!);
        return [.. json.RootElement.GetProperty("lineas").EnumerateArray().Select(l => l.Clone())];
    }

    private List<(string?, string?)> Vehiculos() =>
        [.. LineasJson().Select(l => (
            l.TryGetProperty("tipoVehiculo", out var v) ? v.GetString() : null,
            l.TryGetProperty("carroceria", out var c) ? c.GetString() : null))];

    /// <summary>
    /// Ruta 1 con cuatro camiones del cubicador (o los de <paramref name="cubicador"/>) y ruta 2 con el camión del respaldo
    /// (vehículo digitado <c>3s3</c> / <c>Furgón</c>, sin recomendación).
    /// </summary>
    private static clsSolicitudNexo Solicitud((string Tipo, string Carroceria)[]? cubicador = null, bool respaldo = true)
    {
        cubicador ??= [("3S3", "FURGON"), ("2S2", "ESTACAS"), ("cama_baja", "FURGON_REFRIGERADO"), ("TRACTOMÚLA", "TOLVA")];

        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-188-1" }).Valor;
        var linea = clsSolicitudCosteoMuestra.RutaCaptura();
        List<clsRutaCapturada> rutas =
            [new clsRutaCapturada(linea, 1, 0, [.. cubicador.Select(c => new clsCamionPlaneado(c.Tipo, c.Carroceria, 5_000m, Guid.NewGuid()))], [])];
        if (respaldo)
        {
            rutas.Add(new clsRutaCapturada(linea, 1, 1, [new clsCamionPlaneado("3s3", "Furgón", 28_500.5m)], []));
        }

        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, rutas);
        return new clsSolicitudNexo(clsClienteNexo.PorBp("15927"), cotizacion.EncabezadoSolicitud!,
            [.. cotizacion.LineasNexo().Select(l => l.Linea)], "PuertoAInterior");
    }

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
