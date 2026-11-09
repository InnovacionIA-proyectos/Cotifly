using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Validación adversarial de HU-044.5 (#256) por HTTP contra la API real y el Nexo falso: borrado sin dueño, ids raros, NIT inválidos sin
/// llamar a NEXO, receta opaca byte a byte, límites y nombres engañosos del archivo, partes repetidas del multipart, inyección de
/// parámetros en el diccionario, que no salga nada del cliente en el diccionario, roles y concurrencia.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsFormatosClienteAdversarialTests : IDisposable
{
    private const string RutaPlantillas = "/web/licitaciones/plantillas-mapeo";
    private const string RutaRecetas = "/web/licitaciones/recetas-lectura";
    private const string RutaDiccionario = "/web/diccionario-cliente";
    private const string RutaNexoPlantillas = "/api/captura-solicitudes/plantillas-mapeo";
    private const string RutaNexoRecetas = "/api/captura-solicitudes/recetas-lectura";
    private const string RutaNexoDiccionario = "/api/diccionario-cliente";
    private const string NitA = "900555111";
    private const string NitB = "800111222";
    private const string Receta = """{"version":1,"hojas":[{"hoja":"*","usar":true}],"unir":false,"notas":null}""";

    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsFormatosClienteAdversarialTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Plantilla(string? nit) => JsonSerializer.Serialize(new
    {
        nit_cliente = nit,
        encabezados = new[] { "ORIGEN", "DESTINO" },
        columnas = new object[] { new { indice = 0, campo = "origen" }, new { indice = 1, campo = "destino" } },
    });

    private static MultipartFormDataContent Formulario(
        byte[]? archivo = null, string nombreArchivo = "pliego.xlsx", string? receta = Receta, string? nit = NitA)
    {
        var formulario = new MultipartFormDataContent();
        formulario.Add(new ByteArrayContent(archivo ?? [0x50, 0x4B, 3, 4]), "archivo", nombreArchivo);
        if (receta is not null)
        {
            formulario.Add(new StringContent(receta, Encoding.UTF8), "receta");
        }

        if (nit is not null)
        {
            formulario.Add(new StringContent(nit, Encoding.UTF8), "nit_cliente");
        }

        return formulario;
    }

    private Task<HttpResponseMessage> PutReceta(clsSesionPrueba sesion, HttpContent contenido) =>
        _api.EnviarContenidoAsync(HttpMethod.Put, RutaRecetas, sesion.Access, contenido);

    private int LlamadasImportacion() =>
        _nexo.Peticiones.Count(p => p.Url.AbsolutePath.StartsWith("/api/captura-solicitudes/plantillas-mapeo", StringComparison.Ordinal)
            || p.Url.AbsolutePath.StartsWith("/api/captura-solicitudes/recetas-lectura", StringComparison.Ordinal)
            || p.Url.AbsolutePath.StartsWith(RutaNexoDiccionario, StringComparison.Ordinal));

    // --- Borrado sin dueño (riesgo señalado por el desarrollador) ---

    [Fact]
    public async Task OtroComercialBorraLaPlantillaDeOtroClienteSoloConElId()
    {
        // Caracterización del riesgo: el DELETE no lleva NIT ni contexto de licitación y el listado admite cualquier NIT. Un comercial que
        // nunca trabajó con el cliente B lista sus plantillas y las borra. NEXO solo audita.
        var duenio = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var ajeno = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var guardada = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, duenio.Access, Plantilla(NitB));
        guardada.StatusCode.Should().Be(HttpStatusCode.Created);

        using var listado = await _api.EnviarAsync(HttpMethod.Get, $"{RutaPlantillas}?nit_cliente={NitB}", ajeno.Access);
        var id = (await VerificarContratoAsync(listado, RutaPlantillas, HttpMethod.Get, HttpStatusCode.OK))[0].GetProperty("id").GetGuid();
        using var borrada = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{id}", ajeno.Access);

        borrada.StatusCode.Should().Be(HttpStatusCode.NoContent, "hoy Cotifly no comprueba dueño ni cliente al borrar (riesgo R1 del informe)");
        var delete = _nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Delete);
        delete.Url.Query.Should().BeEmpty("el DELETE a NEXO no lleva el NIT: NEXO tampoco puede cruzarlo");
        delete.IdPortalUsuario.Should().NotBeNullOrEmpty("al menos NEXO audita quién borró");
    }

    [Fact]
    public async Task OtroComercialBorraLaRecetaDeOtroClienteSoloConElId()
    {
        var duenio = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var ajeno = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var guardada = await PutReceta(duenio, Formulario(nit: NitB));
        var id = (await VerificarContratoAsync(guardada, RutaRecetas, HttpMethod.Put, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var borrada = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/{id}", ajeno.Access);

        borrada.StatusCode.Should().Be(HttpStatusCode.NoContent, "hoy Cotifly no comprueba dueño ni cliente al borrar (riesgo R1 del informe)");
        _nexo.RecetasGuardadas.Should().BeEmpty();
    }

    // --- Ids que no son GUID y GUID en otros formatos ---

    [Theory]
    [InlineData("no-es-un-id")]
    [InlineData("123")]
    [InlineData("00000000-0000-0000-0000-00000000000Z")]
    [InlineData("..%2F..%2Fapi%2Fcotizaciones")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task UnIdRaroEs404ConSuCodigoYNoLlegaANexo(string id)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var plantilla = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{id}", sesion.Access);
        using var receta = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/{id}", sesion.Access);

        plantilla.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(plantilla, clsCodigosError.PlantillaNoEncontrada);
        receta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(receta, clsCodigosError.RecetaNoEncontrada);
        _nexo.Peticiones.Should().BeEmpty("un id que no es GUID no llega a NEXO");
    }

    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    public async Task UnGuidEnOtroFormatoLlegaANexoEnFormatoCanonico(string formato)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var id = Guid.NewGuid();

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{Uri.EscapeDataString(id.ToString(formato))}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(respuesta, clsCodigosError.PlantillaNoEncontrada);
        _nexo.Peticiones.Single().Url.AbsolutePath.Should().Be($"{RutaNexoPlantillas}/{id:D}");
    }

    // --- NIT: obligatorio e inválido sin llamar a NEXO ---

    [Theory]
    [InlineData(null, "CLIENTE_REQUERIDO")]
    [InlineData("   ", "CLIENTE_REQUERIDO")]
    [InlineData("---", "CLIENTE_NIT_INVALIDO")]
    [InlineData("1234567890123456789012345678901", "CLIENTE_NIT_INVALIDO")]
    public async Task GuardarLaPlantillaSinNitUtilizableEs400SinLlamarANexo(string? nit, string codigo)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla(nit));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, codigo);
        LlamadasImportacion().Should().Be(0);
    }

    [Theory]
    [InlineData(RutaPlantillas, "---")]
    [InlineData(RutaPlantillas, "1234567890123456789012345678901")]
    [InlineData(RutaRecetas, "---")]
    [InlineData(RutaRecetas, ".-")]
    public async Task ListarConUnNitInvalidoEs400SinLlamarANexo(string ruta, string nit)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{ruta}?nit_cliente={Uri.EscapeDataString(nit)}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CLIENTE_NIT_INVALIDO");
        LlamadasImportacion().Should().Be(0);
    }

    [Fact]
    public async Task UnNitConParametrosIncrustadosNoInyectaNadaEnLaConsultaANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get,
            $"{RutaRecetas}?nit_cliente={Uri.EscapeDataString("900555111&clienteNit=&todos=true")}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        var consulta = System.Web.HttpUtility.ParseQueryString(_nexo.Peticiones.Single().Url.Query);
        consulta.AllKeys.Should().Equal("clienteNit");
        consulta.GetValues("clienteNit").Should().Equal("900555111CLIENTENITTODOSTRUE");
    }

    [Fact]
    public async Task UnCuerpoQueNoEsJsonEnLaPlantillaEs400SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var roto = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, "{ \"nit_cliente\": ");
        using var nulo = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, "null");

        roto.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        nulo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        LlamadasImportacion().Should().Be(0);
    }

    // --- Receta opaca ---

    [Fact]
    public async Task LaRecetaLlegaANexoByteAByteYVuelveSinSnakeCase()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        const string rara = "{\r\n\t\"version\" :1 ,\"hojas\":[{\"hoja\":\"Pliego ñ \\u00e1 \\\"x\\\"\",\"filasEncabezado\":[3],"
            + "\"hastaPrimeraFilaVacia\":true}],\"precision\":12345678901234567890.123456789,\"Clave_Rara\":{\"subClave\":null},"
            + "\"vacia\":\"\",\"emoji\":\"\uD83D\uDE9A\"}  \n";

        using var respuesta = await PutReceta(sesion, Formulario(receta: rara));

        _nexo.UltimoFormularioImportacion!["receta"].Should().Be(rara, "la receta es opaca: ni se recorta ni se reescribe");
        var json = await VerificarContratoAsync(respuesta, RutaRecetas, HttpMethod.Put, HttpStatusCode.Created);
        var receta = json.GetProperty("receta");
        receta.GetProperty("hojas")[0].TryGetProperty("filasEncabezado", out _).Should().BeTrue("las claves de la receta no pasan a snake_case");
        receta.GetProperty("hojas")[0].TryGetProperty("hasta_primera_fila_vacia", out _).Should().BeFalse();
        receta.TryGetProperty("Clave_Rara", out var rarita).Should().BeTrue();
        rarita.TryGetProperty("subClave", out _).Should().BeTrue();
        receta.GetProperty("precision").GetRawText().Should().Be("12345678901234567890.123456789", "un número grande no se redondea");
        receta.GetProperty("emoji").GetString().Should().Be("\uD83D\uDE9A");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task UnaRecetaEnBlancoEsRecetaRequeridaSinLlamarANexo(string receta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await PutReceta(sesion, Formulario(receta: receta));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, clsCodigosError.RecetaRequerida);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Fact(DisplayName = "D1: dos partes 'receta' en el multipart se unen con coma (StringValues.ToString) y esa mezcla viaja a NEXO como si fuera la "
        + "receta: deja de ser opaca. Reproducir: PUT /web/licitaciones/recetas-lectura con dos partes 'receta' → NEXO recibe '{..},{..}'.")]
    public async Task DosPartesRecetaNoSeFusionanAntesDeLlegarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = Formulario();
        formulario.Add(new StringContent("""{"version":2}""", Encoding.UTF8), "receta");

        using var respuesta = await PutReceta(sesion, formulario);

        if (_nexo.UltimoFormularioImportacion is { } enviado)
        {
            enviado["receta"].Should().BeOneOf(Receta, """{"version":2}""");
        }
        else
        {
            respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact(DisplayName = "D2: dos partes 'nit_cliente' se unen con coma y la normalización del NIT las funde en otro NIT ('900555111800111222') "
        + "que viaja a NEXO. Reproducir: PUT /web/licitaciones/recetas-lectura con nit_cliente=900555111 y nit_cliente=800111222.")]
    public async Task DosPartesNitNoSeFundenEnOtroNit()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = Formulario(nit: NitA);
        formulario.Add(new StringContent(NitB, Encoding.UTF8), "nit_cliente");

        using var respuesta = await PutReceta(sesion, formulario);

        if (_nexo.UltimoFormularioImportacion is { } enviado)
        {
            enviado["clienteNit"].Should().BeOneOf(NitA, NitB);
        }
        else
        {
            respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    // --- Archivo: límite y nombres engañosos ---

    [Fact]
    public async Task UnArchivoJustoEnElLimiteLlegaANexoYUnByteMasNo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var enElLimite = await PutReceta(sesion, Formulario(archivo: new byte[clsArchivoExcel.TamanoMaximoBytes]));
        using var excedido = await PutReceta(sesion, Formulario(archivo: new byte[clsArchivoExcel.TamanoMaximoBytes + 1]));

        enElLimite.StatusCode.Should().Be(HttpStatusCode.Created);
        excedido.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(excedido, clsCodigosError.ArchivoDemasiadoGrande);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(1);
    }

    [Fact]
    public async Task UnCuerpoMayorQueElLimiteDeLaRutaEs413SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await PutReceta(sesion, Formulario(archivo: new byte[13 * 1024 * 1024]));

        respuesta.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Theory]
    [InlineData("pliego.xlsx.exe")]
    [InlineData("pliego.xls")]
    [InlineData("pliego.csv")]
    [InlineData("pliego")]
    [InlineData("pliego.xlsx.")]
    [InlineData("xlsx")]
    [InlineData("pliego.xlsx/")]
    public async Task UnaExtensionEnganosaEsFormatoNoSoportadoSinLlamarANexo(string nombre)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await PutReceta(sesion, Formulario(nombreArchivo: nombre));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, clsCodigosError.FormatoNoSoportado);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Theory]
    [InlineData("PLIEGO.XLSX", "PLIEGO.XLSX")]
    [InlineData("pliego.xlsm", "pliego.xlsm")]
    [InlineData("..\\..\\Windows\\pliego.xlsx", "pliego.xlsx")]
    [InlineData("../../etc/pliego.xlsx", "pliego.xlsx")]
    [InlineData("C:\\Users\\comercial\\pliego.xlsx", "pliego.xlsx")]
    [InlineData("  pliego.xlsx  ", "pliego.xlsx")]
    public async Task UnNombreConRutaOMayusculasLlegaANexoSoloConElNombre(string nombre, string esperado)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = new MultipartFormDataContent();
        var archivo = new ByteArrayContent([0x50, 0x4B, 3, 4]);
        archivo.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"archivo\"", FileName = $"\"{nombre}\"" };
        formulario.Add(archivo);
        formulario.Add(new StringContent(Receta, Encoding.UTF8), "receta");
        formulario.Add(new StringContent(NitA, Encoding.UTF8), "nit_cliente");

        using var respuesta = await PutReceta(sesion, formulario);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Created);
        var enviado = _nexo.UltimoFormularioImportacion!["archivo"];
        enviado.Should().EndWith(esperado, "a NEXO no viaja la ruta del equipo del comercial").And.NotContain("/").And.NotContain("\\");
    }

    [Fact]
    public async Task SinMultipartOConLaParteMalNombradaEsArchivoRequeridoSinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var malNombrada = new MultipartFormDataContent
        {
            { new ByteArrayContent([0x50, 0x4B, 3, 4]), "file", "pliego.xlsx" },
            { new StringContent(Receta, Encoding.UTF8), "receta" },
            { new StringContent(NitA, Encoding.UTF8), "nit_cliente" },
        };

        using var json = await _api.EnviarAsync(HttpMethod.Put, RutaRecetas, sesion.Access, """{"receta":"{}","nit_cliente":"900555111"}""");
        using var parte = await PutReceta(sesion, malNombrada);

        json.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(json, clsCodigosError.ArchivoRequerido);
        parte.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(parte, clsCodigosError.ArchivoRequerido);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Fact]
    public async Task UnMultipartMalFormadoEs400SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var roto = new StringContent("--otra\r\nContent-Disposition: form-data; name=\"receta\"\r\n\r\n{}\r\n", Encoding.UTF8);
        roto.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=frontera");

        using var respuesta = await PutReceta(sesion, roto);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    // --- Diccionario: nada del cliente sale de Cotifly ---

    [Fact]
    public async Task ConNitYNexoDevolviendoTerminosDeVariosClientesSoloSalenLosGeneralesSinDatosDeCliente()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clienteDelNit = Guid.NewGuid();
        _nexo.Diccionario.Add((clienteDelNit, "Mula del propio cliente", "valor", "3S2", "tipoVehiculo"));
        _nexo.Diccionario.Add((Guid.NewGuid(), "Patineta de otro", "valor", "2S2", "tipoVehiculo"));
        _nexo.Diccionario.Add((Guid.Empty, "Término con cliente vacío", "columna", "origen", null));

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?nit_cliente={NitA}", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaDiccionario, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").EnumerateArray().Select(t => t.GetProperty("termino").GetString())
            .Should().Equal(["Tractomula", "ORIGEN"], "ni los del propio cliente: la decisión es solo generales más aviso");
        json.GetProperty("aviso").GetProperty("codigo").GetString().Should().Be("TERMINOS_CLIENTE_NO_DISPONIBLES");
        var texto = json.GetRawText();
        texto.Should().NotContain("cliente_id").And.NotContain("clienteId").And.NotContain("cliente_nombre").And.NotContain(clienteDelNit.ToString());
    }

    [Fact]
    public async Task LosFiltrosDelDiccionarioNoInyectanParametrosEnLaConsultaANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var q = "mula&clienteId=" + Guid.NewGuid() + "&incluirGenerales=false";

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?q={Uri.EscapeDataString(q)}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        var consulta = System.Web.HttpUtility.ParseQueryString(_nexo.Peticiones.Single(p => p.Url.AbsolutePath == RutaNexoDiccionario).Url.Query);
        consulta.AllKeys.Should().BeEquivalentTo(["incluirGenerales", "q"]);
        consulta.GetValues("incluirGenerales").Should().Equal("true");
        consulta["q"].Should().Be(q);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("1234567890123456789012345678901")]
    public async Task ElDiccionarioConUnNitInvalidoEs400SinLlamarANexo(string nit)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?nit_cliente={Uri.EscapeDataString(nit)}", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CLIENTE_NIT_INVALIDO");
        LlamadasImportacion().Should().Be(0);
    }

    [Fact]
    public async Task ElDiccionarioConNitEnBlancoEsComoSinNitYSinAviso()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?nit_cliente=%20%20", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaDiccionario, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("aviso").ValueKind.Should().Be(JsonValueKind.Null);
        _nexo.Peticiones.Single().Url.Query.Should().NotContain("clienteNit");
    }

    // --- Roles ---

    [Theory]
    [InlineData(clsBaseDatosPruebas.RolExternoGerente)]
    [InlineData(clsBaseDatosPruebas.RolExternoDirector)]
    public async Task GerenteYDirectorUsanLasRutasComoElComercial(int rol)
    {
        var sesion = await _api.IniciarSesionAsync(rol, nombreCompleto: $"Rol-{rol}");

        using var plantillas = await _api.EnviarAsync(HttpMethod.Get, $"{RutaPlantillas}?nit_cliente={NitA}", sesion.Access);
        using var diccionario = await _api.EnviarAsync(HttpMethod.Get, RutaDiccionario, sesion.Access);

        plantillas.StatusCode.Should().Be(HttpStatusCode.OK, "la política Comercial incluye gerente y director, como la captura");
        diccionario.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnTokenInvalidoEs401YNoLlegaANexo()
    {
        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{Guid.NewGuid()}", "token.que.no-sirve");

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _nexo.Peticiones.Should().BeEmpty();
    }

    // --- Concurrencia ---

    [Fact]
    public async Task VariosPutDeRecetaALaVezSonUnaSolaLlamadaANexoCadaUno()
    {
        var sesiones = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial)));

        var respuestas = await Task.WhenAll(sesiones.Select((s, i) => PutReceta(s, Formulario(nit: $"90055511{i}"))));

        try
        {
            respuestas.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Created);
            _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(5, "ningún PUT de receta se reintenta");
            _nexo.RecetasGuardadas.Select(r => r.Nit).Should().OnlyHaveUniqueItems().And.HaveCount(5);
        }
        finally
        {
            foreach (var respuesta in respuestas)
            {
                respuesta.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
