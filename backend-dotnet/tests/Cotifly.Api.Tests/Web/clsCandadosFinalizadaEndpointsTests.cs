using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-036.3 (#207) por HTTP con SQL Server real, el Nexo falso y el MailSender falso: antes de finalizar, el PDF, el correo y el resultado
/// responden 409 <c>COTIZACION_NO_FINALIZADA</c> con la llamada para finalizar; después de finalizar, el margen responde 409
/// <c>PDF_YA_ENVIADO</c> con la versión nueva y el PDF y el correo sí salen. Cada respuesta se valida contra <c>specs/openapi.yaml</c>.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCandadosFinalizadaEndpointsTests : IDisposable
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsBaseDatosPruebas _baseDatos;
    private readonly clsApiWebPruebas _api;

    public clsCandadosFinalizadaEndpointsTests(clsEntornoApiWeb entorno)
    {
        _baseDatos = entorno.BaseDatos;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo, extra: new Dictionary<string, string?> { ["Correo:Habilitado"] = "true" });
        _api.MailSender.Caido = false;
    }

    [Fact]
    public async Task SinFinalizarElPdfYElResultadoSeRechazanYDespuesDeFinalizarElPdfSaleYElMargenSePideEnVersionNueva()
    {
        var (sesion, folio) = await CosteadaAsync();

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await NoFinalizadaAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", folio);
        using var resultado = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/resultado", sesion.Access,
            clsApiWebPruebas.Json(new { resultado = "ganada" }));
        await NoFinalizadaAsync(resultado, "/web/cotizaciones/{folio}/resultado", folio);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.EndsWith("/resultado", StringComparison.Ordinal),
            "sin finalizar no se le pide el resultado a Nexo");

        await _api.FinalizarAsync(sesion.Access, folio);

        using var pdfFinalizada = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await VerificarContratoAsync(pdfFinalizada, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);

        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        using var margen = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            clsApiWebPruebas.Json(new { margen = 0.18m }), costeo.Headers.ETag?.Tag);
        var error = (await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("PDF_YA_ENVIADO");
        error.GetProperty("detalle").GetProperty("accion").GetString().Should().Be("nueva_version");
    }

    [Fact]
    public async Task UnaCotizacionConPdfAnteriorAHu036NoSeEnviaHastaFinalizarla()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarAsync(sesion.Access, folio);
        using (var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access))
        {
            pdf.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Sin backfill: el PDF se generó antes del script 14 y la cotización quedó sin finalizar.
        await clsBaseDatosPruebas.EjecutarAsync(_baseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = NULL, finalizadaPor = NULL WHERE folio = '{folio}'");
        var cuerpo = clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } });

        using var sinFinalizar = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/enviar", sesion.Access, cuerpo);
        await NoFinalizadaAsync(sinFinalizar, "/web/cotizaciones/{folio}/enviar", folio);
        _api.MailSender.Recibidos.Should().BeEmpty("no llega a MailSender");

        await _api.FinalizarAsync(sesion.Access, folio);
        using var finalizada = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/enviar", sesion.Access, cuerpo);
        (await VerificarContratoAsync(finalizada, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK))
            .GetProperty("estado").GetString().Should().Be("encolado");

        // DEF-1027B-1 (#228, E2E real 2026-10-27B): el 409 PDF_YA_ENVIADO del PDF trae la llamada para la versión nueva en detalle.
        using var pdfEnviada = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await VersionNuevaAsync(pdfEnviada, "/web/cotizaciones/{folio}/costeo/pdf", _post, folio);
    }

    /// <summary>
    /// Validación HU-036: todo lo que cambia el precio o la solicitud de una cotización finalizada (sin enviar) responde 409
    /// <c>PDF_YA_ENVIADO</c> con la versión nueva en <c>detalle</c>, sin tocar Nexo ni el precio; los textos del PDF y el PDF sí se permiten.
    /// </summary>
    [Fact]
    public async Task FinalizadaConexosReSolicitudYRecalculoPidenVersionNuevaSinTocarNexoYLosTextosSiSeEditan()
    {
        var (sesion, folio) = await CosteadaAsync();
        await _api.FinalizarAsync(sesion.Access, folio);
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var etag = costeo.Headers.ETag?.Tag;
        var rutaId = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas")[0].GetProperty("linea_solicitud_id").GetString();
        var precio = await PrecioAsync(folio);
        var peticionesNexo = _nexo.Peticiones.Count;

        using var agregar = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d", cantidad = 1 } } }), etag);
        await VersionNuevaAsync(agregar, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _post, folio);
        using var elegir = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { incluidos = Array.Empty<string>() }), etag);
        await VersionNuevaAsync(elegir, "/web/cotizaciones/{folio}/costeo/servicios-conexos", _put, folio);
        using var reSolicitar = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", sesion.Access,
            clsApiWebPruebas.Json(new { motivo = "Actualizar el costo" }), etag);
        await VersionNuevaAsync(reSolicitar, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post, folio);
        using var recalcular = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos", sesion.Access, "{}", etag);
        await VersionNuevaAsync(recalcular, "/web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos", _post, folio);

        _nexo.Peticiones.Count.Should().Be(peticionesNexo, "ningún candado llama a Nexo");
        (await PrecioAsync(folio)).Should().Be(precio, "el precio finalizado no cambia");

        using var textos = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/textos", sesion.Access,
            clsApiWebPruebas.Json(new { asunto = "Cotización de transporte", introduccion = "Presentamos la tarifa acordada para su carga." }), etag);
        await VerificarContratoAsync(textos, "/web/cotizaciones/{folio}/costeo/textos", _put, HttpStatusCode.OK);
        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
    }

    private static async Task VersionNuevaAsync(HttpResponseMessage respuesta, string ruta, HttpMethod metodo, string folio)
    {
        var error = (await VerificarContratoAsync(respuesta, ruta, metodo, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("PDF_YA_ENVIADO", ruta);
        error.GetProperty("detalle").GetProperty("accion").GetString().Should().Be("nueva_version");
        error.GetProperty("detalle").GetProperty("endpoint").GetString().Should().Be($"/web/cotizaciones/{folio}/costeo/re-solicitar");
    }

    private Task<decimal?> PrecioAsync(string folio) =>
        clsBaseDatosPruebas.EscalarAsync<decimal?>(_baseDatos.CadenaCotifly,
            $"SELECT precioFinal FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private static async Task NoFinalizadaAsync(HttpResponseMessage respuesta, string ruta, string folio)
    {
        var error = (await VerificarContratoAsync(respuesta, ruta, _post, HttpStatusCode.Conflict)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("COTIZACION_NO_FINALIZADA");
        error.GetProperty("mensaje").GetString().Should().StartWith($"Finaliza la cotización {folio} antes de");
        error.GetProperty("detalle").GetProperty("endpoint").GetString().Should().Be($"/web/cotizaciones/{folio}/finalizar");
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> CosteadaAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
