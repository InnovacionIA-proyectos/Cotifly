using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>/web/*</c> de la consola del comercial por HTTP contra la base real (#12 parte B): respuestas que cumplen
/// <c>specs/openapi.yaml</c> (snake_case), aislamiento A/B en cada endpoint (404 indistinguible), validaciones 400 y la tabla de
/// códigos HTTP "Para #12" del README.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsWebEndpointsTests(clsEntornoApiWeb entorno)
{
    private const string NoEsTuya = """{"error":"Cotización no encontrada o no es tuya."}""";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;

    private clsApiWebPruebas Api => entorno.Api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MisCotizacionesCumpleElContratoYSoloTraeLasDelComercialDeLaMasRecienteALaMasAntigua()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var primera = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        await Task.Delay(20, Ct);
        var segunda = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ajena = await Api.CrearCotizacionAsync(luis.UsuarioId, "800333444");

        using var respuesta = await Api.EnviarAsync(_get, "/web/mis-cotizaciones", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/mis-cotizaciones", _get, HttpStatusCode.OK);

        var cotizaciones = json.GetProperty("cotizaciones").EnumerateArray().ToList();
        json.GetProperty("total").GetInt32().Should().Be(2);
        cotizaciones.Select(c => c.GetProperty("folio").GetString()).Should().Equal(segunda.Folio, primera.Folio);
        cotizaciones.Select(c => c.GetProperty("folio").GetString()).Should().NotContain(ajena.Folio);
        var fila = cotizaciones[1];
        fila.GetProperty("id").GetString().Should().Be(primera.Id.ToString());
        fila.GetProperty("linea_negocio").GetString().Should().Be("Nacional");
        fila.GetProperty("estado_envio").GetString().Should().Be("sin_enviar");
        fila.GetProperty("origen_margen").GetString().Should().Be("politica_linea");
        fila.GetProperty("nit_cliente").GetString().Should().Be("900111222");
        fila.GetProperty("precio_final").GetDecimal().Should().Be(1_176_471m);
        fila.GetProperty("pdf_url").GetString().Should().Be($"/pdfs/{primera.Folio}.pdf");
        fila.GetProperty("ganada").ValueKind.Should().Be(JsonValueKind.Null, "los nulos se escriben: el contrato los declara nullable, no opcionales");
        fila.TryGetProperty("precioFinal", out _).Should().BeFalse("/web/* nunca usa camelCase");
    }

    public static TheoryData<string> OperacionesSobreUnaCotizacion() =>
        ["seguimiento", "enviar", "vista-previa", "nueva-version", "pdf"];

    [Theory]
    [MemberData(nameof(OperacionesSobreUnaCotizacion))]
    public async Task UnaCotizacionAjenaRespondeExactamenteLoMismoQueUnaInexistenteYNoSeModifica(string operacion)
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var deLuis = await Api.CrearCotizacionAsync(luis.UsuarioId, "800333444");
        var inexistente = new clsCotizacionPrueba(Guid.NewGuid(), $"PCN-01011999-{Random.Shared.Next(100, 999)}", "PCN-X.pdf", null);

        using var ajena = await OperarAsync(operacion, ana.Access, deLuis);
        using var noExiste = await OperarAsync(operacion, ana.Access, inexistente);

        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ajena.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
        noExiste.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await noExiste.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
        var estado = await clsBaseDatosPruebas.EscalarAsync<string>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(estadoEnvio, '|', COALESCE(CAST(ganada AS nvarchar(5)), 'null')) FROM dbo_cotifly.Cotizaciones WHERE id = '{deLuis.Id}'");
        estado.Should().Be("sin_enviar|null", "el intento de otro comercial no cambia la cotización");
    }

    [Fact]
    public async Task ElSeguimientoDevuelveElResumenActualizadoYLeeElCuerpoEnSnakeCase()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/seguimiento";

        using var perdida = await Api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = "Precio alto" }));
        var json = await VerificarContratoAsync(perdida, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK);
        using var camel = await Api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(new { ganada = true, motivoRechazo = "no se lee" }));
        var jsonCamel = await VerificarContratoAsync(camel, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.OK);

        json.GetProperty("ganada").GetBoolean().Should().BeFalse();
        json.GetProperty("motivo_rechazo").GetString().Should().Be("Precio alto");
        json.GetProperty("linea_negocio").GetString().Should().Be("Nacional");
        jsonCamel.GetProperty("ganada").GetBoolean().Should().BeTrue();
        jsonCamel.GetProperty("motivo_rechazo").ValueKind.Should().Be(JsonValueKind.Null, "el cuerpo de /web/* se lee en snake_case: motivoRechazo no existe");
    }

    [Theory]
    [InlineData("motivo")]
    [InlineData("sin-cuerpo")]
    [InlineData("json-roto")]
    public async Task UnSeguimientoInvalidoResponde400ConErrorWeb(string caso)
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId);
        var cuerpo = caso switch
        {
            "motivo" => clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = new string('x', 2001) }),
            "json-roto" => "{no es json",
            _ => string.Empty,
        };

        using var respuesta = await Api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/seguimiento", ana.Access, cuerpo);

        await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/seguimiento", _post, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SinContentTypeJsonResponde415ConErrorWebYNoProblemDetails()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId);

        using var respuesta = await Api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/seguimiento", ana.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Cuerpo inválido: envía JSON con Content-Type: application/json."}""");
    }

    [Fact]
    public async Task ElTelefonoSeGuardaSeConsultaYSeValida()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var inicial = await Api.EnviarAsync(_get, "/web/usuario/telefono", ana.Access);
        var jsonInicial = await VerificarContratoAsync(inicial, "/web/usuario/telefono", _get, HttpStatusCode.OK);
        using var guardado = await Api.EnviarAsync(_put, "/web/usuario/telefono", ana.Access, clsApiWebPruebas.Json(new { telefono = "+57 300 123 4567" }));
        await VerificarContratoAsync(guardado, "/web/usuario/telefono", _put, HttpStatusCode.OK);
        using var consultado = await Api.EnviarAsync(_get, "/web/usuario/telefono", ana.Access);
        var jsonConsultado = await VerificarContratoAsync(consultado, "/web/usuario/telefono", _get, HttpStatusCode.OK);
        using var invalido = await Api.EnviarAsync(_put, "/web/usuario/telefono", ana.Access, clsApiWebPruebas.Json(new { telefono = "llámame" }));
        await VerificarContratoAsync(invalido, "/web/usuario/telefono", _put, HttpStatusCode.BadRequest);

        jsonInicial.GetProperty("telefono").ValueKind.Should().Be(JsonValueKind.Null);
        jsonConsultado.GetProperty("telefono").GetString().Should().Be("+57 300 123 4567");
    }

    [Fact]
    public async Task LosDatosDeNuevaVersionTraenElFolioBaseYLaVersionSiguiente()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");

        using var respuesta = await Api.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Id}/nueva-version", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{id}/nueva-version", _get, HttpStatusCode.OK);
        using var noUuid = await Api.EnviarAsync(_get, "/web/cotizaciones/no-es-uuid/nueva-version", ana.Access);

        json.GetProperty("cotizacion_origen_id").GetString().Should().Be(cotizacion.Id.ToString());
        json.GetProperty("folio_base").GetString().Should().Be(cotizacion.Folio);
        json.GetProperty("version_siguiente").GetInt32().Should().Be(2);
        json.GetProperty("linea_negocio").GetString().Should().Be("Nacional");
        json.GetProperty("metodo_calculo").GetString().Should().Be("margen");
        noUuid.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await noUuid.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
    }

    [Fact]
    public async Task LaVistaPreviaFuncionaConYSinBorrador()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa";

        using var sinBorrador = await Api.EnviarAsync(_post, ruta, ana.Access);
        var json = await VerificarContratoAsync(sinBorrador, "/web/cotizaciones/{folio}/correo/vista-previa", _post, HttpStatusCode.OK);
        using var conBorrador = await Api.EnviarAsync(_post, ruta, ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" }, cc = new[] { "jefe@cliente.com" }, asunto = "Propuesta" }));
        var jsonBorrador = await VerificarContratoAsync(conBorrador, "/web/cotizaciones/{folio}/correo/vista-previa", _post, HttpStatusCode.OK);

        json.GetProperty("asunto").GetString().Should().Be($"Cotización {cotizacion.Folio} — Botero Soto");
        json.GetProperty("adjunto").GetString().Should().Be(cotizacion.ArchivoPdf);
        jsonBorrador.GetProperty("asunto").GetString().Should().Be("Propuesta");
        jsonBorrador.GetProperty("para").EnumerateArray().Select(p => p.GetString()).Should().Equal("compras@cliente.com");
        jsonBorrador.GetProperty("cc").EnumerateArray().Select(p => p.GetString()).Should().Equal("jefe@cliente.com");
    }

    [Fact]
    public async Task ElEnvioSigueLaTablaDeCodigosHttp()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/enviar";
        var cuerpo = clsApiWebPruebas.Json(new { para = new[] { "cliente@empresa.com" } });

        using var deshabilitado = await entorno.ApiSinCorreo.EnviarAsync(_post, ruta, ana.Access, cuerpo);
        using var sinPara = await Api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(new { para = Array.Empty<string>() }));
        using var relayCaido = await Api.EnviarAsync(_post, ruta, ana.Access, cuerpo);

        deshabilitado.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(deshabilitado, "CORREO_DESHABILITADO");
        var jsonSinPara = await VerificarContratoAsync(sinPara, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest);
        jsonSinPara.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "CORREO_INVALIDO va como ErrorWeb");
        var jsonRelay = await VerificarContratoAsync(relayCaido, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest);
        jsonRelay.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CORREO_ENVIO_FALLIDO", "los errores del envío van como ErrorNegocio");
    }

    [Fact]
    public async Task LosDestinatariosPreviosSoloIncluyenLosEnviosDelComercial()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var nit = $"8{Random.Shared.Next(10_000_000, 99_999_999)}";
        var deLuis = await Api.CrearCotizacionAsync(luis.UsuarioId, nit);
        await Api.RegistrarEnvioAsync(deLuis.Id, luis.UsuarioId, "compras@cliente-b.com");

        using var paraLuis = await Api.EnviarAsync(_get, $"/web/clientes/{nit}/destinatarios-previos", luis.Access);
        var jsonLuis = await VerificarContratoAsync(paraLuis, "/web/clientes/{nit}/destinatarios-previos", _get, HttpStatusCode.OK);
        using var paraAna = await Api.EnviarAsync(_get, $"/web/clientes/{nit}/destinatarios-previos", ana.Access);
        var jsonAna = await VerificarContratoAsync(paraAna, "/web/clientes/{nit}/destinatarios-previos", _get, HttpStatusCode.OK);
        using var largo = await Api.EnviarAsync(_get, $"/web/clientes/{new string('9', 21)}/destinatarios-previos", ana.Access);

        jsonLuis.GetProperty("para").EnumerateArray().Select(p => p.GetString()).Should().Equal("compras@cliente-b.com");
        jsonAna.GetProperty("para").GetArrayLength().Should().Be(0);
        jsonAna.GetProperty("cc").GetArrayLength().Should().Be(0);
        largo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnCuerpoDeMasDe64KbEnWebResponde413()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await Api.EnviarAsync(_put, "/web/usuario/telefono", ana.Access,
            clsApiWebPruebas.Json(new { telefono = new string('1', 70 * 1024) }));

        respuesta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"El cuerpo de la petición es demasiado grande."}""");
    }

    private Task<HttpResponseMessage> OperarAsync(string operacion, string token, clsCotizacionPrueba cotizacion) => operacion switch
    {
        "seguimiento" => Api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/seguimiento", token, clsApiWebPruebas.Json(new { ganada = true })),
        "enviar" => Api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", token, clsApiWebPruebas.Json(new { para = new[] { "x@empresa.com" } })),
        "vista-previa" => Api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", token),
        "nueva-version" => Api.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Id}/nueva-version", token),
        "pdf" => Api.EnviarAsync(_get, $"/pdfs/{cotizacion.ArchivoPdf}", token),
        _ => throw new ArgumentOutOfRangeException(nameof(operacion), operacion, null),
    };
}
