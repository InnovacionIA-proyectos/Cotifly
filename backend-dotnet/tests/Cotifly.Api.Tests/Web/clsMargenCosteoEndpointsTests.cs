using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Margen sobre el costeo de Nexo y "Mis cotizaciones" (HU-005.7, ADR-015 §5) por HTTP con SQL Server real y el Nexo falso: el
/// precio sale del módulo de Precio, el costo bruto de Nexo nunca llega al comercial, los servicios conexos se eligen y el PDF
/// lista solo los incluidos.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenCosteoEndpointsTests : IDisposable
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsMargenCosteoEndpointsTests(clsEntornoApiWeb entorno) => _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElCostoBrutoDeNexoNuncaSeMuestraComoPrecioFinalAlComercial()
    {
        var (sesion, folio) = await EnviarAsync();

        // Antes del costeo: sin precio (nunca el costo).
        var antes = await FilaAsync(sesion, folio);
        antes.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);

        // Nexo costea con 2.550.000 (flete + retorno); el comercial agrega en Cotifly un conexo de 170.000 (HU-021, decisión 3); la línea
        // Importacion tiene política 15 %.
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_000_000m, retorno: 550_000m);
        await _api.ProcesarPollingAsync(folio);
        await AgregarConexoAsync(sesion, folio, "Cargue", 170_000m);

        using var lista = await _api.EnviarAsync(_get, "/web/mis-cotizaciones", sesion.Access);
        var cuerpoLista = await lista.Content.ReadAsStringAsync(Ct);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        var cuerpoCosteo = json.GetRawText();

        var fila = (await FilaAsync(sesion, folio));
        fila.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        fila.GetProperty("precio_final").GetDecimal().Should().Be(3_200_000m, "(2.550.000 + 170.000) / (1 - 0,15): HU-021, el precio de la ruta incluye los conexos");
        json.GetProperty("precio_final").GetDecimal().Should().Be(3_200_000m);
        json.GetProperty("servicios_conexos")[0].GetProperty("precio").GetDecimal().Should().Be(200_000m);
        foreach (var cuerpo in new[] { cuerpoLista, cuerpoCosteo })
        {
            cuerpo.Should().NotContain("2550000").And.NotContain("170000", "el costo de Nexo no se expone");
            cuerpo.Should().NotContain("\"costo\"");
        }
    }

    [Fact]
    public async Task ElComercialEligeLosServiciosConexosYElPdfSoloListaLosIncluidos()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        await AgregarConexoAsync(sesion, folio, "Cargue en puerto", 170_000m);
        await AgregarConexoAsync(sesion, folio, "Escolta", 85_000m);
        using var antes = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var servicios = (await VerificarContratoAsync(antes, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("servicios_conexos").EnumerateArray().ToList();
        servicios.Should().OnlyContain(s => s.GetProperty("incluido").GetBoolean(), "por defecto se incluyen");
        var cargue = servicios.Single(s => s.GetProperty("nombre").GetString() == "Cargue en puerto").GetProperty("id").GetString()!;

        using var seleccion = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { incluidos = new[] { cargue } }), antes.Headers.ETag?.Tag);
        var elegida = await VerificarContratoAsync(seleccion, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, HttpStatusCode.OK);
        seleccion.Headers.ETag.Should().NotBeNull().And.NotBe(antes.Headers.ETag, "la selección cambia la fila y trae el ETag nuevo");
        elegida.GetProperty("servicios_conexos").EnumerateArray().Count(s => s.GetProperty("incluido").GetBoolean()).Should().Be(1);

        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        var salida = await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        salida.GetProperty("folio").GetString().Should().Be(folio);

        using var descarga = await _api.EnviarAsync(_get, $"/pdfs/{folio}.pdf", sesion.Access);
        descarga.StatusCode.Should().Be(HttpStatusCode.OK);
        var texto = clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(Ct)).Texto;
        // HU-033 (#199): el valor final de la ruta con el conexo incluido (2.550.000 / 0,85 + 170.000 / 0,85) y el conexo sin precio aparte.
        texto.Should().Contain("Cargue en puerto").And.Contain("3.200.000").And.NotContain("Escolta", "solo los conexos incluidos");
        texto.Should().NotContain("$3.000.000", "sin desglose: el transporte solo no se imprime").And.NotContain("$200.000", "ni el precio del conexo");
        texto.Should().NotContain("2.550.000", "el PDF lleva el precio con margen, no el costo de Nexo");
        // #173: la nota usa las horas capturadas (4 h de cargue y 6,5 h de descargue) y no dice que no incluye el cargue (va itemizado).
        var plano = System.Text.RegularExpressions.Regex.Replace(texto, @"\s+", " ");
        plano.Should().Contain("no incluyen el servicio de descargue").And.NotContain("no incluyen servicios de cargue ni descargue");
        plano.Should().Contain("máximo 4 horas para el cargue y 6,5 horas para el descargue");

        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var otra = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        otra.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(otra, "PDF_YA_GENERADO");
    }

    [Fact]
    public async Task SinCosteoNoSePuedenElegirConexosNiGenerarElPdfYUnaAjenaEs404()
    {
        var (sesion, folio) = await EnviarAsync();
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        using var seleccion = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access, """{"incluidos":[]}""",
            await EtagAsync(sesion, folio));
        using var ajena = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", luis.Access);

        pdf.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(pdf, "COSTEO_NO_LISTO");
        seleccion.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ElAjusteDeMargenExigeIfMatchRecalculaDevuelveElEtagNuevoYDetectaElConflicto()
    {
        var (sesion, folio) = await CosteadaAsync();
        var ruta = $"/web/cotizaciones/{folio}/costeo/margen";
        var etag = await EtagAsync(sesion, folio);
        etag.Should().NotBeNullOrEmpty("GET .../costeo trae el ETag");

        using var sinIfMatch = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"margen":0.18}""", null);
        sinIfMatch.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        await VerificarErrorNegocioAsync(sinIfMatch, "PRECONDICION_REQUERIDA");

        using var ajuste = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"margen":0.18}""", etag);
        var json = await VerificarContratoAsync(ajuste, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        json.GetProperty("precio_final").GetDecimal().Should().Be(3_317_073m, "(2.550.000 + 170.000) / (1 - 0,18): el precio incluye los conexos (HU-021)");
        json.GetProperty("margen_aplicado").GetDecimal().Should().Be(0.18m);
        json.GetProperty("margen_sugerido").GetDecimal().Should().Be(0.15m);
        json.GetProperty("origen_margen").GetString().Should().Be("negociado");
        json.GetProperty("servicios_conexos")[0].GetProperty("precio").GetDecimal().Should().Be(207_317m, "170.000 / (1 - 0,18)");
        var nuevo = ajuste.Headers.ETag!.Tag;
        nuevo.Should().NotBe(etag).And.Be("\"" + json.GetProperty("row_version").GetString() + "\"");

        using var vencido = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"margen":0.20}""", etag);
        vencido.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(vencido, "CONFLICTO_CONCURRENCIA");

        using var bajo = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"margen":0.10}""", nuevo);
        bajo.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var documento = JsonDocument.Parse(await bajo.Content.ReadAsStringAsync(Ct));
        var error = documento.RootElement.GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("JUSTIFICACION_REQUERIDA");
        error.GetProperty("detalle").GetProperty("margen_sugerido").GetDecimal().Should().Be(0.15m);

        using var fuera = await EnviarConVersionAsync(_put, ruta, sesion.Access, """{"margen":1}""", nuevo);
        fuera.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(fuera, "AJUSTE_MARGEN_INVALIDO");

        var ajustes = await clsBaseDatosPruebas.EscalarAsync<string>(Configuracion("ConnectionStrings:Cotifly"),
            $"SELECT CONCAT(COUNT(*), '|', MIN(margenPolitica), '|', MIN(margenAplicado)) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'");
        ajustes.Should().Be("1|0.1500|0.1800");
    }

    [Fact]
    public async Task AjustarConElPdfSinEnviarLoInvalidaYAlRegenerarloElAnteriorQuedaArchivado()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        pdf.StatusCode.Should().Be(HttpStatusCode.Created);
        var etagPdf = pdf.Headers.ETag?.Tag;
        etagPdf.Should().NotBeNullOrEmpty("el PDF también devuelve el ETag nuevo");

        using var ajuste = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access, """{"margen":0.18}""", etagPdf);
        var json = await VerificarContratoAsync(ajuste, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        json.GetProperty("pdf_url").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();

        using var vencido = await EnviarConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access, null, etagPdf);
        vencido.StatusCode.Should().Be(HttpStatusCode.Conflict, "el If-Match opcional del PDF también se verifica");
        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var nuevo = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await VerificarContratoAsync(nuevo, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);

        using var descarga = await _api.EnviarAsync(_get, $"/pdfs/{folio}.pdf", sesion.Access);
        // HU-033 (#199): el valor final de la ruta con el margen ajustado (2.550.000 / 0,82 + el Cargue 170.000 / 0,82), sin desglose.
        clsLectorPdf.Leer(await descarga.Content.ReadAsByteArrayAsync(Ct)).Texto.Should().Contain("3.317.073", "el PDF nuevo lleva el precio ajustado");
        File.Exists(Path.Combine(Configuracion("Pdf:StorageDir"), $"{folio}-h1.pdf")).Should().BeTrue("el PDF invalidado se conserva archivado");
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("pdf_desactualizado").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ElegirConexosConElPdfGeneradoLoInvalidaEnLugarDeRechazar()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarSiCorrespondeAsync(sesion.Access, folio); // HU-036.3: como el frontend
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);

        using var seleccion = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            """{"incluidos":[]}""", pdf.Headers.ETag?.Tag);

        var json = await VerificarContratoAsync(seleccion, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, HttpStatusCode.OK);
        json.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
        json.GetProperty("pdf_url").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PorRutaElMargenYLosConexosIndicanLaRutaYQuitarUnConexoQuedaAuditado()
    {
        // HU-021.8 (#135): una sola ruta (la ruta es opcional); el margen y los conexos dicen la ruta con linea_orden o su id.
        var (sesion, folio) = await CosteadaAsync();
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var vista = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        var ruta = vista.GetProperty("rutas").EnumerateArray().Single();
        ruta.GetProperty("linea_orden").GetInt32().Should().Be(1);
        ruta.GetProperty("precio_final_linea").GetDecimal().Should().Be(3_200_000m);
        var rutaId = ruta.GetProperty("linea_solicitud_id").GetString()!;
        var conexo = ruta.GetProperty("servicios_conexos")[0].GetProperty("id").GetString()!;

        using var otraRuta = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            """{"margen":0.18,"linea_orden":2}""", costeo.Headers.ETag?.Tag);
        otraRuta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(otraRuta, "RUTA_INEXISTENTE");

        using var margen = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            clsApiWebPruebas.Json(new { margen = 0.18m, linea_solicitud_id = rutaId }), costeo.Headers.ETag?.Tag);
        var ajustado = await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        ajustado.GetProperty("rutas")[0].GetProperty("margen_aplicado").GetDecimal().Should().Be(0.18m);
        (await clsBaseDatosPruebas.EscalarAsync<int>(Configuracion("ConnectionStrings:Cotifly"),
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}' AND lineaSolicitudId = '{rutaId}'"))
            .Should().Be(1, "el ajuste guarda la ruta real");

        using var cantidad = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { linea_orden = 1, conexos = new[] { new { id = conexo, cantidad = 2 } } }), margen.Headers.ETag?.Tag);
        var conDos = await VerificarContratoAsync(cantidad, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, HttpStatusCode.OK);
        conDos.GetProperty("servicios_conexos")[0].GetProperty("cantidad").GetDecimal().Should().Be(2m);
        conDos.GetProperty("precio_final").GetDecimal().Should().Be(3_524_390m, "(2.550.000 + 2 × 170.000) / (1 - 0,18)");

        using var quitar = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            """{"conexos":[]}""", cantidad.Headers.ETag?.Tag);
        var sinConexos = await VerificarContratoAsync(quitar, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, HttpStatusCode.OK);
        sinConexos.GetProperty("servicios_conexos").GetArrayLength().Should().Be(0);
        sinConexos.GetProperty("precio_final").GetDecimal().Should().Be(3_109_756m);
        (await clsBaseDatosPruebas.EscalarAsync<int>(Configuracion("ConnectionStrings:Cotifly"),
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos WHERE lineaSolicitudId = '{rutaId}'"))
            .Should().Be(0, "quitar un conexo borra su fila");
        (await clsBaseDatosPruebas.EscalarAsync<int>(Configuracion("ConnectionStrings:Cotifly"),
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'cambiar_servicios_conexos' AND exito = 1 AND input LIKE '%{folio}%'"))
            .Should().Be(2, "cada cambio de conexos queda auditado");
    }

    [Fact]
    public async Task VariasConsultasDelPollingSinCambiosNoInvalidanElEtag()
    {
        var (sesion, folio) = await CosteadaAsync();
        var etag = await EtagAsync(sesion, folio);

        for (var i = 0; i < 3; i++)
        {
            await _api.ProcesarPollingAsync(folio);
        }

        (await EtagAsync(sesion, folio)).Should().Be(etag, "el reclamo y la agenda del polling no cambian la versión de negocio");
        using var ajuste = await EnviarConVersionAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access, """{"margen":0.18}""", etag);
        ajuste.StatusCode.Should().Be(HttpStatusCode.OK, "el ETag leído antes de las consultas sigue vigente");

        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_600_000m);
        await _api.ProcesarPollingAsync(folio);
        (await EtagAsync(sesion, folio)).Should().NotBe(ajuste.Headers.ETag!.Tag, "un costo nuevo de Nexo sí es un cambio de negocio");
    }

    /// <summary>
    /// Cotización enviada y costeada por Nexo (flete 2.550.000: transporte de 3.000.000 con el 15 %) y un conexo de 170.000 que el
    /// comercial agrega en Cotifly (precio total 3.200.000).
    /// </summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> CosteadaAsync()
    {
        var (sesion, folio) = await EnviarAsync();
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        await AgregarConexoAsync(sesion, folio, "Cargue", 170_000m);
        return (sesion, folio);
    }

    /// <summary>
    /// HU-021 (decisión 3): los conexos los agrega el comercial en Cotifly (<c>POST .../servicios-conexos</c>), que los crea en Nexo y
    /// guarda el valor que Nexo calcula; el polling ya no los importa.
    /// </summary>
    private async Task AgregarConexoAsync(clsSesionPrueba sesion, string folio, string nombre, decimal costo)
    {
        var id = Guid.NewGuid().ToString();
        _nexo.ConServicioConexo(id, $"SC-{id[..8]}", nombre, "logistico", "servicio", costo);
        using var respuesta = await EnviarConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = id, cantidad = 1 } } }), await EtagAsync(sesion, folio));
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync(Ct));
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private string Configuracion(string clave) => _api.Factory.Services.GetRequiredService<IConfiguration>()[clave]!;

    /// <summary>Petición con <c>If-Match</c> (si viene) y cuerpo JSON (si viene).</summary>
    private async Task<HttpResponseMessage> EnviarConVersionAsync(HttpMethod metodo, string ruta, string token, string? cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (cuerpo is not null)
        {
            peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        }

        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        json.GetProperty("origen_margen").GetString().Should().Be("politica_linea", "el margen se resuelve al enviar (sin SAP TM configurado)");
        json.GetProperty("margen_aplicado").GetDecimal().Should().Be(0.15m);
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<JsonElement> FilaAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, "/web/mis-cotizaciones", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/mis-cotizaciones", _get, HttpStatusCode.OK);
        return json.GetProperty("cotizaciones").EnumerateArray().Single(c => c.GetProperty("folio").GetString() == folio);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
