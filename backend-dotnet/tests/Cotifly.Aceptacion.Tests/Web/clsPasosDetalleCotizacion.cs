using Cotifly.Aceptacion.Tests.Soporte;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/detalle_cotizacion.feature</c>: <c>GET /web/cotizaciones/{folio}</c> con el dueño, el
/// gerente de su regional (solo consulta) y los ajenos. Reutiliza las sesiones, las cotizaciones y la última respuesta de
/// <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosDetalleCotizacion(clsContextoWeb web)
{
    [When("{string} consulta el detalle de la cotización {string}")]
    public Task CuandoConsultaElDetalle(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{Uri.EscapeDataString(web.Cotizacion(cotizacion).Folio)}", web.Sesion(alias).Access);

    [Then(@"^el detalle es de la cotización ""([^""]+)"", su dueño es ""([^""]+)"" y (no es|es) de solo consulta$")]
    public void EntoncesElDetalle(string cotizacion, string dueno, string soloConsulta)
    {
        var json = web.UltimoJson();
        var folio = web.Cotizacion(cotizacion).Folio;
        json.GetProperty("folio").GetString().Should().Be(folio);
        json.GetProperty("cotizacion").GetProperty("folio").GetString().Should().Be(folio);
        json.GetProperty("costeo").GetProperty("folio").GetString().Should().Be(folio);
        json.GetProperty("cotizacion").GetProperty("comercial").GetProperty("usuario_id").GetString()
            .Should().Be(web.Sesion(dueno).UsuarioId.ToString());
        json.GetProperty("solo_lectura").GetBoolean().Should().Be(soloConsulta == "es");
        json.GetProperty("captura").GetArrayLength().Should().Be(json.GetProperty("costeo").GetProperty("rutas").GetArrayLength());
    }

    [Then("el detalle trae el ETag igual a su row_version")]
    public void EntoncesElEtag()
    {
        web.UltimoEtag.Should().NotBeNull();
        web.UltimoEtag!.Trim('"').Should().Be(web.UltimoJson().GetProperty("row_version").GetString());
    }
}
