using Cotifly.Aceptacion.Tests.Soporte;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/cotizaciones_regional.feature</c> (HU-016.1, #154): consulta de <c>GET /web/cotizaciones/regional</c> y
/// comprobación del orden y del comercial dueño de cada fila. Reutiliza las sesiones, las cotizaciones y la última respuesta de
/// <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosCotizacionesRegional(clsContextoWeb web)
{
    [When("{string} consulta las cotizaciones de su regional")]
    public Task CuandoConsultaLaRegional(string alias) =>
        web.EnviarAsync(HttpMethod.Get, "/web/cotizaciones/regional", web.Sesion(alias).Access);

    [Then("el listado regional trae en orden las cotizaciones {string}, {string} y {string}")]
    public void EntoncesElOrden(string primera, string segunda, string tercera) =>
        Filas().Select(f => f.GetProperty("folio").GetString()).Should()
            .Equal(web.Cotizacion(primera).Folio, web.Cotizacion(segunda).Folio, web.Cotizacion(tercera).Folio);

    [Then("la cotización {string} del listado regional es de {string}")]
    public void EntoncesElComercial(string cotizacion, string alias)
    {
        var folio = web.Cotizacion(cotizacion).Folio;
        var comercial = Filas().Single(f => f.GetProperty("folio").GetString() == folio).GetProperty("comercial");
        comercial.GetProperty("usuario_id").GetString().Should().Be(web.Sesion(alias).UsuarioId.ToString());
        comercial.GetProperty("nombre_usuario").GetString().Should().Be(web.Sesion(alias).NombreUsuario);
    }

    private List<System.Text.Json.JsonElement> Filas() => [.. web.UltimoJson().GetProperty("cotizaciones").EnumerateArray()];
}
