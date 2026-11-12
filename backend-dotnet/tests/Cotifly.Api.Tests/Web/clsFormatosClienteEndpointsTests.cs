using System.Net;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-044.5 (#256) por HTTP contra la API real y el Nexo falso: plantillas de mapeo (<c>PUT/GET/DELETE /web/licitaciones/plantillas-mapeo</c>),
/// recetas de lectura (<c>PUT/GET/DELETE /web/licitaciones/recetas-lectura</c>) y la consulta del diccionario del cliente
/// (<c>GET /web/diccionario-cliente</c> y <c>/opciones</c>). Los listados sin NIT responden 400 sin llamar a NEXO; el PUT de la receta no
/// se reintenta; "no encontrado" es 404 con el código de NEXO; el diccionario trae los términos del cliente (con NIT) y los generales.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsFormatosClienteEndpointsTests : IDisposable
{
    private const string RutaPlantillas = "/web/licitaciones/plantillas-mapeo";
    private const string RutaRecetas = "/web/licitaciones/recetas-lectura";
    private const string RutaDiccionario = "/web/diccionario-cliente";
    private const string RutaOpciones = "/web/diccionario-cliente/opciones";
    private const string RutaNexoPlantillas = "/api/captura-solicitudes/plantillas-mapeo";
    private const string RutaNexoRecetas = "/api/captura-solicitudes/recetas-lectura";
    private const string Nit = "900.555.111-2";
    private const string RecetaExacta = """{ "version" : 1, "hojas" : [ { "hoja" : "*", "usar" : true } ], "unir" : false, "notas" : null }""";

    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;

    public clsFormatosClienteEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static string Plantilla(string? nit = Nit, string? nombre = "Pliego EPM") => JsonSerializer.Serialize(new
    {
        nit_cliente = nit,
        nombre,
        encabezados = new[] { "ORIGEN", "DESTINO", "TON" },
        columnas = new object[]
        {
            new { indice = 0, campo = "origen" },
            new { indice = 1, campo = "destino" },
            new { indice = 2, campo = "peso", transformacion = "peso_toneladas_a_kg" },
        },
    });

    private static MultipartFormDataContent FormularioReceta(
        byte[]? archivo = null, string nombreArchivo = "EPM_formulario.xlsx", string? receta = RecetaExacta, string? nit = Nit,
        string? nombre = "Formulario oferta", string? instrucciones = null)
    {
        var formulario = new MultipartFormDataContent();
        if (archivo is not { Length: 0 })
        {
            formulario.Add(new ByteArrayContent(archivo ?? [0x50, 0x4B, 3, 4]), "archivo", nombreArchivo);
        }

        foreach (var (campo, valor) in new[] { ("receta", receta), ("nit_cliente", nit), ("nombre", nombre), ("instrucciones", instrucciones) })
        {
            if (valor is not null)
            {
                formulario.Add(new StringContent(valor, Encoding.UTF8), campo);
            }
        }

        return formulario;
    }

    private Task<HttpResponseMessage> GuardarRecetaAsync(clsSesionPrueba sesion, HttpContent contenido) =>
        _api.EnviarContenidoAsync(HttpMethod.Put, RutaRecetas, sesion.Access, contenido);

    // --- Plantillas de mapeo ---

    [Fact]
    public async Task GuardarLaPlantillaResponde201LaPrimeraVezY200AlReemplazarla()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var primera = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla());
        var creada = await VerificarContratoAsync(primera, RutaPlantillas, HttpMethod.Put, HttpStatusCode.Created);
        using var segunda = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla(nombre: "Otro nombre"));
        var reemplazada = await VerificarContratoAsync(segunda, RutaPlantillas, HttpMethod.Put, HttpStatusCode.OK);

        reemplazada.GetProperty("id").GetGuid().Should().Be(creada.GetProperty("id").GetGuid());
        reemplazada.GetProperty("nombre").GetString().Should().Be("Otro nombre");
        creada.GetProperty("columnas").EnumerateArray().Select(c => (c.GetProperty("indice").GetInt32(), c.GetProperty("campo").GetString(),
                c.GetProperty("transformacion").GetString()))
            .Should().Equal((0, "origen", "ninguna"), (1, "destino", "ninguna"), (2, "peso", "peso_toneladas_a_kg"));
        creada.TryGetProperty("cliente_id", out _).Should().BeFalse("Cotifly no expone el id local del cliente en NEXO");
        var enviada = _nexo.Peticiones.First(p => p.Url.AbsolutePath == RutaNexoPlantillas && p.Metodo == HttpMethod.Put);
        using var cuerpo = JsonDocument.Parse(enviada.Cuerpo!);
        cuerpo.RootElement.GetProperty("clienteNit").GetString().Should().Be("900555111", "el NIT viaja como lo compara NEXO");
        enviada.IdPortalUsuario.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GuardarLaPlantillaConUnNitQueNexoNoPuedeUsarEsClienteNitInvalido()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.NitsDesconocidos.Add("123456789");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla(nit: "123.456.789"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CLIENTE_NIT_INVALIDO");
    }

    [Fact]
    public async Task UnRechazoDeValidacionDeNexoPasaConSuCodigo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        (_nexo.ImportacionFalla, _nexo.CodigoImportacionFalla) = (HttpStatusCode.BadRequest, "CAMPO_DUPLICADO");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla());

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CAMPO_DUPLICADO");
    }

    [Fact]
    public async Task ListarLasPlantillasDelClienteDevuelveSoloLasSuyasYUnClienteSinPlantillasLaListaVacia()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        (await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla())).Dispose();
        (await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla(nit: "800111222"))).Dispose();

        using var delCliente = await _api.EnviarAsync(HttpMethod.Get, $"{RutaPlantillas}?nit_cliente={Uri.EscapeDataString(Nit)}", sesion.Access);
        using var sinPlantillas = await _api.EnviarAsync(HttpMethod.Get, $"{RutaPlantillas}?nit_cliente=811000999", sesion.Access);

        var lista = await VerificarContratoAsync(delCliente, RutaPlantillas, HttpMethod.Get, HttpStatusCode.OK);
        lista.GetArrayLength().Should().Be(1);
        (await VerificarContratoAsync(sinPlantillas, RutaPlantillas, HttpMethod.Get, HttpStatusCode.OK)).GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?nit_cliente=")]
    [InlineData("?nit_cliente=%20")]
    public async Task ListarPlantillasSinNitEs400SinLlamarANexo(string consulta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, RutaPlantillas + consulta, sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CLIENTE_REQUERIDO");
        _nexo.Llamadas(HttpMethod.Get, RutaNexoPlantillas).Should().Be(0);
    }

    [Fact]
    public async Task BorrarLaPlantillaResponde204YLaQueNoExisteEs404ConSuCodigo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var guardada = await _api.EnviarAsync(HttpMethod.Put, RutaPlantillas, sesion.Access, Plantilla());
        var id = (await VerificarContratoAsync(guardada, RutaPlantillas, HttpMethod.Put, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var borrada = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{id}", sesion.Access);
        using var otraVez = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/{id}", sesion.Access);

        borrada.StatusCode.Should().Be(HttpStatusCode.NoContent);
        otraVez.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(otraVez, "PLANTILLA_NO_ENCONTRADA");
    }

    [Fact]
    public async Task BorrarUnaPlantillaConUnIdQueNoEsGuidEs404SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaPlantillas}/no-es-un-id", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(respuesta, "PLANTILLA_NO_ENCONTRADA");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexoPlantillas, StringComparison.Ordinal));
    }

    // --- Recetas de lectura ---

    [Fact]
    public async Task GuardarLaRecetaLaPasaExactaA201YAlReemplazarlaResponde200()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var primera = await GuardarRecetaAsync(sesion, FormularioReceta(instrucciones: "Las celdas vacías no se cotizan"));
        var creada = await VerificarContratoAsync(primera, RutaRecetas, HttpMethod.Put, HttpStatusCode.Created);
        var formulario = _nexo.UltimoFormularioImportacion!;
        using var segunda = await GuardarRecetaAsync(sesion, FormularioReceta());
        var reemplazada = await VerificarContratoAsync(segunda, RutaRecetas, HttpMethod.Put, HttpStatusCode.OK);

        formulario["receta"].Should().Be(RecetaExacta, "la receta es opaca: llega a NEXO exactamente como vino");
        (formulario["archivo"], formulario["clienteNit"], formulario["nombre"], formulario["instrucciones"])
            .Should().Be(("EPM_formulario.xlsx", "900555111", "Formulario oferta", "Las celdas vacías no se cotizan"));
        reemplazada.GetProperty("id").GetGuid().Should().Be(creada.GetProperty("id").GetGuid());
        creada.GetProperty("receta").GetProperty("hojas")[0].GetProperty("hoja").GetString().Should().Be("*");
        creada.TryGetProperty("cliente_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ConNexoCaidoElPutDeLaRecetaNoSeReintentaYEs503()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await GuardarRecetaAsync(sesion, FormularioReceta());

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, "NEXO_NO_DISPONIBLE");
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(1, "el PUT de la receta puede gastar IA: nunca se reintenta");
    }

    [Fact]
    public async Task UnaRecetaSinFilasPasaConElCodigoDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        (_nexo.ImportacionFalla, _nexo.CodigoImportacionFalla) = (HttpStatusCode.BadRequest, "RECETA_SIN_FILAS");

        using var respuesta = await GuardarRecetaAsync(sesion, FormularioReceta());

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "RECETA_SIN_FILAS");
    }

    [Theory]
    [InlineData("pliego.csv", "FORMATO_NO_SOPORTADO", true, true)]
    [InlineData("pliego.xlsx", "ARCHIVO_REQUERIDO", false, true)]
    [InlineData("pliego.xlsx", "RECETA_REQUERIDA", true, false)]
    public async Task GuardarUnaRecetaInvalidaEs400SinLlamarANexo(string nombreArchivo, string codigo, bool conArchivo, bool conReceta)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await GuardarRecetaAsync(sesion,
            FormularioReceta(archivo: conArchivo ? null : [], nombreArchivo: nombreArchivo, receta: conReceta ? RecetaExacta : null));

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, codigo);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Theory]
    [InlineData("receta", RecetaExacta)]
    [InlineData("nit_cliente", "800111222")]
    [InlineData("nombre", "Otro nombre")]
    [InlineData("instrucciones", "Otra instrucción")]
    public async Task UnaParteRepetidaDelFormularioEs400SinLlamarANexo(string parte, string valor)
    {
        // #256 (validador D1/D2): dos partes con el mismo nombre se unirían con coma: una receta que deja de ser opaca o dos NIT que la
        // normalización funde en otro NIT (la receta se guardaría en otro cliente). Se rechaza el formulario completo.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = FormularioReceta(instrucciones: "Las celdas vacías no se cotizan");
        formulario.Add(new StringContent(valor, Encoding.UTF8), parte);

        using var respuesta = await GuardarRecetaAsync(sesion, formulario);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(parte);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Fact]
    public async Task DosArchivosEnElFormularioEs400SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var formulario = FormularioReceta();
        formulario.Add(new ByteArrayContent([0x50, 0x4B, 3, 4]), "archivo", "otro.xlsx");

        using var respuesta = await GuardarRecetaAsync(sesion, formulario);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _nexo.Llamadas(HttpMethod.Put, RutaNexoRecetas).Should().Be(0);
    }

    [Fact]
    public async Task ListarLasRecetasDelClienteYSinNitEs400SinLlamarANexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        (await GuardarRecetaAsync(sesion, FormularioReceta())).Dispose();

        using var delCliente = await _api.EnviarAsync(HttpMethod.Get, $"{RutaRecetas}?nit_cliente=900555111", sesion.Access);
        var lista = await VerificarContratoAsync(delCliente, RutaRecetas, HttpMethod.Get, HttpStatusCode.OK);
        using var sinCliente = await _api.EnviarAsync(HttpMethod.Get, $"{RutaRecetas}?nit_cliente=811000999", sesion.Access);
        using var sinNit = await _api.EnviarAsync(HttpMethod.Get, RutaRecetas, sesion.Access);

        lista.GetArrayLength().Should().Be(1);
        (await VerificarContratoAsync(sinCliente, RutaRecetas, HttpMethod.Get, HttpStatusCode.OK)).GetArrayLength().Should().Be(0);
        sinNit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(sinNit, "CLIENTE_REQUERIDO");
        _nexo.Llamadas(HttpMethod.Get, RutaNexoRecetas).Should().Be(2, "sin NIT no se llama a NEXO");
    }

    [Fact]
    public async Task BorrarLaRecetaResponde204YLaQueNoExisteEs404ConSuCodigo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var guardada = await GuardarRecetaAsync(sesion, FormularioReceta());
        var id = (await VerificarContratoAsync(guardada, RutaRecetas, HttpMethod.Put, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var borrada = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/{id}", sesion.Access);
        using var otraVez = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/{id}", sesion.Access);
        using var sinId = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/x", sesion.Access);

        borrada.StatusCode.Should().Be(HttpStatusCode.NoContent);
        otraVez.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(otraVez, "RECETA_NO_ENCONTRADA");
        sinId.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(sinId, "RECETA_NO_ENCONTRADA");
        _nexo.Llamadas(HttpMethod.Delete, $"{RutaNexoRecetas}/{id}").Should().Be(2);
    }

    [Fact]
    public async Task UnNotFoundDeNexoConUnCodigoDesconocidoEs400CosteoNexoRechazada()
    {
        // #256 (B3): lo que pasaría con un NEXO sin recetas-lectura (PR #433 fuera de QA): 404 sin un código de la tabla de NEXO.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        (_nexo.ImportacionFalla, _nexo.CodigoImportacionFalla) = (HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA");

        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{RutaRecetas}/{Guid.NewGuid()}", sesion.Access);
        using var listar = await _api.EnviarAsync(HttpMethod.Get, $"{RutaRecetas}?nit_cliente=900555111", sesion.Access);

        borrar.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(borrar, "COSTEO_NEXO_RECHAZADA");
        listar.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(listar, "COSTEO_NEXO_RECHAZADA");
    }

    // --- Diccionario del cliente (consulta) ---

    [Fact]
    public async Task ElDiccionarioSinNitSoloTraeLosTerminosGenerales()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Diccionario.Add((Guid.NewGuid(), "Mula de otro cliente", "valor", "3S2", "tipoVehiculo"));

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, RutaDiccionario, sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaDiccionario, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").EnumerateArray().Select(t => t.GetProperty("termino").GetString()).Should().Equal("Tractomula", "ORIGEN");
        json.GetProperty("terminos").EnumerateArray().Should().OnlyContain(t => t.GetProperty("general").GetBoolean());
        json.GetProperty("aviso").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ElDiccionarioConNitTraeLosTerminosDelClienteYLosGeneralesSinAviso()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Diccionario.Add((clsNexoFalso.ClienteIdDeNit(Nit), "Mula del cliente", "valor", "3S2", "tipoVehiculo"));
        _nexo.Diccionario.Add((Guid.NewGuid(), "Mula de otro cliente", "valor", "3S2", "tipoVehiculo"));

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?nit_cliente=900555111&tipo=valor&q=mula", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaDiccionario, HttpMethod.Get, HttpStatusCode.OK);
        var terminos = json.GetProperty("terminos").EnumerateArray()
            .Select(t => (Termino: t.GetProperty("termino").GetString(), General: t.GetProperty("general").GetBoolean())).ToList();
        terminos.Should().BeEquivalentTo([("Tractomula", true), ("ORIGEN", true), ("Mula del cliente", false)]);
        json.GetProperty("aviso").ValueKind.Should().Be(JsonValueKind.Null);
        var enviada = _nexo.Peticiones.Single(p => p.Url.AbsolutePath == "/api/diccionario-cliente");
        enviada.Url.Query.Should().Contain("clienteNit=900555111").And.Contain("tipo=valor").And.Contain("q=mula");
    }

    [Fact]
    public async Task SiNexoDejaDeMandarElClienteIdNoSeMuestraNingunTermino()
    {
        // #256 (B2): el filtro de generales falla cerrado ante un cambio de forma de NEXO.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Diccionario.Add((Guid.NewGuid(), "Mula de otro cliente", "valor", "3S2", "tipoVehiculo"));
        _nexo.DiccionarioSinClienteId = true;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, RutaDiccionario, sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaDiccionario, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UnTipoInvalidoDelDiccionarioPasaConElCodigoDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{RutaDiccionario}?tipo=otro", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "TIPO_TERMINO_INVALIDO");
    }

    [Fact]
    public async Task ConNexoCaidoElDiccionarioEs503()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.Caido = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, RutaDiccionario, sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, "NEXO_NO_DISPONIBLE");
    }

    [Fact]
    public async Task LasOpcionesDelDiccionarioCumplenElContrato()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, RutaOpciones, sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaOpciones, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("campos_destino").EnumerateArray().Select(c => c.GetString()).Should().Contain("origen");
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
