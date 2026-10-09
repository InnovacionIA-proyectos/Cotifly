using System.Net;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// <see cref="clsCubicadorHttp"/> (HU-021.5, #133) contra el cubicador falso (<see cref="clsCubicadorFalso"/>, un
/// <see cref="HttpMessageHandler"/> que nunca sale a la red): forma de <c>POST /recomendaciones</c>, lectura de la Tabla 7 (C2, C3, C6),
/// catálogo con caché y traducción de los fallos a <c>CUBICADOR_NO_DISPONIBLE</c>. La tubería (sin reintento del POST, tiempo agotado)
/// se prueba con el registro real de Infraestructura.
/// </summary>
public sealed class clsCubicadorHttpTests : IDisposable
{
    private static readonly Guid _tipoCafe = Guid.Parse("6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50");
    private static readonly Guid _rutaId = Guid.Parse("0b9e1d7a-3c55-4b0e-8a11-5f2c6d7e8f90");

    private readonly clsCubicadorFalso _cubicador = new();
    private readonly HttpClient _http;
    private readonly clsCacheTiposMercancia _cache = new(TimeProvider.System);

    public clsCubicadorHttpTests() => _http = new HttpClient(_cubicador);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<clsMercanciaCubicador> Mercancias() =>
        [new clsMercanciaCubicador(_tipoCafe, 20, 120m, 100m, 150m, 700m), new clsMercanciaCubicador(_tipoCafe, 1, 1200m, 235m, 269m, null)];

    [Fact]
    public async Task MandaLaRutaConOrigenCotiflyLasMercanciasYLaLlaveComoBearer()
    {
        await Cubicador().RecomendarAsync(_rutaId, Mercancias(), Ct);

        var peticion = _cubicador.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be(clsCubicadorFalso.UrlBase + "/recomendaciones");
        peticion.Autorizacion.Should().Be("Bearer " + clsCubicadorFalso.ApiKey);
        var cuerpo = JsonNode.Parse(peticion.Cuerpo!)!;
        cuerpo["idLineaSolicitud"]!.GetValue<string>().Should().Be(_rutaId.ToString("D"));
        cuerpo["origenSistema"]!.GetValue<string>().Should().Be("COTIFLY");
        var mercancias = cuerpo["mercancias"]!.AsArray();
        mercancias.Should().HaveCount(2);
        mercancias[0]!["idTipoMercancia"]!.GetValue<Guid>().Should().Be(_tipoCafe);
        mercancias[0]!["cantidad"]!.GetValue<int>().Should().Be(20);
        mercancias[0]!["largoCm"]!.GetValue<decimal>().Should().Be(120m);
        mercancias[0]!["pesoKg"]!.GetValue<decimal>().Should().Be(700m);
        mercancias[1]!["pesoKg"].Should().BeNull("los nulos no viajan");
    }

    [Fact]
    public async Task LeeUnRegistroPorVehiculoConPesoIdYResultado()
    {
        _cubicador.RecomendarSiempre(
            clsCubicadorFalso.Fila("3S3", "FURGON", 16_000m),
            clsCubicadorFalso.Fila(null, null, 12_500m, "SIN_SELECCION_AUTOMATICA", "carga extradimensionada"),
            clsCubicadorFalso.Fila("2S2", "ESTACAS", 4_000m, "condicionada", "pendiente de AdminTarifas"));

        var resultado = await Cubicador().RecomendarAsync(_rutaId, Mercancias(), Ct);

        var vehiculos = resultado.Valor.Vehiculos;
        vehiculos.Should().HaveCount(3);
        vehiculos[0].Should().Match<clsVehiculoRecomendado>(v =>
            v.TipoVehiculo == "3S3" && v.Carroceria == "FURGON" && v.PesoTotalKg == 16_000m && v.IdRecomendacionVehiculo != null
            && v.Resultado == clsResultadosRecomendacion.Seleccionado);
        vehiculos[1].SinSeleccion.Should().BeTrue();
        vehiculos[1].Motivo.Should().Be("carga extradimensionada");
        vehiculos[2].Condicionada.Should().BeTrue("el resultado se normaliza a mayúsculas");
    }

    [Fact]
    public async Task AceptaUnArregloEnLaRaizYLosAliasDelDiseno()
    {
        _cubicador.CuerpoRecomendacion = """[{"id":"9d1a0b52-1111-4c3d-9e8f-0a1b2c3d4e5f","TipoVehiculo":"3S2","carroceria":"TANQUE","pesoKg":"9000.5"}]""";

        var vehiculo = (await Cubicador().RecomendarAsync(_rutaId, Mercancias(), Ct)).Valor.Vehiculos.Should().ContainSingle().Subject;

        vehiculo.Should().Be(new clsVehiculoRecomendado(
            Guid.Parse("9d1a0b52-1111-4c3d-9e8f-0a1b2c3d4e5f"), "3S2", "TANQUE", 9_000.5m, clsResultadosRecomendacion.Seleccionado));
    }

    [Theory]
    [InlineData("""{"otra":"cosa"}""")]
    [InlineData("no es json")]
    public async Task UnaRespuestaSinFilasOIlegibleNoEstaDisponible(string cuerpo)
    {
        _cubicador.CuerpoRecomendacion = cuerpo;

        var resultado = await Cubicador().RecomendarAsync(_rutaId, Mercancias(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task UnErrorHttpEsCubicadorNoDisponibleSinLaLlaveEnElDetalle(HttpStatusCode estado)
    {
        _cubicador.Caido = estado;

        var resultado = await Cubicador().RecomendarAsync(_rutaId, Mercancias(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().Contain(((int)estado).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain(clsCubicadorFalso.ApiKey);
        clsErroresCubicador.EsCaida(resultado.Error).Should().Be(estado != HttpStatusCode.BadRequest,
            "un 5xx (o la llave rechazada) es una caída; un 400 es un rechazo de esa petición");
    }

    [Fact]
    public async Task SinConfiguracionNoLlamaYNoEstaDisponible()
    {
        var cubicador = Cubicador(new clsOpcionesCubicador());

        cubicador.EstaConfigurado.Should().BeFalse();
        (await cubicador.RecomendarAsync(_rutaId, Mercancias(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ElCatalogoDeTiposDeMercanciaSeLeeUnaVezYQuedaEnCache()
    {
        _cubicador.TiposMercancia.Add((_tipoCafe, "Café en estibas"));
        _cubicador.TiposMercancia.Add((Guid.NewGuid(), "Bobinas de acero"));
        var cubicador = Cubicador();

        var primera = await cubicador.ListarTiposMercanciaAsync(Ct);
        var segunda = await Cubicador().ListarTiposMercanciaAsync(Ct);

        primera.Valor.Select(t => t.Nombre).Should().Equal("Bobinas de acero", "Café en estibas");
        segunda.Valor.Should().BeEquivalentTo(primera.Valor);
        _cubicador.Peticiones.Should().ContainSingle().Which.Url.AbsoluteUri.Should().Be(clsCubicadorFalso.UrlBase + "/tipos-mercancia");
    }

    [Fact]
    public async Task UnCatalogoVacioOCaidoNoSeGuardaEnCache()
    {
        var vacio = await Cubicador().ListarTiposMercanciaAsync(Ct);
        _cubicador.TiposMercancia.Add((_tipoCafe, "Café en estibas"));
        var despues = await Cubicador().ListarTiposMercanciaAsync(Ct);

        vacio.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        despues.Valor.Should().ContainSingle();
    }

    // --- Tubería real (registro de Infraestructura) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SinUrlBaseElAdaptadorEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Cubicador:UrlBase"] = url });

        proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().Should().BeOfType<clsCubicadorNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseSinLlaveLaConfiguracionEsInvalida()
    {
        using var proveedor = Proveedor(new() { ["Cubicador:UrlBase"] = clsCubicadorFalso.UrlBase });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Cubicador:ApiKey");
    }

    [Fact]
    public async Task UnPostFallidoNoSeReintenta()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        using var proveedor = Proveedor(Configurado(), _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(_rutaId, Mercancias(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().ContainSingle("el POST de la recomendación no es idempotente: nunca se reintenta");
    }

    [Fact]
    public async Task UnGetFallidoSiSeReintenta()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        using var proveedor = Proveedor(Configurado(), _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().ListarTiposMercanciaAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().HaveCount(1 + clsCubicadorHttp.ReintentosLectura);
    }

    [Fact]
    public async Task SiElCubicadorNoRespondeATiempoNoEstaDisponible()
    {
        _cubicador.Demora = TimeSpan.FromSeconds(10);
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(_rutaId, Mercancias(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().Contain("tiempo agotado");
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeTrue();
    }

    [Fact]
    public void ElTiempoTotalDejaCaberLosReintentosDelGet()
    {
        var intento = TimeSpan.FromSeconds(15);

        clsCubicadorHttp.TiempoTotal(intento).Should().BeGreaterThan(intento * (clsCubicadorHttp.ReintentosLectura + 1));
    }

    public void Dispose()
    {
        _http.Dispose();
        _cubicador.Dispose();
    }

    private clsCubicadorHttp Cubicador(clsOpcionesCubicador? opciones = null) => new(
        _http,
        Options.Create(opciones ?? new clsOpcionesCubicador { UrlBase = clsCubicadorFalso.UrlBase, ApiKey = clsCubicadorFalso.ApiKey }),
        _cache,
        NullLogger<clsCubicadorHttp>.Instance);

    private static Dictionary<string, string?> Configurado() => new()
    {
        ["Cubicador:UrlBase"] = clsCubicadorFalso.UrlBase,
        ["Cubicador:ApiKey"] = clsCubicadorFalso.ApiKey,
    };

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, clsCubicadorFalso? falso = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        if (falso is not null)
        {
            servicios.AddHttpClient(clsCubicadorHttp.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => falso);
        }

        return servicios.BuildServiceProvider();
    }
}
