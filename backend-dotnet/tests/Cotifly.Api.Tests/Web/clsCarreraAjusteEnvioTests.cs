using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Carrera entre el ajuste de margen y el envío por correo (ADR-018 §11 y §12): las dos mutaciones bloquean primero la cotización y
/// comparan la versión de negocio, así que de dos peticiones simultáneas exactamente una gana (200) y la otra recibe 409. Si gana el
/// envío, el ajuste responde <c>PDF_YA_ENVIADO</c> o <c>CONFLICTO_CONCURRENCIA</c> y nunca invalida el PDF que salió.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCarreraAjusteEnvioTests : IAsyncDisposable
{
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsServidorSmtpFalso _relay = new();
    private readonly clsApiWebPruebas _api;

    public clsCarreraAjusteEnvioTests(clsEntornoApiWeb entorno) =>
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo, new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:Modo"] = "smtp_relay",
            ["Correo:SmtpRelay:Host"] = clsServidorSmtpFalso.Host,
            ["Correo:SmtpRelay:Port"] = _relay.Puerto.ToString(CultureInfo.InvariantCulture),
            ["Correo:SmtpRelay:TimeoutSeg"] = "10",
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnAjusteYUnEnvioSimultaneosDejanExactamenteUnGanador()
    {
        for (var ronda = 0; ronda < 4; ronda++)
        {
            var (sesion, folio, etag) = await CosteadaConPdfAsync();
            var ajuste = _api.EnviarAsync(HttpMethod.Put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access, """{"margen":0.18}""", etag);
            var envio = _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/enviar", sesion.Access, """{"para":["cliente@longyin.com.co"]}""");
            await Task.WhenAll(ajuste, envio);
            using var respuestaAjuste = await ajuste;
            using var respuestaEnvio = await envio;
            var cuerpoAjuste = await respuestaAjuste.Content.ReadAsStringAsync(Ct);
            var cuerpoEnvio = await respuestaEnvio.Content.ReadAsStringAsync(Ct);

            new[] { respuestaAjuste.StatusCode, respuestaEnvio.StatusCode }.Should().BeEquivalentTo(
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, $"ronda {ronda}: ajuste {cuerpoAjuste} / envío {cuerpoEnvio}");

            using var costeo = await _api.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
            using var json = JsonDocument.Parse(await costeo.Content.ReadAsStringAsync(Ct));
            if (respuestaEnvio.StatusCode == HttpStatusCode.OK)
            {
                Codigo(cuerpoAjuste).Should().BeOneOf("PDF_YA_ENVIADO", "CONFLICTO_CONCURRENCIA");
                json.RootElement.GetProperty("pdf_desactualizado").GetBoolean().Should().BeFalse("el PDF que salió sigue vigente");
                json.RootElement.GetProperty("margen_aplicado").GetDecimal().Should().Be(0.15m);
            }
            else
            {
                Codigo(cuerpoEnvio).Should().Be("CONFLICTO_CONCURRENCIA");
                json.RootElement.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
            }
        }
    }

    private async Task<(clsSesionPrueba Sesion, string Folio, string Etag)> CosteadaConPdfAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var alta = await _api.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", sesion.Access, clsCosteoEndpointsTests.Formulario());
        alta.StatusCode.Should().Be(HttpStatusCode.Created);
        using var altaJson = JsonDocument.Parse(await alta.Content.ReadAsStringAsync(Ct));
        var folio = altaJson.RootElement.GetProperty("folio").GetString()!;
        _nexo.Costear(_nexo.SolicitudesCreadas[^1], 2_550_000m);
        await _api.ProcesarPollingAsync(folio);
        using var pdf = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        pdf.StatusCode.Should().Be(HttpStatusCode.Created, await pdf.Content.ReadAsStringAsync(Ct));
        return (sesion, folio, pdf.Headers.ETag!.Tag);
    }

    private static string? Codigo(string cuerpo)
    {
        using var documento = JsonDocument.Parse(cuerpo);
        return documento.RootElement.GetProperty("error").GetProperty("codigo").GetString();
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        _nexo.Dispose();
        await _relay.DisposeAsync();
    }
}
