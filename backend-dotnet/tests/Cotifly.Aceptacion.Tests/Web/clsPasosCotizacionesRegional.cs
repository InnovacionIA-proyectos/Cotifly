using Cotifly.Aceptacion.Tests.Soporte;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/cotizaciones_regional.feature</c>: consulta de <c>GET /web/cotizaciones/regional</c> y
/// comprobación del orden y del comercial dueño de cada fila. Reutiliza las sesiones, las cotizaciones y la última respuesta de
/// <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosCotizacionesRegional(clsContextoWeb web)
{
    [When("{string} consulta las cotizaciones de su regional")]
    public Task CuandoConsultaLaRegional(string alias) =>
        web.EnviarAsync(HttpMethod.Get, "/web/cotizaciones/regional", web.Sesion(alias).Access);

    /// <summary><c>?regional=</c> con el nombre real de la regional (alias del escenario).</summary>
    [When("{string} consulta las cotizaciones de la regional {string}")]
    public Task CuandoConsultaUnaRegional(string alias, string regional) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/regional?regional={Uri.EscapeDataString(web.Regional(regional))}", web.Sesion(alias).Access);

    [When("{string} consulta las regionales disponibles")]
    public Task CuandoConsultaLasRegionales(string alias) => web.EnviarAsync(HttpMethod.Get, "/web/regionales", web.Sesion(alias).Access);

    [Then("el listado regional es de todas las regionales")]
    public void EntoncesTodas()
    {
        web.UltimoJson().GetProperty("regional").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        web.UltimoJson().GetProperty("fuente_regional").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Then("el listado regional es de la regional {string}")]
    public void EntoncesDeLaRegional(string regional)
    {
        web.UltimoJson().GetProperty("regional").GetString().Should().Be(web.Regional(regional));
        web.UltimoJson().GetProperty("fuente_regional").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Then("las regionales disponibles incluyen {string}, {string} y {string}")]
    public void EntoncesIncluyen(string una, string dos, string tres) =>
        Regionales().Should().Contain([web.Regional(una), web.Regional(dos), web.Regional(tres)]);

    [Then("las regionales disponibles son solo {string}")]
    public void EntoncesSolo(string regional) => Regionales().Should().Equal(web.Regional(regional));

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

    private List<string?> Regionales() => [.. web.UltimoJson().GetProperty("regionales").EnumerateArray().Select(r => r.GetString())];
}
