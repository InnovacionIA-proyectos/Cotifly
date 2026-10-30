using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/cotizacion_finalizada.feature</c> (HU-036): <c>POST /web/cotizaciones/{folio}/finalizar</c> contra la API real
/// en memoria, SQL Server 2019 con el script 14 y el Nexo falso de <see cref="clsPasosCosteo"/>. Las mutaciones envían <c>If-Match</c>
/// con la versión vigente de la fila (leída de la base) salvo que el paso diga otra cosa.
/// </summary>
[Binding]
public sealed class clsPasosFinalizacion(clsContextoWeb web, clsPasosCosteo costeo, clsPasosAjusteMargen margen)
{
    /// <summary>La herramienta de la auditoría (<c>clsFinalizacionCotizacionService.HerramientaAuditoria</c>).</summary>
    private const string HerramientaAuditoria = "finalizar_cotizacion";

    private string? _finalizadaAtPrimera;
    private string? _etagPrimera;

    [Given("que {string} finalizó la cotización {string}")]
    public async Task DadoQueFinalizo(string alias, string cotizacion)
    {
        await CuandoFinaliza(alias, cotizacion);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        _finalizadaAtPrimera = web.UltimoJson().GetProperty("finalizada_at").GetString();
        _etagPrimera = web.UltimoEtag;
    }

    [When("{string} finaliza la cotización {string}")]
    public async Task CuandoFinaliza(string alias, string cotizacion) =>
        await FinalizarAsync(alias, cotizacion, await VersionVigenteAsync(cotizacion));

    [When("{string} finaliza la cotización {string} sin If-Match")]
    public Task CuandoFinalizaSinIfMatch(string alias, string cotizacion) => FinalizarAsync(alias, cotizacion, null);

    [When("{string} finaliza la cotización {string} con el ETag guardado")]
    public Task CuandoFinalizaConElEtagGuardado(string alias, string cotizacion) =>
        FinalizarAsync(alias, cotizacion, margen.EtagGuardado ?? throw new InvalidOperationException("No se guardó ningún ETag."));

    [Given("Nexo cambia el flete de {string} a {int} y el polling lo registró")]
    public async Task DadoQueNexoCambiaElFlete(string cotizacion, int flete)
    {
        var version = await VersionAsync(cotizacion);
        costeo.Nexo.Costear((await costeo.Columna(cotizacion, "costeoId"))!, flete);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await VersionAsync(cotizacion)).Should().BeGreaterThan(version, "el polling aplicó el costo nuevo (cambio de negocio)");
    }

    [When("{string} pide directamente el PDF del costeo de {string}")]
    public Task CuandoGeneraElPdfSinFinalizar(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/pdf", web.Sesion(alias).Access);

    [When("{string} pide directamente marcar el resultado de {string} como {string}")]
    public Task CuandoMarcaElResultadoSinFinalizar(string alias, string cotizacion, string resultado) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/resultado", web.Sesion(alias).Access,
            System.Text.Json.JsonSerializer.Serialize(new { resultado }));

    [When("{string} consulta la solicitud {string} para editarla")]
    public Task CuandoConsultaParaEditar(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/solicitud", web.Sesion(alias).Access);

    [Then("la solicitud {string} se edita como {string}")]
    public void EntoncesSeEditaComo(string cotizacion, string modo)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var json = web.UltimoJson();
        json.GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion));
        json.GetProperty("edicion").GetProperty("modo").GetString().Should().Be(modo);
        json.GetProperty("edicion").GetProperty("avisos").EnumerateArray()
            .Should().Contain(av => av.GetProperty("mensaje").GetString()!.Contains("finalizada", StringComparison.Ordinal),
                "el aviso dice por qué se crea una versión nueva");
    }

    [Then("el error pide finalizar la cotización {string} antes de {string}")]
    public void EntoncesPideFinalizar(string cotizacion, string accion)
    {
        var folio = costeo.Folio(cotizacion);
        var error = web.UltimoJson().GetProperty("error");
        error.GetProperty("mensaje").GetString().Should().StartWith($"Finaliza la cotización {folio} antes de {accion}");
        var detalle = error.GetProperty("detalle");
        detalle.GetProperty("accion").GetString().Should().Be("finalizar");
        detalle.GetProperty("metodo").GetString().Should().Be("POST");
        detalle.GetProperty("endpoint").GetString().Should().Be($"/web/cotizaciones/{folio}/finalizar");
    }

    [When("Nexo cambia el flete de {string} a {int} y entrega la cotización")]
    public async Task CuandoNexoCambiaYEntrega(string cotizacion, int flete)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Costear(solicitud, flete);
        costeo.Nexo.Entregar(solicitud);
    }

    [Then("el motivo del costeo de {string} avisa que cambió la ruta {int}")]
    public async Task EntoncesElMotivoAvisaLaRuta(string cotizacion, int ruta)
    {
        var motivo = await costeo.Columna(cotizacion, "motivoEstadoCosteo");
        motivo.Should().StartWith("El costeo de Nexo cambió después de finalizar la cotización").And.Contain($"la ruta {ruta} (");
    }

    [Then("el costo de {int} que trajo Nexo para {string} queda en la auditoría")]
    public async Task EntoncesElCostoAuditado(int costo, string cotizacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'costeo_nexo_precio_congelado' AND exito = 1 "
            + $"AND input LIKE '%{costeo.Folio(cotizacion)}%' AND outputResumen LIKE '%{costo.ToString(CultureInfo.InvariantCulture)}%'"))
        .Should().Be(1);

    [Then("la respuesta trae finalizada_at y un ETag nuevo")]
    public void EntoncesTraeFinalizadaAt()
    {
        var json = web.UltimoJson();
        json.GetProperty("finalizada_at").ValueKind.Should().Be(JsonValueKind.String);
        web.UltimoEtag.Should().NotBeNullOrEmpty();
    }

    [Then("la respuesta trae el mismo finalizada_at y el mismo ETag de la primera finalización")]
    public void EntoncesSinCambios()
    {
        web.UltimoJson().GetProperty("finalizada_at").GetString().Should().Be(_finalizadaAtPrimera);
        web.UltimoEtag.Should().Be(_etagPrimera, "re-finalizar no sube la versión de negocio");
    }

    [Then("la cotización {string} queda finalizada por {string}")]
    public async Task EntoncesQuedaFinalizada(string cotizacion, string alias)
    {
        (await costeo.Columna(cotizacion, "finalizadaAt")).Should().NotBeNull();
        Guid.Parse((await costeo.Columna(cotizacion, "finalizadaPor"))!).Should().Be(web.Sesion(alias).UsuarioId);
    }

    [Then("la cotización {string} sigue sin finalizar")]
    public async Task EntoncesSinFinalizar(string cotizacion)
    {
        (await costeo.Columna(cotizacion, "finalizadaAt")).Should().BeNull();
        (await costeo.Columna(cotizacion, "finalizadaPor")).Should().BeNull();
    }

    [Then("el costeo y el detalle de {string} traen finalizada_at")]
    public async Task EntoncesElCosteoYElDetalle(string cotizacion)
    {
        var dueno = await DuenoAsync(cotizacion);
        var folio = costeo.Folio(cotizacion);
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", dueno);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var enCosteo = web.UltimoJson().GetProperty("finalizada_at").GetString();
        enCosteo.Should().NotBeNullOrEmpty();

        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}", dueno);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var detalle = web.UltimoJson();
        detalle.GetProperty("fechas").GetProperty("finalizada_at").GetString().Should().Be(enCosteo);
        detalle.GetProperty("cotizacion").GetProperty("finalizada_at").GetString().Should().Be(enCosteo);
        detalle.GetProperty("costeo").GetProperty("finalizada_at").GetString().Should().Be(enCosteo);
    }

    [Then("el detalle del error trae el estado de costeo {string}")]
    public void EntoncesElEstadoDelError(string estado) =>
        web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("estado_costeo").GetString().Should().Be(estado);

    [Then("la finalización de {string} queda en la auditoría como exitosa")]
    public async Task EntoncesAuditadaExitosa(string cotizacion) =>
        (await EventosAsync(cotizacion, exito: true, codigo: null)).Should().Be(1);

    [Then("la finalización de {string} queda en la auditoría como rechazada por {string}")]
    public async Task EntoncesAuditadaRechazada(string cotizacion, string codigo) =>
        (await EventosAsync(cotizacion, exito: false, codigo)).Should().Be(1);

    private Task<int> EventosAsync(string cotizacion, bool exito, string? codigo) =>
        clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{HerramientaAuditoria}' AND exito = {(exito ? 1 : 0)} "
            + $"AND input LIKE '%{costeo.Folio(cotizacion)}%'" + (codigo is null ? string.Empty : $" AND outputResumen LIKE '%{codigo}%'"));

    private async Task FinalizarAsync(string alias, string cotizacion, string? ifMatch) =>
        await web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/finalizar", web.Sesion(alias).Access, null, ifMatch);

    private async Task<string> DuenoAsync(string cotizacion)
    {
        var usuarioId = Guid.Parse((await costeo.Columna(cotizacion, "usuarioId"))!);
        return web.Sesiones.Values.First(s => s.UsuarioId == usuarioId).Access;
    }

    private Task<int> VersionAsync(string cotizacion) =>
        clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{costeo.Folio(cotizacion)}'");

    /// <summary><c>If-Match</c> con la versión de negocio vigente (<c>versionNegocio</c>, entre comillas).</summary>
    private async Task<string> VersionVigenteAsync(string cotizacion) =>
        $"\"{(await VersionAsync(cotizacion)).ToString(CultureInfo.InvariantCulture)}\"";
}
