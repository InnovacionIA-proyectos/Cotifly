using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/ajuste_margen_costeo.feature</c> (HU-013.2, #98, ADR-018 §3, §8 y §11): la API real en memoria, SQL Server
/// 2019 con el 0006 y el Nexo falso de <see cref="clsPasosCosteo"/>. Las mutaciones envían <c>If-Match</c> con la versión vigente de
/// la fila (leída de la base) salvo que el paso diga otra cosa.
/// </summary>
[Binding]
public sealed class clsPasosAjusteMargen(clsContextoWeb web, clsPasosCosteo costeo)
{
    private readonly Dictionary<string, string> _duenos = new(StringComparer.OrdinalIgnoreCase);
    private string? _etagGuardado;

    [Given("{string} envió a costeo el formulario ampliado del cliente {string} como {string}")]
    [Given("que {string} envió a costeo el formulario ampliado del cliente {string} como {string}")]
    [Given("que {string} envió a costeo el formulario ampliado del cliente {string} como {string} y Nexo no lo ha costeado")]
    public async Task DadoQueEnvioACosteo(string alias, string nit, string cotizacion)
    {
        await costeo.CuandoEnviaACosteo(alias, nit, cotizacion);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        _duenos[cotizacion] = alias;
    }

    [Given("Nexo costeó la solicitud de {string} con un flete de {int} y el polling lo registró")]
    public async Task DadoQueNexoCosteo(string cotizacion, int flete)
    {
        costeo.Nexo.Costear((await costeo.Columna(cotizacion, "costeoId"))!, flete);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be("autocosteo");
    }

    [Given("que el costeo de {string} trae el servicio conexo {string} con un valor de Nexo de {int}")]
    public async Task DadoElConexo(string cotizacion, string nombre, int valor)
    {
        costeo.Nexo.AgregarConexo((await costeo.Columna(cotizacion, "costeoId"))!, nombre, valor);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
    }

    [Given("{string} ajusta el margen de {string} a {string} sin justificación")]
    [When("{string} ajusta el margen de {string} a {string} sin justificación")]
    public async Task CuandoAjustaSinJustificacion(string alias, string cotizacion, string margen) =>
        await AjustarAsync(alias, cotizacion, Cuerpo(margen, null), await VersionVigenteAsync(cotizacion));

    [When("{string} ajusta el margen de {string} a {string} con la justificación {string}")]
    public async Task CuandoAjustaConJustificacion(string alias, string cotizacion, string margen, string justificacion) =>
        await AjustarAsync(alias, cotizacion, Cuerpo(margen, justificacion), await VersionVigenteAsync(cotizacion));

    [When("{string} ajusta el margen de {string} a {string} sin justificación y sin If-Match")]
    public Task CuandoAjustaSinIfMatch(string alias, string cotizacion, string margen) =>
        AjustarAsync(alias, cotizacion, Cuerpo(margen, null), null);

    [When("{string} ajusta el margen de {string} a {string} sin justificación con el ETag guardado")]
    public Task CuandoAjustaConElEtagGuardado(string alias, string cotizacion, string margen) =>
        AjustarAsync(alias, cotizacion, Cuerpo(margen, null), _etagGuardado ?? throw new InvalidOperationException("No se guardó ningún ETag."));

    [Given("que {string} leyó el costeo de {string} y guardó su ETag")]
    public async Task DadoQueLeyoElEtag(string alias, string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Get, Ruta(cotizacion), web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
        _etagGuardado = web.UltimoEtag;
        _etagGuardado.Should().NotBeNullOrEmpty("GET .../costeo trae el ETag de la fila");
    }

    [Then("la respuesta trae un ETag distinto del guardado")]
    public void EntoncesEtagNuevo()
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        web.UltimoEtag.Should().NotBeNullOrEmpty().And.NotBe(_etagGuardado);
        web.UltimoEtag.Should().Be($"\"{web.UltimoJson().GetProperty("row_version").GetString()}\"", "el ETag y row_version son el mismo valor");
    }

    [Then("el costeo de {string} tiene el precio final {int} con el margen aplicado {string} y el origen {string}")]
    public async Task EntoncesElPrecio(string cotizacion, int precio, string margen, string origen)
    {
        var json = await CosteoAsync(cotizacion);
        json.GetProperty("precio_final").GetDecimal().Should().Be(precio);
        json.GetProperty("margen_aplicado").GetDecimal().Should().Be(Decimal(margen));
        json.GetProperty("origen_margen").GetString().Should().Be(origen);
    }

    [Then("el margen sugerido de {string} sigue en {string}")]
    public async Task EntoncesElSugerido(string cotizacion, string margen) =>
        (await CosteoAsync(cotizacion)).GetProperty("margen_sugerido").GetDecimal().Should().Be(Decimal(margen));

    [Then("queda un ajuste de margen de {string} de {string} a {string} por {string} sin justificación")]
    public Task EntoncesElAjusteSinJustificacion(string cotizacion, string sugerido, string aplicado, string alias) =>
        VerificarAjusteAsync(cotizacion, sugerido, aplicado, alias, null);

    [Then("queda un ajuste de margen de {string} de {string} a {string} por {string} con la justificación {string}")]
    public Task EntoncesElAjusteConJustificacion(string cotizacion, string sugerido, string aplicado, string alias, string justificacion) =>
        VerificarAjusteAsync(cotizacion, sugerido, aplicado, alias, justificacion);

    [Then("no hay ajustes de margen de {string}")]
    public async Task EntoncesSinAjustes(string cotizacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{costeo.Folio(cotizacion)}'")).Should().Be(0);

    [Then("las métricas de ajustes de {string} marcan {int} ajuste por debajo del sugerido")]
    public async Task EntoncesLasMetricas(string alias, int ajustes)
    {
        await web.EnviarAsync(HttpMethod.Get, "/web/metricas/ajustes-margen", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        web.UltimoJson().EnumerateArray().Sum(f => f.GetProperty("num_ajustes_bajo_sugerido").GetInt32()).Should().Be(ajustes);
    }

    [Then("el servicio conexo {string} de {string} tiene el precio {int}")]
    public async Task EntoncesElConexo(string nombre, string cotizacion, int precio) =>
        (await CosteoAsync(cotizacion)).GetProperty("servicios_conexos").EnumerateArray()
            .Single(s => s.GetProperty("nombre").GetString() == nombre).GetProperty("precio").GetDecimal().Should().Be(precio);

    [Then("ninguna respuesta del costeo de {string} contiene el costo {int}")]
    public async Task EntoncesSinCosto(string cotizacion, int costo)
    {
        var texto = costo.ToString(CultureInfo.InvariantCulture);
        var anterior = web.UltimoCuerpo;
        await CosteoAsync(cotizacion);
        foreach (var cuerpo in new[] { anterior, web.UltimoCuerpo })
        {
            cuerpo.Should().NotContain(texto, "el costo de Nexo nunca llega al comercial").And.NotContain("\"costo\"");
        }
    }

    [Given("que {string} generó el PDF del costeo de {string}")]
    public async Task DadoQueGeneroElPdf(string alias, string cotizacion)
    {
        await CuandoGeneraElPdf(alias, cotizacion);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
    }

    [When("{string} genera el PDF del costeo de {string}")]
    public Task CuandoGeneraElPdf(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Post, $"{Ruta(cotizacion)}/pdf", web.Sesion(alias).Access);

    [Given("que {string} generó el PDF del costeo de {string} y lo envió al cliente")]
    public async Task DadoQueLoEnvio(string alias, string cotizacion)
    {
        await DadoQueGeneroElPdf(alias, cotizacion);

        // El envío por correo está deshabilitado en la API de pruebas: se deja la cotización como la deja un envío exitoso.
        await clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoEnvio = 'enviada' WHERE folio = '{costeo.Folio(cotizacion)}'");
    }

    [Given("que Nexo entregó la cotización de {string} y {string} la marcó como {string}")]
    public async Task DadoQueTieneResultado(string cotizacion, string alias, string resultado)
    {
        costeo.Nexo.Entregar((await costeo.Columna(cotizacion, "costeoId"))!);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        await web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/resultado", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { resultado }));
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [Then("el costeo de {string} no tiene pdf_url y está marcado con pdf_desactualizado")]
    public async Task EntoncesPdfDesactualizado(string cotizacion)
    {
        var json = await CosteoAsync(cotizacion);
        json.GetProperty("pdf_url").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("pdf_desactualizado").GetBoolean().Should().BeTrue();
    }

    [Then("el costeo de {string} tiene pdf_url y ya no está marcado con pdf_desactualizado")]
    public async Task EntoncesPdfVigente(string cotizacion)
    {
        var json = await CosteoAsync(cotizacion);
        json.GetProperty("pdf_url").GetString().Should().EndWith($"/pdfs/{costeo.Folio(cotizacion)}.pdf");
        json.GetProperty("pdf_desactualizado").GetBoolean().Should().BeFalse();
    }

    [Then("el PDF anterior de {string} queda en el historial de auditoría")]
    public async Task EntoncesElHistorial(string cotizacion)
    {
        var folio = costeo.Folio(cotizacion);
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
                $"SELECT STRING_AGG(CONCAT(h.motivo, '|', h.pdfPath), ';') FROM dbo_cotifly.CotizacionPdfsHistorial h "
                + $"JOIN dbo_cotifly.Cotizaciones c ON c.id = h.cotizacionId WHERE c.folio = '{folio}'"))
            .Should().StartWith("ajuste_margen|").And.EndWith($"{folio}.pdf", "una sola fila: el PDF que estaba vigente");
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
                "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'ajustar_margen_costeo' AND exito = 1 "
                + $"AND input LIKE '%{folio}%' AND outputResumen LIKE '%\"pdf_invalidado\":true%'"))
            .Should().Be(1, "la acción queda auditada con el PDF invalidado");
    }

    [Then("el detalle del error indica la acción {string} con {string} al endpoint de re-solicitud de {string} y nueva_version {string}")]
    public void EntoncesElDetalle(string accion, string metodo, string cotizacion, string nuevaVersion)
    {
        var detalle = web.UltimoJson().GetProperty("error").GetProperty("detalle");
        detalle.GetProperty("accion").GetString().Should().Be(accion);
        detalle.GetProperty("metodo").GetString().Should().Be(metodo);
        detalle.GetProperty("endpoint").GetString().Should().Be($"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/re-solicitar");
        detalle.GetProperty("cuerpo").GetProperty("nueva_version").GetBoolean().Should().Be(bool.Parse(nuevaVersion));
        detalle.GetProperty("cuerpo").GetProperty("motivo").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private Task AjustarAsync(string alias, string cotizacion, string cuerpo, string? ifMatch) =>
        web.EnviarAsync(HttpMethod.Put, $"{Ruta(cotizacion)}/margen", web.Sesion(alias).Access, cuerpo, ifMatch);

    /// <summary>GET del costeo con la sesión del dueño (la última respuesta queda en el contexto).</summary>
    private async Task<JsonElement> CosteoAsync(string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Get, Ruta(cotizacion), web.Sesion(_duenos[cotizacion]).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        return web.UltimoJson();
    }

    private async Task VerificarAjusteAsync(string cotizacion, string sugerido, string aplicado, string alias, string? justificacion)
    {
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT STRING_AGG(CONCAT(margenPolitica, '|', margenAplicado, '|', usuarioId, '|', ISNULL(motivo, '<null>')), ';') "
            + $"FROM dbo_cotifly.MargenAjustes WHERE folio = '{costeo.Folio(cotizacion)}'");
        fila.Should().Be(string.Join('|',
            Decimal(sugerido).ToString("0.0000", CultureInfo.InvariantCulture),
            Decimal(aplicado).ToString("0.0000", CultureInfo.InvariantCulture),
            web.Sesion(alias).UsuarioId.ToString().ToUpperInvariant(),
            justificacion ?? "<null>"));
    }

    /// <summary><c>If-Match</c> con la versión de negocio vigente (<c>versionNegocio</c>, entre comillas).</summary>
    private async Task<string> VersionVigenteAsync(string cotizacion)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.Cotizaciones WHERE folio = '{costeo.Folio(cotizacion)}'");
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private string Ruta(string cotizacion) => $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo";

    private static string Cuerpo(string margen, string? justificacion) => justificacion is null
        ? $$"""{"margen":{{margen}}}"""
        : $$"""{"margen":{{margen}},"justificacion":{{JsonSerializer.Serialize(justificacion)}}}""";

    private static decimal Decimal(string valor) => decimal.Parse(valor, CultureInfo.InvariantCulture);
}
