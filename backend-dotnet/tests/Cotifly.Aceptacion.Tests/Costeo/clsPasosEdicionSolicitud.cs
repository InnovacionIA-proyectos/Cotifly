using System.Net;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/editar_solicitud.feature</c> (HU-028, #186, ADR-021): el GET del modelo editable y el PUT con ese modelo
/// cambiado y el <c>ETag</c> vigente, como lo hace la pantalla "Editar solicitud", contra la API real y el Nexo falso de
/// <see cref="clsPasosCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosEdicionSolicitud(clsContextoWeb web, clsPasosCosteo costeo, clsPasosLicitaciones licitaciones)
{
    private string? _solicitudAnterior;

    [When("{string} edita la solicitud {string} con las horas de cargue en {int}")]
    public Task CuandoEditaLasHoras(string alias, string cotizacion, int horas) =>
        EditarAsync(alias, cotizacion, modelo =>
        {
            // #278 (DU §5): las horas de cargue y descargue van las dos o ninguna; si la ruta no tenía, la de descargue queda en 4.
            var ruta = modelo["rutas"]![0]!;
            ruta["horas_cargue"] = horas;
            ruta["horas_descargue"] ??= 4;
        });

    [When("{string} edita la solicitud {string} con el NIT {string}")]
    public Task CuandoEditaElNit(string alias, string cotizacion, string nit) =>
        EditarAsync(alias, cotizacion, modelo => modelo["nit_cliente"] = nit);

    [When("Nexo no deja rechazar solicitudes")]
    public void CuandoNexoNoDejaRechazar() => costeo.Nexo.RechazoFalla = HttpStatusCode.ServiceUnavailable;

    [When("{string} consulta para editar la solicitud de la licitación {string}")]
    public Task CuandoConsultaLaLicitacion(string alias, string licitacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{licitaciones.Folio(licitacion)}/solicitud", web.Sesion(alias).Access);

    [Then("la edición de {string} se aplicó como {string} en el mismo folio")]
    public void EntoncesElModoEnElMismoFolio(string cotizacion, string modo)
    {
        var json = web.UltimoJson();
        json.GetProperty("modo").GetString().Should().Be(modo);
        json.GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion));
    }

    [Then("la edición de {string} se aplicó como {string} en el folio con el sufijo {string}")]
    public void EntoncesElModoEnOtroFolio(string cotizacion, string modo, string sufijo)
    {
        var json = web.UltimoJson();
        json.GetProperty("modo").GetString().Should().Be(modo);
        json.GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion) + sufijo);
        web.UltimaUbicacion.Should().Be($"/web/cotizaciones/{costeo.Folio(cotizacion)}{sufijo}/costeo");
    }

    [Then("Nexo no recibió ningún rechazo")]
    public void EntoncesSinRechazos() => costeo.Nexo.IntentosRechazo.Should().Be(0);

    [Then("Nexo rechazó la solicitud anterior de {string} con el motivo {string}")]
    public void EntoncesNexoRechazo(string cotizacion, string motivo)
    {
        var rechazo = costeo.Nexo.Rechazos.Should().ContainSingle().Subject;
        (rechazo.SolicitudId, rechazo.Motivo).Should().Be((_solicitudAnterior!, motivo));
        rechazo.Comentario.Should().Contain(costeo.Folio(cotizacion));
        costeo.Nexo.EstadoDe(_solicitudAnterior!).Should().Be("Descartada");
    }

    [Then("la cotización {string} apunta a una solicitud nueva de Nexo")]
    public async Task EntoncesSolicitudNueva(string cotizacion)
    {
        var actual = await costeo.Columna(cotizacion, "costeoId");
        actual.Should().NotBeNull().And.NotBe(_solicitudAnterior);
        costeo.Nexo.SolicitudesCreadas.Should().Contain(actual!);
    }

    [Then("Nexo tiene una sola solicitud de {string} y sigue viva")]
    public async Task EntoncesUnaSolaSolicitud(string cotizacion)
    {
        costeo.Nexo.SolicitudesCreadas.Should().ContainSingle("si el rechazo falla no se captura otra");
        (await costeo.Columna(cotizacion, "costeoId")).Should().Be(_solicitudAnterior);
        costeo.Nexo.EstadoDe(_solicitudAnterior!).Should().NotBe("Descartada");
    }

    [Then("la ruta {int} de {string} tiene {int} horas de cargue")]
    public async Task EntoncesLasHoras(int orden, string cotizacion, int horas) =>
        (await clsBaseDatosPruebas.EscalarAsync<decimal>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT l.horasCargue FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}")).Should().Be(horas);

    [Then("la edición no se permite por {string}")]
    public void EntoncesNoSePermite(string motivo)
    {
        var edicion = web.UltimoJson().GetProperty("edicion");
        edicion.GetProperty("permitida").GetBoolean().Should().BeFalse();
        edicion.GetProperty("motivo_no_permitida").GetString().Should().Be(motivo);
    }

    /// <summary>
    /// Como la pantalla: lee el modelo editable (con su ETag), le aplica <paramref name="cambio"/> y lo devuelve en el PUT con
    /// <c>If-Match</c> y el <c>modo_esperado</c> que anunció el GET.
    /// </summary>
    private async Task EditarAsync(string alias, string cotizacion, Action<JsonNode> cambio)
    {
        var folio = costeo.Folio(cotizacion);
        _solicitudAnterior = await costeo.Columna(cotizacion, "costeoId");
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/solicitud", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var etag = web.UltimoEtag ?? throw new InvalidOperationException("GET .../solicitud no trajo ETag.");
        var modelo = JsonNode.Parse(web.UltimoCuerpo)!;
        modelo["modo_esperado"] = modelo["edicion"]!["modo"]!.GetValue<string>();
        cambio(modelo);
        await web.EnviarAsync(HttpMethod.Put, $"/web/cotizaciones/{folio}/solicitud", web.Sesion(alias).Access, modelo.ToJsonString(), etag);
    }
}
