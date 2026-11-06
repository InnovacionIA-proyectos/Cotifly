using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-030.1 (#184): peso con tara de una ruta de contenedor, por HTTP con SQL Server real, el Nexo falso (que valida
/// <c>PESO_INCONSISTENTE</c> como Nexo: #250, DU §8, solo una suma de posiciones que SUPERA el peso en más de 1 %) y el cubicador falso. El
/// comercial escribe <c>peso_con_tara_kg</c> por ruta (captura, edición y licitación); se guarda en
/// <c>CotizacionLineasSolicitud.pesoConTaraKg</c> (script 11) y viaja a Nexo como el peso de las líneas de la ruta (repartida la tara por
/// camión). #250: con un solo camión la línea lleva además sus posiciones; con varios, sin posiciones. Al cubicador va lo mismo que antes.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPesoConTaraEndpointsTests : IDisposable
{
    private const string Captura = "/web/solicitudes-costeo";
    private const string Edicion = "/web/cotizaciones/{folio}/solicitud";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsPesoConTaraEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add((Guid.Parse(clsCosteoEndpointsTests.TipoMercancia), "Café en estibas"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnaRutaContenedorConTaraYUnCamionViajaANexoConElPesoConTaraYSusPosiciones()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario().ConTara(32_300.5m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        var linea = _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["peso"]!.GetValue<decimal>().Should().Be(32_300.5m, "a Nexo va el peso con tara que escribió el comercial");
        linea["posiciones"]!.AsArray().Select(p => p!["cantidad"]!.GetValue<int>() * p["pesoKg"]!.GetValue<decimal>())
            .Should().HaveCount(2).And.Subject.Sum().Should().Be(28_500m, "#250 (DU §8): las posiciones viajan con las mercancías, sin tara");
        _nexo.AltasPesoInconsistente.Should().Be(0, "un peso mayor que la suma de las posiciones es la tara y Nexo lo acepta");
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        (await PesoConTaraGuardadoAsync(folio)).Should().Be(32_300.5m, "se guarda en la ruta (script 11)");

        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var ruta = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK)).GetProperty("rutas")[0];
        ruta.GetProperty("peso_con_tara_kg").GetDecimal().Should().Be(32_300.5m);
        ruta.GetProperty("peso_kg").GetDecimal().Should().Be(28_500.5m, "el peso de carga no cambia");
        ruta.GetProperty("camiones")[0].GetProperty("peso_con_tara_kg").GetDecimal().Should().Be(32_300.5m);
    }

    [Fact]
    public async Task ConVariosCamionesLaTaraSeRepartePorPartesIgualesYLoQueVaAlCubicadorNoCambia()
    {
        _cubicador.RecomendarSiempre(
            clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_500.5m));
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        // Carga 28500.5 kg; con tara 36100.5 kg: 7600 kg de tara (dos contenedores de 3800 kg).
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario().ConTara(36_100.5m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => l["peso"]!.GetValue<decimal>()).Should().Equal(19_800m, 16_300.5m);
        lineas.Should().OnlyContain(l => l["posiciones"] == null);
        lineas.Sum(l => l["peso"]!.GetValue<decimal>()).Should().Be(36_100.5m, "la suma de las líneas es el peso con tara de la ruta");
        _nexo.AltasPesoInconsistente.Should().Be(0);

        var alCubicador = _cubicador.Recomendaciones.Should().ContainSingle().Subject;
        alCubicador.ToJsonString().Should().NotContain("36100", "el cubicador recibe el peso por mercancía, sin tara");
        alCubicador["mercancias"]!.AsArray().Select(m => m!["pesoKg"]!.GetValue<decimal>()).Should().Equal(1424.95m, 1m);

        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var camiones = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas")[0].GetProperty("camiones").EnumerateArray().ToList();
        camiones.Select(c => (c.GetProperty("peso_kg").GetDecimal(), c.GetProperty("peso_con_tara_kg").GetDecimal()))
            .Should().Equal((16_000m, 19_800m), (12_500.5m, 16_300.5m));
    }

    [Fact]
    public async Task SinTaraLaRutaContenedorSigueViajandoComoAntesConSusPosiciones()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        var linea = _nexo.UltimaAlta!["lineas"]![0]!;
        linea["peso"]!.GetValue<decimal>().Should().Be(28_500.5m);
        linea["posiciones"]!.AsArray().Should().HaveCount(2);
        (await PesoConTaraGuardadoAsync(folio)).Should().BeNull();
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var ruta = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK)).GetProperty("rutas")[0];
        ruta.GetProperty("peso_con_tara_kg").ValueKind.Should().Be(JsonValueKind.Null);
        ruta.GetProperty("camiones")[0].GetProperty("peso_con_tara_kg").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ConTaraEnOtraClaseDeViajeEs400CampoNoAplicaYNoSeGuardaNiSeEnviaNada()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = JsonNode.Parse(clsCosteoEndpointsTests.Formulario().ConTara(30_000m))!;
        var ruta = formulario["rutas"]![0]!.AsObject();
        ruta["clase_viaje"] = "Cupo";
        ruta["tipologia"] = "CargaSeca";
        ruta.Remove("clase_contenedor");

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, formulario.ToJsonString());

        var error = await ErrorDeCampoAsync(envio, "peso_con_tara_kg");
        error.GetProperty("codigo").GetString().Should().Be("CAMPO_NO_APLICA");
        error.GetProperty("mensaje").GetString().Should().Contain("Contenedor");
        _nexo.SolicitudesCreadas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(28_000, "PESO_CON_TARA_MENOR_QUE_CARGA")]
    [InlineData(1_000_000, "VALOR_FUERA_DE_RANGO")]
    [InlineData(30_000.125, "VALOR_FUERA_DE_RANGO")]
    public async Task UnPesoConTaraMenorQueLaCargaOFueraDelTopeEs400(double conTara, string codigo)
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario().ConTara((decimal)conTara));

        (await ErrorDeCampoAsync(envio, "peso_con_tara_kg")).GetProperty("codigo").GetString().Should().Be(codigo);
        _nexo.SolicitudesCreadas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnPesoConTaraIgualALaCargaSeAcepta()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario().ConTara(28_500.5m));

        await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created);
        _nexo.UltimaAlta!["lineas"]![0]!["posiciones"]!.AsArray().Should().HaveCount(2);
        _nexo.AltasPesoInconsistente.Should().Be(0);
    }

    /// <summary>
    /// HU-028 (#186) + HU-030.1: el comercial agrega (o corrige) el peso con tara en "Editar solicitud". Se guarda, el GET lo devuelve y la
    /// captura nueva en Nexo lleva el peso con tara y, con un solo camión, sus posiciones (#250). Cambiar solo la tara no vuelve a cubicar.
    /// </summary>
    [Fact]
    public async Task EnLaEdicionSeAgregaElPesoConTaraSinRecubicarYLaCapturaNuevaLoLleva()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.UltimaAlta!["lineas"]![0]!["posiciones"].Should().NotBeNull("sin tara viajan las posiciones");
        var cubicaciones = _cubicador.Recomendaciones.Count;

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        modelo["rutas"]![0]!.AsObject().TryGetPropertyValue("peso_con_tara_kg", out var antes).Should().BeTrue("el GET siempre trae el campo");
        antes.Should().BeNull("la ruta se capturó sin tara");
        modelo["rutas"]![0]!["peso_con_tara_kg"] = 32_300.5m;
        using var editada = await EditarAsync(api, ana, folio, modelo, etag);

        var json = await VerificarContratoAsync(editada, Edicion, _put, HttpStatusCode.OK);
        json.GetProperty("modo").GetString().Should().Be("reemplazo");
        json.GetProperty("rutas_replanificadas").EnumerateArray().Should().BeEmpty("la tara no cambia lo que recibe el cubicador");
        _cubicador.Recomendaciones.Should().HaveCount(cubicaciones);
        var linea = _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["peso"]!.GetValue<decimal>().Should().Be(32_300.5m);
        linea["posiciones"]!.AsArray().Should().HaveCount(2, "#250: con un solo camión viajan también con tara");
        _nexo.AltasPesoInconsistente.Should().Be(0);
        (await PesoConTaraGuardadoAsync(folio)).Should().Be(32_300.5m);

        var (releido, etag2) = await ModeloAsync(api, ana, folio);
        releido["rutas"]![0]!["peso_con_tara_kg"]!.GetValue<decimal>().Should().Be(32_300.5m);

        // El mismo modelo otra vez no cambia nada; quitar la tara (null) vuelve al peso de la carga, con las mismas posiciones.
        releido["rutas"]![0]!["peso_con_tara_kg"] = null;
        using var sinTara = await EditarAsync(api, ana, folio, releido, etag2);
        await VerificarContratoAsync(sinTara, Edicion, _put, HttpStatusCode.OK);
        (await PesoConTaraGuardadoAsync(folio)).Should().BeNull();
        _nexo.UltimaAlta!["lineas"]![0]!["posiciones"]!.AsArray().Should().HaveCount(2);
        _nexo.UltimaAlta!["lineas"]![0]!["peso"]!.GetValue<decimal>().Should().Be(28_500.5m);
    }

    [Fact]
    public async Task EnLaEdicionUnaTaraEnOtraClaseDeViajeEs400CampoNoAplica()
    {
        var api = Api();
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Captura, ana.Access, clsCosteoEndpointsTests.Formulario().ConTara(32_000m));
        var folio = (await VerificarContratoAsync(envio, Captura, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        var (modelo, etag) = await ModeloAsync(api, ana, folio);
        var ruta = modelo["rutas"]![0]!.AsObject();
        ruta["clase_viaje"] = "Cupo";
        ruta["tipologia"] = "CargaSeca";
        ruta["clase_contenedor"] = null;
        using var editada = await EditarAsync(api, ana, folio, modelo, etag);

        (await ErrorDeCampoAsync(editada, "peso_con_tara_kg")).GetProperty("codigo").GetString().Should().Be("CAMPO_NO_APLICA");
        (await PesoConTaraGuardadoAsync(folio)).Should().Be(32_000m, "un 400 no cambia nada");
    }

    private clsApiWebPruebas Api()
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador);
        _apis.Add(api);
        return api;
    }

    private Task<decimal?> PesoConTaraGuardadoAsync(string folio) =>
        clsBaseDatosPruebas.EscalarAsync<decimal?>(_entorno.BaseDatos.CadenaCotifly,
            "SELECT l.pesoConTaraKg FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c "
            + $"ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    /// <summary>El 400 <c>SOLICITUD_COSTEO_INVALIDA</c> con su error de <paramref name="campo"/> en <c>detalle.errores</c>.</summary>
    private static async Task<JsonElement> ErrorDeCampoAsync(HttpResponseMessage respuesta, string campo)
    {
        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest, await respuesta.Content.ReadAsStringAsync(Ct));
        using var cuerpo = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
        var error = cuerpo.RootElement.GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("SOLICITUD_COSTEO_INVALIDA");
        return error.GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Should().ContainSingle(e => e.GetProperty("campo").GetString() == campo).Subject.Clone();
    }

    private static async Task<(JsonNode Modelo, string? Etag)> ModeloAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/solicitud", sesion.Access);
        await VerificarContratoAsync(respuesta, Edicion, _get, HttpStatusCode.OK);
        return (JsonNode.Parse(await respuesta.Content.ReadAsStringAsync(Ct))!, respuesta.Headers.ETag?.Tag);
    }

    private static async Task<HttpResponseMessage> EditarAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio, JsonNode cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(_put, $"/web/cotizaciones/{folio}/solicitud");
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return await api.Cliente.SendAsync(peticion, Ct);
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
}

/// <summary>HU-030.1 (#184): el formulario de prueba con <c>peso_con_tara_kg</c> en su primera ruta.</summary>
internal static class clsFormularioConTara
{
    public static string ConTara(this string formulario, decimal pesoConTaraKg)
    {
        var json = JsonNode.Parse(formulario)!;
        json["rutas"]![0]!["peso_con_tara_kg"] = pesoConTaraKg;
        return json.ToJsonString();
    }
}
