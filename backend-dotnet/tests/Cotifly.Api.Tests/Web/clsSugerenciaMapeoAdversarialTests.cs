using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Validador de la #253 (HU-044.2): pruebas adversariales de <c>POST /web/licitaciones/sugerir-mapeo</c> y
/// <c>POST /web/licitaciones/leer-estructura</c> contra la API real y el Nexo falso: límites (100 encabezados, 200 filas, tamaño exacto del
/// archivo), entradas mal formadas (encabezados vacíos, duplicados o enormes, NIT inválido, extensiones engañosas, receta binaria o enorme,
/// partes repetidas), roles, el 429 por comercial, respuestas raras de NEXO, la forma snake_case con la receta intacta y que ningún
/// mensaje de NEXO fuera de la lista llegue al comercial.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed partial class clsSugerenciaMapeoAdversarialTests : IDisposable
{
    private const string RutaSugerir = "/web/licitaciones/sugerir-mapeo";
    private const string RutaLeer = "/web/licitaciones/leer-estructura";
    private const string RutaNexoSugerir = "/api/captura-solicitudes/sugerir-mapeo";
    private const string RutaNexoLeer = "/api/captura-solicitudes/leer-estructura";
    private const string Nit = "900.555.111-2";

    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsSugerenciaMapeoAdversarialTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex SnakeCase();

    private Task<clsSesionPrueba> ComercialAsync() => _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

    private Task<HttpResponseMessage> SugerirAsync(clsSesionPrueba sesion, object cuerpo) =>
        _api.EnviarAsync(HttpMethod.Post, RutaSugerir, sesion.Access, cuerpo as string ?? JsonSerializer.Serialize(cuerpo));

    private Task<HttpResponseMessage> LeerAsync(clsSesionPrueba sesion, HttpContent contenido) =>
        _api.EnviarContenidoAsync(HttpMethod.Post, RutaLeer, sesion.Access, contenido);

    private static MultipartFormDataContent Formulario(
        byte[]? archivo = null, string nombre = "pliego.xlsx", (string Campo, string Valor)[]? campos = null)
    {
        var formulario = new MultipartFormDataContent { { new ByteArrayContent(archivo ?? [0x50, 0x4B, 3, 4]), "archivo", nombre } };
        foreach (var (campo, valor) in campos ?? [])
        {
            formulario.Add(new StringContent(valor, Encoding.UTF8), campo);
        }

        return formulario;
    }

    private static string[] Encabezados(int cuantos) => [.. Enumerable.Range(0, cuantos).Select(i => i switch { 0 => "ORIGEN", 1 => "DESTINO", _ => $"C{i}" })];

    private JsonElement CuerpoEnviadoANexo()
    {
        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Last(p => p.Url.AbsolutePath == RutaNexoSugerir).Cuerpo!);
        return cuerpo.RootElement.Clone();
    }

    // ------------------------------------------------------------------ sugerir-mapeo: límites

    [Fact]
    public async Task ConExactamenteCienEncabezadosSiLlamaANexoYConCientoUnoNo()
    {
        var sesion = await ComercialAsync();

        using var cien = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(100), filas = Array.Empty<string[]>() });
        var llamadasTrasCien = _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir);
        using var cientoUno = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(101) });

        var json = await VerificarContratoAsync(cien, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        json.GetProperty("ia_usada").GetBoolean().Should().BeTrue("100 es el máximo que admite NEXO");
        json.GetProperty("columnas").GetArrayLength().Should().Be(100);
        llamadasTrasCien.Should().Be(1);
        (await VerificarContratoAsync(cientoUno, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK))
            .GetProperty("motivo_sin_ia").GetString().Should().Be("DEMASIADAS_COLUMNAS");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(1, "con 101 no se llama a NEXO");
    }

    [Fact]
    public async Task ConMasDeCienColumnasLaRespuestaConservaElOrdenLosIndicesYLosEncabezadosRaros()
    {
        var sesion = await ComercialAsync();
        var encabezados = Encabezados(150).Select(e => (string?)e).ToArray();
        encabezados[3] = null;
        encabezados[4] = "ORIGEN";
        encabezados[5] = "  ";

        using var respuesta = await SugerirAsync(sesion, new { encabezados });

        var columnas = (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("columnas");
        columnas.GetArrayLength().Should().Be(150);
        columnas.EnumerateArray().Select(c => c.GetProperty("indice").GetInt32()).Should().Equal(Enumerable.Range(0, 150));
        (columnas[3].GetProperty("columna").GetString(), columnas[4].GetProperty("columna").GetString(), columnas[5].GetProperty("columna").GetString())
            .Should().Be((string.Empty, "ORIGEN", "  "));
        columnas.EnumerateArray().Should().OnlyContain(c =>
            c.GetProperty("campo").GetString() == "ignorar" && c.GetProperty("origen").GetString() == "ninguno"
            && c.GetProperty("confianza").GetDouble() == 0 && c.GetProperty("nota").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ConMasDeDoscientasFilasNexoRecibeSoloLasDoscientasPrimerasEnOrden()
    {
        var sesion = await ComercialAsync();
        var filas = Enumerable.Range(0, 250).Select(i => new[] { $"origen {i}", $"destino {i}" }).ToArray();

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(2), filas });

        await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        var enviadas = CuerpoEnviadoANexo().GetProperty("filas");
        enviadas.GetArrayLength().Should().Be(clsSugerenciaMapeoService.MaximoFilasMuestra);
        enviadas[0][0].GetString().Should().Be("origen 0");
        enviadas[199][1].GetString().Should().Be("destino 199");
    }

    [Fact]
    public async Task EncabezadosVaciosDuplicadosYEnormesLleganANexoTalCualYEnSuOrden()
    {
        var sesion = await ComercialAsync();
        var enorme = new string('Ñ', 20_000);
        string?[] encabezados = ["ORIGEN", "ORIGEN", string.Empty, null, enorme, "DESTINO"];
        string?[][] filas = [["a", null, "c"], [], ["1", "2", "3", "4", "5", "6", "7 (más celdas que encabezados)"]];

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados, filas });

        var columnas = (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("columnas");
        columnas.EnumerateArray().Select(c => c.GetProperty("columna").GetString())
            .Should().Equal("ORIGEN", "ORIGEN", string.Empty, string.Empty, enorme, "DESTINO");
        var enviado = CuerpoEnviadoANexo();
        enviado.GetProperty("encabezados").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("ORIGEN", "ORIGEN", string.Empty, string.Empty, enorme, "DESTINO");
        enviado.GetProperty("filas")[0].EnumerateArray().Select(c => c.GetString()).Should().Equal("a", string.Empty, "c");
        enviado.GetProperty("filas")[1].GetArrayLength().Should().Be(0);
        enviado.GetProperty("filas")[2].GetArrayLength().Should().Be(7);
    }

    [Fact]
    public async Task SoloEncabezadosNulosNoSonEncabezadosRequeridosParaCotiflyYDecideNexo()
    {
        // Un null viaja como "": [null] tiene un encabezado (vacío) y llega a NEXO. Documenta el comportamiento.
        var sesion = await ComercialAsync();

        using var respuesta = await SugerirAsync(sesion, """{"encabezados":[null],"filas":null}""");

        await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(1);
        CuerpoEnviadoANexo().GetProperty("filas").GetArrayLength().Should().Be(0, "filas null viaja como lista vacía");
    }

    [Theory]
    [InlineData("""{"nit_cliente":"900555111"}""")]
    [InlineData("""{"encabezados":null}""")]
    public async Task SinEncabezadosOConEncabezadosNullEsEncabezadosRequeridosSinLlamarANexo(string cuerpo)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await SugerirAsync(sesion, cuerpo);

        (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ENCABEZADOS_REQUERIDOS");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(0);
    }

    [Theory]
    [InlineData("""{"encabezados":[1,2]}""")]
    [InlineData("""{"encabezados":["A"],"filas":[["x", {"y":1}]]}""")]
    [InlineData("""{"encabezados":"ORIGEN"}""")]
    [InlineData("""[]""")]
    [InlineData("""{"encabezados":["A"]""")]
    public async Task UnCuerpoMalFormadoEsErrorWebSinLlamarANexo(string cuerpo)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await SugerirAsync(sesion, cuerpo);

        (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "ErrorWeb");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == RutaNexoSugerir);
    }

    // ------------------------------------------------------------------ sugerir-mapeo: NIT

    [Theory]
    [InlineData("---")]
    [InlineData(".-.")]
    [InlineData("1234567890123456789012345678901")]
    public async Task UnNitInvalidoEsClienteNitInvalidoSinLlamarANexo(string nit)
    {
        var sesion = await ComercialAsync();

        using var sugerir = await SugerirAsync(sesion, new { nit_cliente = nit, encabezados = Encabezados(3) });
        using var leer = await LeerAsync(sesion, Formulario(campos: [("nit_cliente", nit)]));

        (await VerificarContratoAsync(sugerir, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_NIT_INVALIDO");
        (await VerificarContratoAsync(leer, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_NIT_INVALIDO");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == RutaNexoSugerir || p.Url.AbsolutePath == RutaNexoLeer);
    }

    [Fact]
    public async Task UnNitEnBlancoEsSinClienteYNoUnError()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = "   ", encabezados = Encabezados(3) });

        await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        CuerpoEnviadoANexo().TryGetProperty("clienteNit", out var nit).Should().BeFalse(nit.ToString());
    }

    [Fact]
    public async Task ConMasDeCienColumnasYNitInvalidoGanaElNitInvalido()
    {
        // O1 (val253): el NIT se valida antes que el límite de columnas: un NIT inválido no se esconde detrás de la hoja sin propuesta.
        var sesion = await ComercialAsync();

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = "---", encabezados = Encabezados(101) });

        (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CLIENTE_NIT_INVALIDO");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(0);
    }

    // ------------------------------------------------------------------ roles y tope de IA

    [Theory]
    [InlineData(clsBaseDatosPruebas.RolExternoGerente)]
    [InlineData(clsBaseDatosPruebas.RolExternoDirector)]
    public async Task ElGerenteYElDirectorTambienUsanLaImportacionConSuPropiaIdentidad(int rol)
    {
        var sesion = await _api.IniciarSesionAsync(rol, $"Reg-{Guid.NewGuid():N}"[..14]);

        using var sugerir = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(3) });
        using var leer = await LeerAsync(sesion, Formulario());

        await VerificarContratoAsync(sugerir, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        await VerificarContratoAsync(leer, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.Peticiones.Where(p => p.Url.AbsolutePath is RutaNexoSugerir or RutaNexoLeer)
            .Should().HaveCount(2).And.OnlyContain(p => !string.IsNullOrEmpty(p.IdPortalUsuario));
    }

    [Fact]
    public async Task UnTokenInvalidoEs401SinLlamarANexo()
    {
        using var sugerir = await _api.EnviarAsync(HttpMethod.Post, RutaSugerir, "no.es.un.jwt", """{"encabezados":["A"]}""");
        using var leer = await _api.EnviarContenidoAsync(HttpMethod.Post, RutaLeer, "no.es.un.jwt", Formulario());

        (sugerir.StatusCode, leer.StatusCode).Should().Be((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized));
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ElTopeDiarioDeUnComercialEnLaSugerenciaNoBloqueaAOtroNiLoBloqueaASiMismoLocalmente()
    {
        var ana = await ComercialAsync();
        var luis = await ComercialAsync();
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "IA_TOPE_DIARIO";

        using var agotada = await SugerirAsync(ana, new { nit_cliente = Nit, encabezados = Encabezados(3) });
        _nexo.ImportacionFalla = null; // NEXO limita a ana, no a luis
        using var otro = await SugerirAsync(luis, new { nit_cliente = Nit, encabezados = Encabezados(3) });
        using var anaOtraVez = await SugerirAsync(ana, new { nit_cliente = Nit, encabezados = Encabezados(3) });

        (await VerificarContratoAsync(agotada, RutaSugerir, HttpMethod.Post, HttpStatusCode.TooManyRequests))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("IA_TOPE_DIARIO");
        agotada.Headers.RetryAfter.Should().BeNull();
        (await VerificarContratoAsync(otro, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("ia_usada").GetBoolean().Should().BeTrue();
        await VerificarContratoAsync(anaOtraVez, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(3, "Cotifly no guarda el tope: lo decide NEXO en cada llamada");
        var identidades = _nexo.Peticiones.Where(p => p.Url.AbsolutePath == RutaNexoSugerir).Select(p => p.IdPortalUsuario).ToList();
        identidades[0].Should().Be(identidades[2]).And.NotBe(identidades[1], "cada comercial viaja con su identidad");
    }

    [Fact]
    public async Task ElCupoPorMinutoDeNexoEnLaSugerenciaEsHojaSinPropuestaYNoBloqueaAlSiguiente()
    {
        var ana = await ComercialAsync();
        var luis = await ComercialAsync();
        _nexo.ImportacionFalla = HttpStatusCode.TooManyRequests;
        _nexo.CodigoImportacionFalla = "DEMASIADAS_SOLICITUDES";

        using var limitada = await SugerirAsync(ana, new { nit_cliente = Nit, encabezados = Encabezados(3) });
        _nexo.ImportacionFalla = null;
        using var siguiente = await SugerirAsync(luis, new { nit_cliente = Nit, encabezados = Encabezados(3) });

        (await VerificarContratoAsync(limitada, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK))
            .GetProperty("motivo_sin_ia").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        (await VerificarContratoAsync(siguiente, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK)).GetProperty("ia_usada").GetBoolean().Should().BeTrue();
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(2);
    }

    [Fact]
    public async Task SugerenciasEnParaleloDeVariosComercialesNoSeMezclan()
    {
        var sesiones = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ComercialAsync()));

        var respuestas = await Task.WhenAll(sesiones.SelectMany((s, i) => Enumerable.Range(0, 3).Select(async j =>
        {
            using var r = await SugerirAsync(s, new { encabezados = new[] { "ORIGEN", $"U{i}-{j}" } });
            return (Esperado: $"U{i}-{j}", Json: await VerificarContratoAsync(r, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK));
        })));

        respuestas.Should().OnlyContain(r => r.Json.GetProperty("columnas")[1].GetProperty("columna").GetString() == r.Esperado);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(12);
    }

    // ------------------------------------------------------------------ sugerir-mapeo: códigos y respuestas raras de NEXO

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION_ERROR")]
    [InlineData(HttpStatusCode.BadRequest, "CODIGO_QUE_COTIFLY_NO_CONOCE")]
    [InlineData(HttpStatusCode.Conflict, "ENCABEZADOS_REQUERIDOS")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "DEMASIADAS_COLUMNAS")]
    public async Task EnLaSugerenciaUnRechazoFueraDeLaListaEsCosteoNexoRechazadaSinElMensajeDeNexo(HttpStatusCode estado, string codigo)
    {
        var sesion = await ComercialAsync();
        _nexo.ImportacionFalla = estado;
        _nexo.CodigoImportacionFalla = codigo;
        _nexo.MensajeImportacionFalla = "El NIT 900555111 de ACME S.A.S. no ... (dato interno de NEXO)";

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(3) });

        var error = (await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.BadRequest)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("COSTEO_NEXO_RECHAZADA", "VALIDATION_ERROR solo pasa en leer-estructura");
        (await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("ACME").And.NotContain("dato interno");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(1);
    }

    [Theory]
    [InlineData("no soy json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"iaUsada":true,"columnas":[null],"dudas":[]}""")]
    [InlineData("""{"iaUsada":true,"columnas":[],"dudas":[null]}""")]
    [InlineData("""{"iaUsada":"si","columnas":[]}""")]
    [InlineData("""[1,2,3]""")]
    public async Task UnaRespuestaRaraDeNexoEnLaSugerenciaEsLaHojaSinPropuesta(string crudo)
    {
        var sesion = await ComercialAsync();
        _nexo.CuerpoImportacionCrudo = crudo;

        using var respuesta = await SugerirAsync(sesion, new { nit_cliente = Nit, encabezados = Encabezados(3) });

        var json = await VerificarContratoAsync(respuesta, RutaSugerir, HttpMethod.Post, HttpStatusCode.OK);
        (json.GetProperty("ia_usada").GetBoolean(), json.GetProperty("motivo_sin_ia").GetString()).Should().Be((false, "NEXO_NO_DISPONIBLE"));
        json.GetProperty("columnas").GetArrayLength().Should().Be(3);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoSugerir).Should().Be(1, "una respuesta rara no se reintenta");
    }

    // ------------------------------------------------------------------ leer-estructura: archivo

    [Fact]
    public async Task ElTamanoExactoDelLimitePasaANexoYUnByteMasSeRechazaSinLlamarla()
    {
        var sesion = await ComercialAsync();

        using var justo = await LeerAsync(sesion, Formulario(new byte[clsArchivoExcel.TamanoMaximoBytes]));
        var llamadasTrasJusto = _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer);
        using var unoMas = await LeerAsync(sesion, Formulario(new byte[clsArchivoExcel.TamanoMaximoBytes + 1]));
        using var unoMenos = await LeerAsync(sesion, Formulario(new byte[clsArchivoExcel.TamanoMaximoBytes - 1]));

        await VerificarContratoAsync(justo, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        llamadasTrasJusto.Should().Be(1);
        (await VerificarContratoAsync(unoMas, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ARCHIVO_DEMASIADO_GRANDE");
        await VerificarContratoAsync(unoMenos, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(2, "el de un byte de más no llega a NEXO");
    }

    [Fact]
    public async Task UnCuerpoQuePasaElLimiteDeLaApiEs413SinLlamarANexo()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await LeerAsync(sesion, Formulario(new byte[(12 * 1024 * 1024) + 1]));

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.RequestEntityTooLarge))
            .GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("pliego.xlsx.exe")]
    [InlineData("pliego.exe")]
    [InlineData("pliego")]
    [InlineData("pliego.")]
    [InlineData("pliego.xls")]
    [InlineData("pliego.xlsx.")]
    [InlineData("pliego.xlsb")]
    [InlineData("pliego.xltx")]
    public async Task UnaExtensionEnganosaEsFormatoNoSoportadoSinLlamarANexo(string nombre)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await LeerAsync(sesion, Formulario(nombre: nombre));

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("FORMATO_NO_SOPORTADO");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(0);
    }

    [Theory]
    [InlineData("PLIEGO.XLSX", "PLIEGO.XLSX")]
    [InlineData("Pliego.XlSm", "Pliego.XlSm")]
    [InlineData("carpeta/sub/pliego.xlsx", "pliego.xlsx")]
    [InlineData("../../etc/pliego.xlsx", "pliego.xlsx")]
    [InlineData("pliego.exe.xlsx", "pliego.exe.xlsx")]
    [InlineData("Licitación Ñandú 2026.xlsx", "Licitación Ñandú 2026.xlsx")]
    public async Task UnaExtensionValidaEnMayusculasORutaLlegaANexoConElNombreSinRuta(string nombre, string llegaANexo)
    {
        var sesion = await ComercialAsync();

        using var respuesta = await LeerAsync(sesion, Formulario(nombre: nombre));

        await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.UltimoFormularioImportacion!["archivo"].Should().Be(llegaANexo);
    }

    [Fact]
    public async Task UnArchivoMandadoComoCampoDeTextoOSinMultipartEsArchivoRequerido()
    {
        var sesion = await ComercialAsync();

        using var comoTexto = await LeerAsync(sesion, new MultipartFormDataContent { { new StringContent("PK.."), "archivo" } });
        using var otroNombre = await LeerAsync(sesion, new MultipartFormDataContent { { new ByteArrayContent([1, 2, 3]), "excel", "pliego.xlsx" } });
        using var json = await _api.EnviarAsync(HttpMethod.Post, RutaLeer, sesion.Access, """{"archivo":"UEsDBA=="}""");

        foreach (var respuesta in new[] { comoTexto, otroNombre, json })
        {
            (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
                .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ARCHIVO_REQUERIDO");
        }

        _nexo.Peticiones.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ leer-estructura: receta e instrucciones

    [Fact]
    public async Task UnaRecetaBinariaLlegaANexoTalCualYSuRechazoVuelveConSuCodigo()
    {
        var sesion = await ComercialAsync();
        var binaria = "\u0001\u0002ÿ{\"a\":\u0000}�";

        using var respuesta = await LeerAsync(sesion, Formulario(campos: [("receta", binaria)]));

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("RECETA_INVALIDA");
        _nexo.UltimoFormularioImportacion!["receta"].Should().Be(binaria);
    }

    [Fact]
    public async Task UnaRecetaEInstruccionesLargasLleganANexoSinRecortarNiValidarEnCotifly()
    {
        // La issue: ≤ 60.000 y ≤ 4000 los valida NEXO. Cotifly reenvía de más (NEXO decide) y sin tocar los caracteres.
        var sesion = await ComercialAsync();
        var receta = $$"""{"version":1,"notas":"{{new string('á', 70_000)}}"}""";
        var instrucciones = "  línea 1\r\n\tlínea 2 — “comillas” 🚚 " + new string('x', 5_000) + "  ";

        using var respuesta = await LeerAsync(sesion, Formulario(campos: [("receta", receta), ("instrucciones", instrucciones)]));

        await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.UltimoFormularioImportacion!["receta"].Should().Be(receta);
        _nexo.UltimoFormularioImportacion!["instrucciones"].Should().Be(instrucciones);
    }

    [Fact]
    public async Task UnaRecetaEnormeLlegaANexoSinTopeEnCotiflyYVuelveSuRechazo()
    {
        // Observación O2 (val253): Cotifly no pone tope a la receta (la issue lo delega en NEXO, máx. 60.000) ni el formulario la corta:
        // una receta de más de 4 MB viaja entera a NEXO. Con un archivo cerca del límite, el cuerpo hacia NEXO pasa de 10 MB y el
        // comercial recibiría ARCHIVO_DEMASIADO_GRANDE (el 413 de NEXO), un mensaje sobre el archivo cuando sobra la receta.
        var sesion = await ComercialAsync();
        var receta = new string('x', (4 * 1024 * 1024) + 1);

        using var respuesta = await LeerAsync(sesion, Formulario(campos: [("receta", receta)]));

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("RECETA_INVALIDA");
        _nexo.UltimoFormularioImportacion!["receta"].Length.Should().Be(receta.Length);
    }

    [Fact]
    public async Task CamposVaciosNoViajanANexo()
    {
        var sesion = await ComercialAsync();

        using var respuesta = await LeerAsync(sesion, Formulario(campos: [("receta", string.Empty), ("instrucciones", string.Empty), ("nit_cliente", string.Empty)]));

        await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        _nexo.UltimoFormularioImportacion!.Should().NotContainKeys("receta", "instrucciones", "clienteNit");
    }

    [Theory]
    [InlineData("nit_cliente")]
    [InlineData("receta")]
    [InlineData("instrucciones")]
    public async Task UnCampoRepetidoSeRechazaSinLlamarANexo(string campo)
    {
        // D1 (val253): dos partes con el mismo nombre se unían con coma (dos NIT formaban un tercero). Como #256: 400 ErrorWeb, sin adivinar.
        var sesion = await ComercialAsync();

        using var respuesta = await LeerAsync(sesion, Formulario(campos: [(campo, "900111222"), (campo, "800333444")]));

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetString().Should().Be($"Formulario inválido: la parte '{campo}' viene más de una vez.");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(0);
    }

    [Fact]
    public async Task DosArchivosSeRechazanSinLlamarANexo()
    {
        var sesion = await ComercialAsync();
        var formulario = Formulario();
        formulario.Add(new ByteArrayContent([0x50, 0x4B, 3, 4]), "archivo", "otro.xlsx");

        using var respuesta = await LeerAsync(sesion, formulario);

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetString().Should().Be("Formulario inválido: la parte 'archivo' viene más de una vez.");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(0);
    }

    // ------------------------------------------------------------------ leer-estructura: códigos y respuestas de NEXO

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION_ERROR", HttpStatusCode.BadRequest, "VALIDATION_ERROR")]
    [InlineData(HttpStatusCode.NotFound, "VALIDATION_ERROR", HttpStatusCode.BadRequest, "VALIDATION_ERROR")]
    [InlineData(HttpStatusCode.BadRequest, "CODIGO_QUE_COTIFLY_NO_CONOCE", HttpStatusCode.BadRequest, "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.Conflict, "VALIDATION_ERROR", HttpStatusCode.BadRequest, "COSTEO_NEXO_RECHAZADA")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE", HttpStatusCode.BadRequest, "ARCHIVO_DEMASIADO_GRANDE")]
    [InlineData(HttpStatusCode.Unauthorized, "NO_AUTENTICADO", HttpStatusCode.Forbidden, "NEXO_SIN_PERMISO")]
    [InlineData(HttpStatusCode.TooManyRequests, "DEMASIADAS_SOLICITUDES", HttpStatusCode.ServiceUnavailable, "NEXO_NO_DISPONIBLE")]
    [InlineData(HttpStatusCode.InternalServerError, "ERROR", HttpStatusCode.ServiceUnavailable, "NEXO_NO_DISPONIBLE")]
    public async Task LosRechazosDeNexoEnLaLecturaSeTraducenSegunElContrato(
        HttpStatusCode estadoNexo, string codigoNexo, HttpStatusCode esperado, string codigoEsperado)
    {
        var sesion = await ComercialAsync();
        _nexo.ImportacionFalla = estadoNexo;
        _nexo.CodigoImportacionFalla = codigoNexo;

        using var respuesta = await LeerAsync(sesion, Formulario());

        var error = (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, esperado)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be(codigoEsperado);
        if (codigoEsperado != codigoNexo)
        {
            error.GetProperty("mensaje").GetString().Should().NotBe(_nexo.MensajeImportacionFalla, "solo los códigos de la lista traen el mensaje de NEXO");
        }

        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(1, "leer-estructura nunca se reintenta");
    }

    [Fact]
    public async Task LaLecturaPasaAPrimerNivelSnakeCaseYDejaLaRecetaIntactaConCambiosYAvisosRaros()
    {
        var sesion = await ComercialAsync();
        const string Receta = """{"version":2,"FilasEncabezado":[3,4],"hojas":[{"hoja":"Hoja 1","unirHojas":true,"snake_ya":{"MiClave":null,"x-y":[1,{"aB":"c"}]}}],"notas":"ñ \"comillas\""}""";
        var guardada = Guid.NewGuid();
        _nexo.CuerpoImportacionCrudo = $$"""
            {"archivo":"desde_nexo.xlsx","origenReceta":"guardada","similitud":0.87,"recetaGuardadaId":"{{guardada}}","iaUsada":false,
             "motivoSinIa":"RECETA_GUARDADA","receta":{{Receta}},"resumen":null,"dudas":[],
             "hojasDelArchivo":[{"nombre":"Hoja 1","oculta":true,"filas":5,"columnas":2,"usada":false}],
             "tablas":[{"nombre":"Hoja 1","filaEncabezado":4,"encabezados":["A",null],"filas":[["1",null],null],"totalFilas":null}],
             "avisos":[{"mensaje":"Hoja oculta"},"texto",null,{"otro":1}],
             "cambios":{"faltan":["TON"],"nuevos":["PESO KG"]},
             "campoNuevoDeNexo":"se ignora"}
            """;

        using var respuesta = await LeerAsync(sesion, Formulario());

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        json.EnumerateObject().Select(p => p.Name).Should().OnlyContain(n => SnakeCase().IsMatch(n));
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(json.GetProperty("receta").GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(Receta))
            .Should().BeTrue("la receta es opaca en todos sus niveles (mismas claves y valores; el escape de los caracteres puede variar)");
        (json.GetProperty("archivo").GetString(), json.GetProperty("origen_receta").GetString(), json.GetProperty("similitud").GetDouble())
            .Should().Be(("desde_nexo.xlsx", "guardada", 0.87));
        json.GetProperty("receta_guardada_id").GetGuid().Should().Be(guardada);
        json.GetProperty("hojas_del_archivo")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("nombre", "oculta", "filas", "columnas", "usada");
        var tabla = json.GetProperty("tablas")[0];
        tabla.EnumerateObject().Select(p => p.Name).Should().OnlyContain(n => SnakeCase().IsMatch(n));
        tabla.GetProperty("encabezados").EnumerateArray().Select(e => e.GetString()).Should().Equal("A", string.Empty);
        tabla.GetProperty("total_filas").GetInt32().Should().Be(2, "sin totalFilas de NEXO cuenta las filas");
        json.GetProperty("avisos").EnumerateArray().Select(a => a.GetString()).Should().Equal("Hoja oculta", "texto", """{"otro":1}""");
        json.GetProperty("cambios").GetProperty("faltan").EnumerateArray().Select(a => a.GetString()).Should().Equal("TON");
        json.GetProperty("cambios").GetProperty("nuevos").EnumerateArray().Select(a => a.GetString()).Should().Equal("PESO KG");
        json.TryGetProperty("campo_nuevo_de_nexo", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"origenReceta":"ia","iaUsada":true,"tablas":[null]}""")]
    [InlineData("""{"origenReceta":"ia","iaUsada":true,"dudas":[null]}""")]
    [InlineData("no soy json")]
    [InlineData("null")]
    [InlineData("""{"origenReceta":"ia","iaUsada":"si"}""")]
    public async Task UnaRespuestaRaraDeNexoEnLaLecturaEs503SinReintento(string crudo)
    {
        var sesion = await ComercialAsync();
        _nexo.CuerpoImportacionCrudo = crudo;

        using var respuesta = await LeerAsync(sesion, Formulario());

        (await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.ServiceUnavailable))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        _nexo.Llamadas(HttpMethod.Post, RutaNexoLeer).Should().Be(1);
    }

    [Theory]
    [InlineData("""{"origenReceta":"ia","iaUsada":true,"receta":null}""", JsonValueKind.Null)]
    [InlineData("""{"origenReceta":"ia","iaUsada":true}""", JsonValueKind.Null)]
    public async Task UnaLecturaSinRecetaDevuelveRecetaNullYListasVacias(string crudo, JsonValueKind receta)
    {
        var sesion = await ComercialAsync();
        _nexo.CuerpoImportacionCrudo = crudo;

        using var respuesta = await LeerAsync(sesion, Formulario(nombre: "original.xlsx"));

        var json = await VerificarContratoAsync(respuesta, RutaLeer, HttpMethod.Post, HttpStatusCode.OK);
        json.GetProperty("receta").ValueKind.Should().Be(receta);
        json.GetProperty("archivo").GetString().Should().Be("original.xlsx", "sin archivo de NEXO, el nombre original");
        (json.GetProperty("tablas").GetArrayLength(), json.GetProperty("dudas").GetArrayLength(), json.GetProperty("avisos").GetArrayLength())
            .Should().Be((0, 0, 0));
        json.GetProperty("cambios").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task UnaRecetaDeNexoQueNoEsObjetoIncumpleElContrato()
    {
        // La receta es opaca: si NEXO la mandara como texto o lista, Cotifly la reenvía igual y la respuesta deja de cumplir openapi
        // (receta: object). Documenta que Cotifly no la "arregla".
        var sesion = await ComercialAsync();
        _nexo.CuerpoImportacionCrudo = """{"origenReceta":"ia","iaUsada":true,"receta":[1,2]}""";

        using var respuesta = await LeerAsync(sesion, Formulario());

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("receta").GetRawText().Should().Be("[1,2]");
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
