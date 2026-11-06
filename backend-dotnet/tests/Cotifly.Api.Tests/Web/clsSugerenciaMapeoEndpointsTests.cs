using System.Net;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-044.2 (#253) por HTTP contra la API real y el Nexo falso: <c>POST /web/licitaciones/sugerir-mapeo</c> (propuesta con IA, plantilla
/// reconocida, sin propuesta con más de 100 columnas, importación apagada o NEXO caído, tope diario de IA) y su paso previo
/// <c>POST /web/licitaciones/leer-estructura</c> (primera lectura, corrección con la receta exacta, reaplicar sin IA, IA no disponible,
/// archivo rechazado sin llamar a NEXO, códigos de NEXO, sin reintentos).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSugerenciaMapeoEndpointsTests : IDisposable
{
    private const string RutaSugerir = "/web/licitaciones/sugerir-mapeo";
    private const string RutaLeer = "/web/licitaciones/leer-estructura";
    private const string RutaNexoSugerir = "/api/captura-solicitudes/sugerir-mapeo";
    private const string RutaNexoLeer = "/api/captura-solicitudes/leer-estructura";
    private const string Nit = "900.555.111-2";

    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsSugerenciaMapeoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Hoja(int columnas = 3, string? nit = Nit)
    {
        string[] base_ = ["ORIGEN", "DESTINO", "TON"];
        var encabezados = Enumerable.Range(0, columnas).Select(i => i < base_.Length ? base_[i] : $"EXTRA {i}").ToArray();
        return JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            encabezados,
            filas = new[] { encabezados.Select((_, i) => i switch { 0 => "Buenaventura", 1 => "Bogotá", 2 => "32", _ => "x" }).ToArray() },
        });
    }

    private Task<HttpResponseMessage> SugerirAsync(clsSesionPrueba sesion, string cuerpo) =>
        _api.EnviarAsync(HttpMethod.Post, RutaSugerir, sesion.Access, cuerpo);

    private static MultipartFormDataContent Formulario(
        byte[]? archivo = null, string nombre = "EPM_formulario.xlsx", string? nit = Nit, string? instrucciones = null, string? receta = null)
    {
        var formulario = new MultipartFormDataContent { { new ByteArrayContent(archivo ?? [0x50, 0x4B, 3, 4]), "archivo", nombre } };
        foreach (var (campo, valor) in new[] { ("nit_cliente", nit), ("instrucciones", instrucciones), ("receta", receta) })
        {
            if (valor is not null)
            {
                formulario.Add(new StringContent(valor, Encoding.UTF8), campo);
            }
        }

        return formulario;
    }

    private Task<HttpResponseMessage> LeerAsync(clsSesionPrueba sesion, HttpContent contenido) =>
        _api.EnviarContenidoAsync(HttpMethod.Post, RutaLeer, sesion.Access, contenido);

    [Fact]
    public async Task LaSugerenciaConIaCumpleElContratoYUsaLosCamposDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await SugerirAsync(sesion, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        json.GetProperty("ia_usada").GetBoolean().Should().BeTrue();
        json.GetProperty("motivo_sin_ia").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("dudas").EnumerateArray().Select(d => d.GetString()).Should().Equal("¿TON es el peso total?");
        json.GetProperty("columnas").EnumerateArray().Select(c => (c.GetProperty("indice").GetInt32(), c.GetProperty("campo").GetString(),
                c.GetProperty("transformacion").GetString(), c.GetProperty("origen").GetString()))
            .Should().Equal((0, "origen", "ninguna", "ia"), (1, "destino", "ninguna", "ia"), (2, "peso", "peso_toneladas_a_kg", "ia"));
        var enviada = _nexo.Peticiones.Single(p => p.Url.AbsolutePath == RutaNexoSugerir);
        using var cuerpo = JsonDocument.Parse(enviada.Cuerpo!);
        cuerpo.RootElement.GetProperty("clienteNit").GetString().Should().Be("900555111", "el NIT viaja como lo compara NEXO");
        enviada.IdPortalUsuario.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConLaPlantillaDelClienteLaSugerenciaVieneDeLaPlantillaSinIa()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var plantillaId = await GuardarPlantillaEnNexoAsync();

        using var respuesta = await SugerirAsync(sesion, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "PLANTILLA_RECONOCIDA"));
        json.GetProperty("plantilla_id").GetGuid().Should().Be(plantillaId);
        json.GetProperty("columnas").EnumerateArray().Should().OnlyContain(c => c.GetProperty("origen").GetString() == "plantilla");
    }

    [Fact]
    public async Task ConMasDeCienColumnasNoLlamaANexoYResponde200SinPropuesta()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await SugerirAsync(sesion, Hoja(columnas: 101));

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "DEMASIADAS_COLUMNAS"));
        json.GetProperty("columnas").GetArrayLength().Should().Be(101);
        json.GetProperty("columnas").EnumerateArray().Should().OnlyContain(c => c.GetProperty("campo").GetString() == "ignorar");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(0);
    }

    [Fact]
    public async Task ConLaIaDeNexoApagadaLaSugerenciaEs200SinIa()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.IaImportacionDisponible = false;

        using var respuesta = await SugerirAsync(sesion, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "IA_NO_DISPONIBLE"));
    }

    [Fact]
    public async Task ConNexoCaidoLaSugerenciaEs200SinPropuestaYNoSeReintenta()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await SugerirAsync(sesion, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "NEXO_NO_DISPONIBLE"));
        json.GetProperty("columnas").EnumerateArray().Select(c => c.GetProperty("columna").GetString()).Should().Equal("ORIGEN", "DESTINO", "TON");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(1, "sugerir-mapeo puede gastar IA: nunca se reintenta");
    }

    [Fact]
    public async Task ConLaImportacionApagadaLaSugerenciaEs200SinPropuestaSinLlamarANexo()
    {
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?> { ["ImportacionInteligente:Habilitada"] = "false" });
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(HttpMethod.Post, RutaSugerir, sesion.Access, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "NEXO_NO_DISPONIBLE"));
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(0);
    }

    [Fact]
    public async Task ElTopeDiarioDeIaEnLaSugerenciaEsUnErrorDeNegocio429()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "IA_TOPE_DIARIO";

        using var respuesta = await SugerirAsync(sesion, Hoja());

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.TooManyRequests);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("IA_TOPE_DIARIO");
        respuesta.Headers.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task SinEncabezadosEsEncabezadosRequeridosYUnCuerpoMalFormadoEsErrorWeb()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var vacia = await SugerirAsync(sesion, """{"nit_cliente":"900555111","encabezados":[]}""");
        using var rota = await SugerirAsync(sesion, """{"encabezados": 3}""");

        (await VerificarContratoAsync(vacia, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ENCABEZADOS_REQUERIDOS");
        (await VerificarContratoAsync(rota, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "ErrorWeb");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(0);
    }

    [Fact]
    public async Task LaPrimeraLecturaReenviaElArchivoConSuNombreYDevuelveLaRecetaYLasTablasSinAlterar()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await LeerAsync(sesion, Formulario());

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("origen_receta").GetString(), json.GetProperty("ia_usada").GetBoolean()).Should().Be(("ia", true));
        json.GetProperty("receta").GetRawText().Should().Be(clsNexoFalso.RecetaDePrueba, "la receta es opaca: sus claves no pasan a snake_case");
        var tabla = json.GetProperty("tablas")[0];
        (tabla.GetProperty("nombre").GetString(), tabla.GetProperty("fila_encabezado").GetInt32(), tabla.GetProperty("total_filas").GetInt32())
            .Should().Be(("Pliego", 3, 2));
        tabla.GetProperty("encabezados").EnumerateArray().Select(e => e.GetString()).Should().Equal("ORIGEN", "DESTINO", "TON");
        json.GetProperty("hojas_del_archivo")[0].GetProperty("usada").GetBoolean().Should().BeTrue();
        json.GetProperty("cambios").ValueKind.Should().Be(JsonValueKind.Null);
        var formulario = _nexo.UltimoFormularioImportacion!;
        formulario["archivo"].Should().Be("EPM_formulario.xlsx");
        formulario["clienteNit"].Should().Be("900555111");
        formulario.Should().NotContainKeys("instrucciones", "receta");
    }

    [Fact]
    public async Task UnaTablaDeLaLecturaEntraASugerirMapeoSinTransformarla()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var lectura = await LeerAsync(sesion, Formulario());
        var tabla = (await VerificarContratoAsync(lectura, RutaLeer, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("tablas")[0];

        var cuerpo = JsonSerializer.Serialize(new { nit_cliente = Nit, encabezados = tabla.GetProperty("encabezados"), filas = tabla.GetProperty("filas") });
        using var respuesta = await SugerirAsync(sesion, cuerpo);

        (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK))
            .GetProperty("columnas").EnumerateArray().Select(c => c.GetProperty("campo").GetString()).Should().Equal("origen", "destino", "peso");
    }

    [Fact]
    public async Task LaCorreccionLlegaANexoConExactamenteLaRecetaAnterior()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var primera = await LeerAsync(sesion, Formulario());
        var receta = (await VerificarContratoAsync(primera, RutaLeer, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("receta").GetRawText();
        var recetaConEspacios = " " + receta + "\n";

        using var segunda = await LeerAsync(sesion, Formulario(instrucciones: "el destino es la casilla al lado de SERVICIO", receta: recetaConEspacios));

        var json = await VerificarContratoAsync(segunda, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        json.GetProperty("ia_usada").GetBoolean().Should().BeTrue();
        var formulario = _nexo.UltimoFormularioImportacion!;
        formulario["receta"].Should().Be(recetaConEspacios, "Cotifly no toca la receta (ni sus espacios)");
        formulario["instrucciones"].Should().Be("el destino es la casilla al lado de SERVICIO");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(2);
    }

    [Fact]
    public async Task ReaplicarUnaRecetaSinInstruccionesEs200SinIa()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await LeerAsync(sesion, Formulario(receta: clsNexoFalso.RecetaDePrueba));

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("origen_receta").GetString(), json.GetProperty("ia_usada").GetBoolean()).Should().Be(("manual", false));
        json.GetProperty("motivo_sin_ia").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ConLaIaDeNexoApagadaLaLecturaEs200ConIaUsadaFalse()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.IaImportacionDisponible = false;

        using var respuesta = await LeerAsync(sesion, Formulario());

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "IA_NO_DISPONIBLE"));
        json.GetProperty("tablas").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("pliego.xls", 10, "FORMATO_NO_SOPORTADO")]
    [InlineData("pliego.csv", 10, "FORMATO_NO_SOPORTADO")]
    [InlineData("pliego.xlsx", 10_500_000, "ARCHIVO_DEMASIADO_GRANDE")]
    [InlineData("pliego.xlsx", 0, "ARCHIVO_REQUERIDO")]
    public async Task UnArchivoGrandeODeOtraExtensionSeRechazaSinLlamarANexo(string nombre, int bytes, string codigo)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await LeerAsync(sesion, Formulario(new byte[bytes], nombre));

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(0);
    }

    [Fact]
    public async Task SinArchivoEsArchivoRequeridoSinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await LeerAsync(sesion, new MultipartFormDataContent { { new StringContent(Nit), "nit_cliente" } });

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ARCHIVO_REQUERIDO");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(0);
    }

    [Fact]
    public async Task UnArchivoQueCotiflyNoPodriaAbrirIgualLlegaANexo()
    {
        // #277: Cotifly no abre el libro. Estos bytes no son un .xlsx; decide NEXO (aquí, el falso lo lee).
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await LeerAsync(sesion, Formulario(Encoding.ASCII.GetBytes("no soy un zip"), "raro.xlsm"));

        await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.UltimoFormularioImportacion!["archivo"].Should().Be("raro.xlsm");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "EXCEL_INVALIDO", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadRequest, "EXCEL_SIN_DATOS", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadRequest, "RECETA_INVALIDA", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadRequest, "INSTRUCCIONES_DEMASIADO_LARGAS", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION_ERROR", HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES", HttpStatusCode.Forbidden)]
    public async Task LosErroresDeNexoEnLaLecturaLleganConSuCodigoYSuMensaje(HttpStatusCode estadoNexo, string codigoNexo, HttpStatusCode esperado)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ImportacionFalla = estadoNexo;
        _nexo.CodigoImportacionFalla = codigoNexo;

        using var respuesta = await LeerAsync(sesion, Formulario());

        var error = (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, esperado)).GetProperty("error");
        if (esperado == HttpStatusCode.Forbidden)
        {
            error.GetProperty("codigo").GetString().Should().Be("NEXO_SIN_PERMISO");
            return;
        }

        (error.GetProperty("codigo").GetString(), error.GetProperty("mensaje").GetString()).Should().Be((codigoNexo, _nexo.MensajeImportacionFalla));
    }

    [Fact]
    public async Task ElTopeDiarioDeIaEnLaLecturaEs429YOtroComercialSigueUsandoLaIa()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "IA_TOPE_DIARIO";

        using var agotada = await LeerAsync(ana, Formulario());
        _nexo.ImportacionFalla = null; // NEXO limita a ana, no a luis
        using var otra = await LeerAsync(luis, Formulario());

        (await VerificarContratoAsync(agotada, RutaLeer, HttpMethod.Post, HttpStatusCode.TooManyRequests))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("IA_TOPE_DIARIO");
        agotada.Headers.RetryAfter.Should().BeNull();
        (await VerificarContratoAsync(otra, RutaLeer, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("ia_usada").GetBoolean().Should().BeTrue();
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(2, "el 429 no se reintenta ni bloquea al resto");
    }

    [Fact]
    public async Task ConNexoCaidoLaLecturaEs503YNoSeReintenta()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await LeerAsync(sesion, Formulario());

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.ServiceUnavailable))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(1, "leer-estructura puede gastar IA: nunca se reintenta");
    }

    [Fact]
    public async Task SinSesionEs401()
    {
        using var sugerir = await _api.EnviarAsync(HttpMethod.Post, RutaSugerir, token: null, Hoja());
        using var leer = await _api.EnviarContenidoAsync(HttpMethod.Post, RutaLeer, token: null, Formulario());

        sugerir.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        leer.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _nexo.Peticiones.Should().BeEmpty();
    }

    /// <summary>Guarda en el Nexo falso la plantilla del cliente para ORIGEN, DESTINO y TON (en Cotifly ese endpoint llega con #256).</summary>
    private async Task<Guid> GuardarPlantillaEnNexoAsync()
    {
        using var cliente = new HttpClient(_nexo, disposeHandler: false);
        using var peticion = new HttpRequestMessage(HttpMethod.Put, clsNexoFalso.UrlBase + "/api/captura-solicitudes/plantillas-mapeo")
        {
            Content = new StringContent(
                """{"clienteNit":"900555111","nombre":"Pliego","encabezados":["ORIGEN","DESTINO","TON"],"columnas":[{"indice":0,"campo":"origen"},{"indice":1,"campo":"destino"},{"indice":2,"campo":"peso","transformacion":"peso_toneladas_a_kg"}]}""",
                Encoding.UTF8, "application/json"),
        };
        peticion.Headers.Add(clsPeticionNexo.EncabezadoIdentidad, "1");
        using var respuesta = await cliente.SendAsync(peticion, TestContext.Current.CancellationToken);
        respuesta.StatusCode.Should().Be(HttpStatusCode.Created);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.GetProperty("id").GetGuid();
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
