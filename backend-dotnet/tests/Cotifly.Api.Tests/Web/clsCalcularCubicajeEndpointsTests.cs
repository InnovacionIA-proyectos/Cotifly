using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// "Calcular cubicaje" (<c>POST /web/cubicacion</c>, HU-023.2, #157) por HTTP con SQL Server real, el Nexo falso y el cubicador falso
/// (<see cref="clsCubicadorFalso"/>): la corrida de prueba <c>PRUEBA-&lt;guid&gt;</c>, la lista de vehículos (flota mixta) con los nombres
/// del catálogo, cada motivo de "sin selección", la condicionada, la descripción resuelta al código más parecido, la mercancía desconocida,
/// el cubicador caído o sin configurar y que no se guarda nada en la base ni se llama a Nexo. Cada respuesta se valida contra el contrato.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCalcularCubicajeEndpointsTests : IDisposable
{
    private const string Ruta = "/web/cubicacion";
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new();
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    /// <summary>Tablas de negocio que "Calcular cubicaje" no puede tocar.</summary>
    private static readonly string[] _tablas =
    [
        "CotizacionesSolicitud", "CotizacionLineasSolicitud", "CotizacionVehiculosLineaSolicitud", "CotizacionMercancias",
        "CotizacionServiciosConexos", "EventosHerramienta", "MargenAjustes", "Correos",
    ];

    public clsCalcularCubicajeEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("CAFE_EN_ESTIBAS", "Café en estibas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("QUIMICOS", "Tambor de químicos", Categoria: "PELIGROSA", Condicionada: true));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("BOBINAS_ACERO", "Bobinas de acero"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FlotaMixtaDevuelveLaListaDeVehiculosConUnaCorridaDePruebaYNoGuardaNada()
    {
        _cubicador.RecomendarSiempre(
            clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m),
            clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 4_500.5m, 18.25m, 60m, 75.01m));
        var (api, token) = await SesionAsync();
        var antes = await ConteosAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "CAFE_EN_ESTIBAS" }, claseViaje: "Contenedor"));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("resultado").GetString().Should().Be("ASIGNADO");
        json.GetProperty("motivo_sin_seleccion").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("condicionada").GetBoolean().Should().BeFalse();
        var vehiculos = json.GetProperty("vehiculos").EnumerateArray().ToList();
        vehiculos.Select(v => (v.GetProperty("codigo_vehiculo").GetString(), v.GetProperty("nombre_vehiculo").GetString(),
                v.GetProperty("codigo_carroceria").GetString(), v.GetProperty("nombre_carroceria").GetString()))
            .Should().Equal(("3S3", "Tractocamión 3S3", "FURGON", "Furgón"), ("C2", "Sencillo C2", "ESTACAS", "Estacas"));
        vehiculos[1].GetProperty("peso_total_kg").GetDecimal().Should().Be(4_500.5m);
        vehiculos[1].GetProperty("volumen_total_m3").GetDecimal().Should().Be(18.25m);
        vehiculos[1].GetProperty("porcentaje_llenado_volumen").GetDecimal().Should().Be(60m);
        vehiculos[1].GetProperty("porcentaje_llenado_peso").GetDecimal().Should().Be(75.01m);
        json.TryGetProperty("sugerencia", out _).Should().BeFalse("la forma anterior (un vehículo o N iguales) se retiró");
        json.GetRawText().Should().NotContain("clase_contenedor").And.NotContain("clase_viaje", "el cubicador ya no las sugiere");

        // Una corrida de prueba: PRUEBA-<guid>, origen COTIFLY, la clase de viaje como preferencia y la llave como Bearer.
        var peticion = _cubicador.Recomendaciones.Should().ContainSingle().Subject;
        var id = peticion["idLineaSolicitud"]!.GetValue<string>();
        id.Should().StartWith("PRUEBA-");
        Guid.TryParseExact(id["PRUEBA-".Length..], "D", out _).Should().BeTrue(id);
        peticion["origenSistema"]!.GetValue<string>().Should().Be("COTIFLY");
        peticion["claseViajeSeleccionada"]!.GetValue<string>().Should().Be("Contenedor");
        peticion["mercancias"]![0]!["codigoMercancia"]!.GetValue<string>().Should().Be("CAFE_EN_ESTIBAS");
        _cubicador.Peticiones.Should().OnlyContain(p => p.Autorizacion == "Bearer " + clsCubicadorFalso.ApiKey);

        // Cotifly no guarda nada de esa llamada ni llama a Nexo.
        (await ConteosAsync()).Should().Equal(antes);
        _nexo.UltimaAlta.Should().BeNull();
    }

    [Fact]
    public async Task ConLaBanderaDelPlanBLaRespuestaTraeLaSugerenciaLegadaYCumpleElContrato()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m));
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador,
            extra: new Dictionary<string, string?> { ["Cubicacion:IncluirSugerenciaLegada"] = "true" });
        _apis.Add(api);
        var token = (await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)).Access;

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "CAFE_EN_ESTIBAS" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        var sugerencia = json.GetProperty("sugerencia");
        sugerencia.GetProperty("unidad_id").GetString().Should().Be("3S3");
        sugerencia.GetProperty("unidad_nombre").GetString().Should().Be("Tractocamión 3S3");
        sugerencia.GetProperty("carroceria").GetString().Should().Be("FURGON");
        sugerencia.GetProperty("factor_utilizacion").GetDecimal().Should().Be(0.8125m);
        json.GetProperty("vehiculos").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("EXTRADIMENSIONADO")]
    [InlineData("SIN_CANDIDATOS_DIMENSIONALES")]
    [InlineData("SUPERA_K_MAX")]
    [InlineData("SIN_CARROCERIA_COMPATIBLE")]
    public async Task CadaMotivoDeSinSeleccionLlegaConSuMotivoYSinVehiculos(string motivo)
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.SinSeleccion(motivo);
        var (api, token) = await SesionAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "BOBINAS_ACERO" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("resultado").GetString().Should().Be("SIN_SELECCION_AUTOMATICA");
        json.GetProperty("motivo_sin_seleccion").GetString().Should().Be(motivo);
        json.GetProperty("vehiculos").GetArrayLength().Should().Be(0);
        var extradimension = json.GetProperty("extradimension");
        if (motivo == "EXTRADIMENSIONADO")
        {
            extradimension.GetProperty("hay_extradimension").GetBoolean().Should().BeTrue();
            extradimension.GetProperty("medidas_estandar").GetProperty("alto_max_cm").GetDecimal().Should().Be(300m);
            var pieza = extradimension.GetProperty("piezas").EnumerateArray().Should().ContainSingle().Subject;
            pieza.GetProperty("indice_posicion").GetInt32().Should().Be(0);
            pieza.GetProperty("excesos").EnumerateArray().Select(d => d.GetString()).Should().Equal("alto 320.0 cm > 300.0 cm");
            json.GetProperty("mensaje_sin_seleccion").GetString().Should().NotContain("cabe");
        }
        else
        {
            extradimension.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task LaCondicionadaLlegaConSusMercanciasYSusAdvertencias()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.Condicionada;
        var (api, token) = await SesionAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "QUIMICOS" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        json.GetProperty("condicionada").GetBoolean().Should().BeTrue();
        json.GetProperty("mercancias_condicionadas").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("codigo_mercancia").GetString().Should().Be("QUIMICOS");
        json.GetProperty("advertencias").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("mensaje").GetString().Should().Contain("Mesa de Costeo");
        json.GetProperty("vehiculos").EnumerateArray().Should().ContainSingle()
            .Which.GetProperty("nombre_vehiculo").GetString().Should().Be("Tractocamión 2S2");
    }

    [Theory]
    [InlineData("Café", "CAFE_EN_ESTIBAS")]
    [InlineData("CAFÉ EN ESTIBAS", "CAFE_EN_ESTIBAS")]
    [InlineData("bobina de ACERO", "BOBINAS_ACERO")]
    public async Task LaDescripcionSinCodigoSeResuelveAlTipoMasParecido(string descripcion, string codigo)
    {
        var (api, token) = await SesionAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { descripcion }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);

        var mercancia = json.GetProperty("mercancias").EnumerateArray().Should().ContainSingle().Subject;
        mercancia.GetProperty("codigo_mercancia").GetString().Should().Be(codigo);
        mercancia.GetProperty("descripcion").GetString().Should().Be(descripcion);
        mercancia.GetProperty("sugerida").GetBoolean().Should().BeTrue();
        _cubicador.Recomendaciones.Should().ContainSingle().Which["mercancias"]![0]!["codigoMercancia"]!.GetValue<string>().Should().Be(codigo);
    }

    [Fact]
    public async Task UnaDescripcionSinCoincidenciaEsMercanciaDesconocidaConUnMensajeClaro()
    {
        var (api, token) = await SesionAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { descripcion = "Electrodomésticos" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("MERCANCIA_DESCONOCIDA");
        json.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("«Electrodomésticos»");
        _cubicador.Recomendaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task UnCodigoQueElCubicadorNoConoceSeRechazaConUnMensajeClaro()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "MERCANCIA_DESCONOCIDA");
        var (api, token) = await SesionAsync();
        var antes = await ConteosAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "NO_EXISTE" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("MERCANCIA_DESCONOCIDA");
        json.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("escógela del catálogo");
        (await ConteosAsync()).Should().Equal(antes);
    }

    [Fact]
    public async Task UnCubicadorCaidoResponde503SinDetalleTecnicoYSinReintentarElPost()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var (api, token) = await SesionAsync();
        var antes = await ConteosAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, Cuerpo(new { codigo_mercancia = "CAFE_EN_ESTIBAS" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CUBICADOR_NO_DISPONIBLE");
        json.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("cubicaje");
        json.GetRawText().Should().NotContain("HTTP").And.NotContain("/recomendaciones", "el detalle técnico solo va al log");
        _cubicador.Recomendaciones.Should().ContainSingle("el POST nunca se reintenta");
        (await ConteosAsync()).Should().Equal(antes);
    }

    [Fact]
    public async Task SinCubicadorConfiguradoResponde503()
    {
        var api = _entorno.Api;
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, Ruta, sesion.Access, Cuerpo(new { codigo_mercancia = "CAFE_EN_ESTIBAS" }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.ServiceUnavailable);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CUBICADOR_NO_DISPONIBLE");
        json.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("no está configurado");
    }

    [Fact]
    public async Task SinItemsRespondeItemsRequeridos()
    {
        var (api, token) = await SesionAsync();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token, clsApiWebPruebas.Json(new { items = Array.Empty<object>() }));
        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("ITEMS_REQUERIDOS");
        _cubicador.Peticiones.Should().BeEmpty();
    }

    public void Dispose()
    {
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
        _cubicador.Dispose();
    }

    private async Task<(clsApiWebPruebas Api, string Token)> SesionAsync()
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador);
        _apis.Add(api);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        return (api, sesion.Access);
    }

    /// <summary>Un ítem (10 estibas de 120 × 100 × 150 cm y 800 kg) con el tipo de mercancía dado.</summary>
    private static string Cuerpo(object mercancia, string? claseViaje = null)
    {
        var item = JsonSerializer.SerializeToNode(mercancia)!.AsObject();
        item["cantidad"] = 10;
        item["largo_cm"] = 120;
        item["ancho_cm"] = 100;
        item["alto_cm"] = 150;
        item["peso_kg"] = 800;
        return clsApiWebPruebas.Json(new { items = new[] { item }, clase_viaje = claseViaje });
    }

    /// <summary>Filas de cada tabla de negocio (<see cref="_tablas"/>), en orden.</summary>
    private async Task<IReadOnlyList<int>> ConteosAsync()
    {
        var conteos = new List<int>();
        foreach (var tabla in _tablas)
        {
            conteos.Add(await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly, $"SELECT COUNT(*) FROM dbo_cotifly.{tabla}"));
        }

        return conteos;
    }
}
