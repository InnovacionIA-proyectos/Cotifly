using System.Net;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>GET /web/cotizaciones/regional</c> por HTTP contra la base real: contrato, política <c>GerenteComercial</c>
/// (403 al comercial), regional resuelta en el servidor (409 sin regional) y aislamiento entre regionales.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCotizacionesRegionalEndpointsTests(clsEntornoApiWeb entorno)
{
    private const string Ruta = "/web/cotizaciones/regional";
    private static readonly HttpMethod _get = HttpMethod.Get;

    private clsApiWebPruebas Api => entorno.Api;

    private static string Regional() => $"Reg-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task ElGerenteVeLasDeSuRegionalConElComercialDuenoYNuncaLasDeOtra()
    {
        var occidente = Regional();
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Regional");
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Luis Regional");
        var marta = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional(), "Marta Regional");
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente, "Gloria Regional");
        var deAna = await CrearAsync(ana.UsuarioId, "2026-03-10");
        var deLuis = await CrearAsync(luis.UsuarioId, "2026-04-15");
        var deGloria = await CrearAsync(gloria.UsuarioId, "2026-02-01");
        var deMarta = await CrearAsync(marta.UsuarioId, "2026-05-01");

        using var respuesta = await Api.EnviarAsync(_get, Ruta, gloria.Access);
        var json = await VerificarContratoAsync(respuesta, Ruta, _get, HttpStatusCode.OK);

        json.GetProperty("regional").GetString().Should().Be(occidente);
        json.GetProperty("fuente_regional").GetString().Should().Be("usuarios_web");
        json.GetProperty("total").GetInt32().Should().Be(3);
        var filas = json.GetProperty("cotizaciones").EnumerateArray().ToList();
        filas.Select(f => f.GetProperty("folio").GetString()).Should().Equal([deLuis.Folio, deAna.Folio, deGloria.Folio],
            "de la más reciente a la más antigua; la de otra regional no sale");
        filas.Select(f => f.GetProperty("comercial").GetProperty("usuario_id").GetString())
            .Should().Equal(luis.UsuarioId.ToString(), ana.UsuarioId.ToString(), gloria.UsuarioId.ToString());
        filas[0].GetProperty("comercial").GetProperty("nombre_completo").GetString().Should().Be("Luis Regional");
        filas[0].GetProperty("comercial").GetProperty("nombre_usuario").GetString().Should().Be(luis.NombreUsuario);
        filas[0].GetProperty("linea_negocio").GetString().Should().NotBeNullOrEmpty("misma proyección que Mis cotizaciones");
        json.GetRawText().Should().NotContain(deMarta.Folio);
    }

    [Fact]
    public async Task MisCotizacionesNoTraeElComercial()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional());
        await CrearAsync(ana.UsuarioId, "2026-03-10");

        using var respuesta = await Api.EnviarAsync(_get, "/web/mis-cotizaciones", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/mis-cotizaciones", _get, HttpStatusCode.OK);

        json.GetProperty("cotizaciones").EnumerateArray().Select(f => f.TryGetProperty("comercial", out _)).Should().NotBeEmpty().And.OnlyContain(tiene => !tiene);
    }

    [Fact]
    public async Task UnComercialRecibe403YUnGerenteSinRegional409()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, Regional());
        var sinRegional = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var comercial = await Api.EnviarAsync(_get, Ruta, ana.Access);
        using var gerente = await Api.EnviarAsync(_get, Ruta, sinRegional.Access);

        await VerificarContratoAsync(comercial, Ruta, _get, HttpStatusCode.Forbidden);
        await VerificarContratoAsync(gerente, Ruta, _get, HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(gerente, "REGIONAL_NO_ASIGNADA");
    }

    /// <summary>
    /// La fila regional es de consulta: el gerente descarga el PDF de un comercial de su regional, pero las acciones del
    /// dueño (seguimiento, nueva versión) le responden 404 igual que a cualquier ajeno, y la cotización no cambia.
    /// </summary>
    [Fact]
    public async Task ElGerenteDescargaElPdfDeLaFilaPeroLasAccionesDelDuenoLeResponden404()
    {
        var occidente = Regional();
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var deLuis = await Api.CrearCotizacionAsync(luis.UsuarioId, "800333444");

        using var listado = await Api.EnviarAsync(_get, Ruta, gloria.Access);
        var json = await VerificarContratoAsync(listado, Ruta, _get, HttpStatusCode.OK);
        var fila = json.GetProperty("cotizaciones").EnumerateArray().Single(f => f.GetProperty("folio").GetString() == deLuis.Folio);
        fila.GetProperty("pdf_url").GetString().Should().EndWith($"/pdfs/{deLuis.ArchivoPdf}");

        using var pdf = await Api.EnviarAsync(_get, $"/pdfs/{deLuis.ArchivoPdf}", gloria.Access);
        using var seguimiento = await Api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{deLuis.Folio}/seguimiento", gloria.Access, """{"ganada":true}""");
        using var nuevaVersion = await Api.EnviarAsync(_get, $"/web/cotizaciones/{deLuis.Id}/nueva-version", gloria.Access);
        var ganada = await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{deLuis.Id}' AND ganada IS NOT NULL");

        pdf.StatusCode.Should().Be(HttpStatusCode.OK, "el PDF es la excepción regional (sección 5.6 de la guía)");
        seguimiento.StatusCode.Should().Be(HttpStatusCode.NotFound, "el resultado solo lo marca el dueño");
        nuevaVersion.StatusCode.Should().Be(HttpStatusCode.NotFound, "la nueva versión solo la pide el dueño");
        ganada.Should().Be(0, "el intento del gerente no cambió la cotización");
    }

    [Fact]
    public async Task SinSesionResponde401()
    {
        using var respuesta = await Api.EnviarAsync(_get, Ruta, null);

        await VerificarContratoAsync(respuesta, Ruta, _get, HttpStatusCode.Unauthorized);
    }

    /// <summary>Cotización del comercial con su fecha de creación fijada por SQL.</summary>
    private async Task<clsCotizacionPrueba> CrearAsync(Guid usuarioId, string creada)
    {
        var cotizacion = await Api.CrearCotizacionAsync(usuarioId, conPdf: false);
        await clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET createdAt = '{creada}T12:00:00-05:00' WHERE id = '{cotizacion.Id}';");
        return cotizacion;
    }
}
