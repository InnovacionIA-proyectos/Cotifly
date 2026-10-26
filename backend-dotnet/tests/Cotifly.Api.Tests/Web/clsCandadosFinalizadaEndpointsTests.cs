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
    }

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
