using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/motivo_perdida.feature</c>: la lista de motivos de pérdida
/// (<c>GET /web/motivos-perdida</c>) y su validación en <c>POST .../resultado</c> y <c>POST .../seguimiento</c>. Reutiliza el Nexo falso, los
/// folios y las columnas de <see cref="clsPasosCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosMotivoPerdida(clsContextoWeb web, clsPasosCosteo costeo)
{
    [When("{string} consulta los motivos de pérdida")]
    public Task CuandoConsultaLosMotivos(string alias) => web.EnviarAsync(HttpMethod.Get, "/web/motivos-perdida", web.Sesion(alias).Access);

    [Then("los motivos de pérdida son {string}")]
    public void EntoncesLosMotivosSon(string motivos) =>
        web.UltimoJson().GetProperty("motivos").EnumerateArray().Select(m => m.GetProperty("motivo").GetString())
            .Should().Equal(motivos.Split('|'), "la lista de NEXO, en su orden");

    [Then("solo el motivo {string} exige comentario")]
    public void EntoncesSoloExigeComentario(string motivo) =>
        web.UltimoJson().GetProperty("motivos").EnumerateArray()
            .Where(m => m.GetProperty("requiere_comentario").GetBoolean())
            .Select(m => m.GetProperty("motivo").GetString())
            .Should().Equal(motivo);

    [Then("el detalle del error lista los {int} motivos permitidos")]
    public void EntoncesElDetalleListaLosMotivos(int cantidad)
    {
        var detalle = web.UltimoJson().GetProperty("error").GetProperty("detalle");
        detalle.GetProperty("motivos_permitidos").GetArrayLength().Should().Be(cantidad);
        detalle.GetProperty("requiere_comentario").GetBoolean().Should().BeFalse();
    }

    [Then("el detalle del error pide el comentario")]
    public void EntoncesElDetallePideElComentario() =>
        web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("requiere_comentario").GetBoolean().Should().BeTrue();

    [Then("Nexo registró la cotización de {string} como {string} con el motivo {string} y el comentario {string}")]
    public async Task EntoncesNexoRegistroConMotivo(string cotizacion, string resultado, string motivo, string comentario)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Resultado(solicitud).Should().Be(resultado);
        costeo.Nexo.MotivoDelResultado(solicitud).Should().Be((motivo, comentario), "a Nexo va el motivo de la lista y el comentario aparte");
    }

    /// <summary>La excepción a la regla del motivo: las descartadas y las de costeo vencido se marcan con <c>/seguimiento</c>, solo en Cotifly.</summary>
    [When("{string} marca con el seguimiento la cotización {string} como perdida con el motivo {string} y el comentario {string}")]
    public Task CuandoMarcaConElSeguimiento(string alias, string cotizacion, string motivo, string comentario) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/seguimiento", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { ganada = false, motivo_rechazo = motivo, comentario = comentario.Length == 0 ? null : comentario }));

    /// <summary>Una perdida guardada sin motivo (dato heredado): no se revalida.</summary>
    [When("la cotización {string} quedó perdida sin motivo antes de la lista de motivos")]
    public Task CuandoQuedoPerdidaSinMotivo(string cotizacion) =>
        clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = 0, motivoRechazo = NULL, marcadoAt = SYSDATETIMEOFFSET() WHERE folio = N'{costeo.Folio(cotizacion)}'");

    [Then("la cotización {string} queda ganada {string} sin motivo")]
    public async Task EntoncesGanadaSinMotivo(string cotizacion, string ganada)
    {
        (await costeo.Columna(cotizacion, "ganada")).Should().Be(bool.Parse(ganada) ? "1" : "0");
        (await costeo.Columna(cotizacion, "motivoRechazo")).Should().BeNull();
        var fila = web.UltimoJson().GetProperty("cotizaciones").EnumerateArray()
            .Single(c => c.GetProperty("folio").GetString() == costeo.Folio(cotizacion));
        fila.GetProperty("ganada").GetBoolean().Should().Be(bool.Parse(ganada));
        fila.GetProperty("motivo_rechazo").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
