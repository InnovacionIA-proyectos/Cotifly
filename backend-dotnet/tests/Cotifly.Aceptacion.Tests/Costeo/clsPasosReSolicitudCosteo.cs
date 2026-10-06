using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/resolicitar_costeo.feature</c> (HU-013.4, #100, ADR-018 §5): la API real en memoria, SQL Server 2019 y el
/// Nexo falso de <see cref="clsPasosCosteo"/>. Las re-solicitudes envían <c>If-Match</c> con la versión vigente de la fila (leída de la
/// base) salvo que el paso diga otra cosa.
/// </summary>
[Binding]
public sealed class clsPasosReSolicitudCosteo(clsContextoWeb web, clsPasosCosteo costeo)
{
    private readonly List<(HttpStatusCode Estado, string Cuerpo)> _dobleClic = [];
    private int _altasAntes;
    private string? _folioNuevo;
    private string? _pdfAntes;

    [Given("que el polling consultó a Nexo {int} horas después del envío y {string} quedó en {string}")]
    public async Task DadoQueVencio(int horas, string cotizacion, string estado)
    {
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion), TimeSpan.FromHours(horas));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be(estado);
        _altasAntes = costeo.Nexo.SolicitudesCreadas.Count;
    }

    [Given("que Nexo costeó la solicitud de {string} con un flete de {int} y la entregó")]
    public async Task DadoQueEntrego(string cotizacion, int flete)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Costear(solicitud, flete);
        costeo.Nexo.Entregar(solicitud);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be("entregada");
        _altasAntes = costeo.Nexo.SolicitudesCreadas.Count;
    }

    [Given("que Nexo respondió {int} al alta de {string} y quedó en {string}")]
    public async Task DadoQueFalloElAlta(int estado, string cotizacion, string estadoCosteo)
    {
        // El alta de los antecedentes ya llegó a Nexo: se deja la cotización como la deja un alta fallida (sin solicitud ni polling).
        await clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoCosteo = '{estadoCosteo}', costeoId = NULL, costeoSiguienteConsultaAt = NULL, "
            + $"motivoEstadoCosteo = N'Nexo no recibió la solicitud (HTTP {estado}).' WHERE folio = '{costeo.Folio(cotizacion)}'");
        costeo.Nexo.AltaFalla = (HttpStatusCode)estado;
        _altasAntes = costeo.Nexo.SolicitudesCreadas.Count;
    }

    [Given("que la cotización {string} está en el estado de costeo {string}")]
    public async Task DadoElEstado(string cotizacion, string estado)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        switch (estado)
        {
            case "autocosteo":
                costeo.Nexo.Costear(solicitud, 2_550_000m);
                break;
            case "descartada":
                costeo.Nexo.Descartar(solicitud);
                break;
        }

        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be(estado);
        _altasAntes = costeo.Nexo.SolicitudesCreadas.Count;
    }

    [Given("{string} marcó el resultado de {string} como {string} con el motivo {string}")]
    public async Task DadoElResultado(string alias, string cotizacion, string resultado, string motivo)
    {
        await web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/resultado", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { resultado, motivo }));
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [When("{string} re-solicita el costeo de {string} con el motivo {string}")]
    public async Task CuandoReSolicita(string alias, string cotizacion, string motivo) =>
        await ReSolicitarAsync(alias, cotizacion, new JsonObject { ["motivo"] = motivo }, await VersionVigenteAsync(cotizacion));

    [When("{string} re-solicita el costeo de {string} con el motivo {string} sin If-Match")]
    public Task CuandoReSolicitaSinIfMatch(string alias, string cotizacion, string motivo) =>
        ReSolicitarAsync(alias, cotizacion, new JsonObject { ["motivo"] = motivo }, null);

    [When("{string} re-solicita el costeo de {string} con el motivo {string} y nueva_version {string}")]
    public async Task CuandoReSolicitaNuevaVersion(string alias, string cotizacion, string motivo, string nuevaVersion)
    {
        _pdfAntes = await costeo.Columna(cotizacion, "pdfPath");
        await ReSolicitarAsync(alias, cotizacion, new JsonObject { ["motivo"] = motivo, ["nueva_version"] = bool.Parse(nuevaVersion) },
            await VersionVigenteAsync(cotizacion));
    }

    [When("{string} re-solicita el costeo de {string} con el motivo {string} y estos datos de carga:")]
    public async Task CuandoReSolicitaConDatos(string alias, string cotizacion, string motivo, DataTable datos)
    {
        ArgumentNullException.ThrowIfNull(datos);
        var carga = new JsonObject();
        foreach (var fila in datos.Rows)
        {
            carga[fila["campo"]] = Valor(fila["valor"]);
        }

        await ReSolicitarAsync(alias, cotizacion, new JsonObject { ["motivo"] = motivo, ["datos_carga"] = carga }, await VersionVigenteAsync(cotizacion));
    }

    [When("{string} re-solicita dos veces el costeo de {string} con el motivo {string}, el peso {int} y la misma Idempotency-Key")]
    public async Task CuandoDobleClic(string alias, string cotizacion, string motivo, int peso)
    {
        var cuerpo = new JsonObject { ["motivo"] = motivo, ["datos_carga"] = new JsonObject { ["peso_kg"] = peso } }.ToJsonString();
        var encabezados = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["If-Match"] = await VersionVigenteAsync(cotizacion),
            ["Idempotency-Key"] = Guid.NewGuid().ToString(),
        };
        var ruta = Ruta(cotizacion);
        var token = web.Sesion(alias).Access;

        var respuestas = await Task.WhenAll(
            web.EnviarSinGuardarAsync(HttpMethod.Post, ruta, token, cuerpo, encabezados),
            web.EnviarSinGuardarAsync(HttpMethod.Post, ruta, token, cuerpo, encabezados));
        foreach (var respuesta in respuestas)
        {
            using (respuesta)
            {
                _dobleClic.Add((respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync()));
            }
        }
    }

    [Then("las dos respuestas tienen el estado {int} y el mismo cuerpo")]
    public void EntoncesDobleClic(int estado)
    {
        _dobleClic.Should().HaveCount(2);
        _dobleClic.Should().OnlyContain(r => r.Estado == (HttpStatusCode)estado, string.Join(" | ", _dobleClic.Select(r => r.Cuerpo)));
        _dobleClic[1].Cuerpo.Should().Be(_dobleClic[0].Cuerpo, "la repetición devuelve la respuesta de la primera");
    }

    [Then("Nexo recibió una sola solicitud nueva para el folio base de {string}")]
    public void EntoncesUnaSolaAlta(string cotizacion) =>
        costeo.Nexo.SolicitudesCreadas.Should().HaveCount(_altasAntes + 1, $"un doble clic de {cotizacion} no envía dos capturas");

    [Then("{string} tiene {int} cotización con el folio base de {string}")]
    [Then("{string} tiene {int} cotizaciones con el folio base de {string}")]
    public async Task EntoncesCotizacionesDeLaFamilia(string alias, int cantidad, string cotizacion)
    {
        var folio = costeo.Folio(cotizacion);
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE usuarioId = '{web.Sesion(alias).UsuarioId}' "
                + $"AND (folio = '{folio}' OR folio LIKE '{folio}-v%')"))
            .Should().Be(cantidad);
    }

    [Then("la cotización {string} conserva su folio y su versión {int}")]
    public async Task EntoncesConservaFolio(string cotizacion, int version)
    {
        web.UltimoJson().GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion));
        web.UltimoJson().GetProperty("version").GetInt32().Should().Be(version);
        (await costeo.Columna(cotizacion, "version")).Should().Be(version.ToString(CultureInfo.InvariantCulture));
    }

    [Then("Nexo recibió una segunda solicitud de {string} a {string} con el BP {string} y {int} posiciones")]
    public async Task EntoncesSegundaSolicitud(string origen, string destino, string bp, int posiciones)
    {
        costeo.Nexo.SolicitudesCreadas.Should().HaveCount(2, "Nexo no tiene 'reabrir': re-solicitar es una captura nueva");
        costeo.EntoncesNexoRecibio(origen, destino, bp, posiciones);
        var cotizacion = costeo.Nexo.SolicitudesCreadas[^1];
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE costeoId = '{cotizacion}'")).Should().Be(1, "se guarda el id de la solicitud nueva");
    }

    [Then("la re-solicitud de {string} queda en la auditoría con el motivo {string} y el usuario {string}")]
    public async Task EntoncesAuditada(string cotizacion, string motivo, string alias)
    {
        var entrada = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT TOP 1 input FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'costeo_nexo_re_solicitud' AND exito = 1 "
            + $"AND input LIKE '%{costeo.Folio(cotizacion)}%' ORDER BY createdAt DESC");
        entrada.Should().Contain(motivo).And.Contain(web.Sesion(alias).UsuarioId.ToString());
    }

    [Then("la cotización {string} vuelve a consultarse en Nexo")]
    public async Task EntoncesVuelveAConsultarse(string cotizacion)
    {
        (await costeo.Columna(cotizacion, "costeoSiguienteConsultaAt")).Should().NotBeNull("el polling se reanuda");
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Costear(solicitud, 2_550_000m);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be("autocosteo", "el polling consulta la solicitud nueva");
    }

    [Then("la respuesta trae el folio de {string} con el sufijo {string} y la versión {int}")]
    public void EntoncesElFolioNuevo(string cotizacion, string sufijo, int version)
    {
        var json = web.UltimoJson();
        _folioNuevo = json.GetProperty("folio").GetString();
        _folioNuevo.Should().Be(costeo.Folio(cotizacion) + sufijo);
        json.GetProperty("version").GetInt32().Should().Be(version);
    }

    [Then("el encabezado Location apunta al costeo de la versión nueva")]
    public void EntoncesLocation() => web.UltimaUbicacion.Should().Be($"/web/cotizaciones/{FolioNuevo}/costeo");

    [Then("la versión nueva queda en el estado de costeo {string} con el peso {int} y el vehículo {string}")]
    public async Task EntoncesLaVersionNueva(string estado, int peso, string vehiculo)
    {
        (await ColumnaFolioAsync(FolioNuevo, "estadoCosteo")).Should().Be(estado);
        decimal.Parse((await ColumnaFolioAsync(FolioNuevo, "pesoKg"))!, CultureInfo.InvariantCulture).Should().Be(peso);
        (await ColumnaFolioAsync(FolioNuevo, "tipoVehiculo")).Should().Be(vehiculo);
    }

    [Then("la cotización {string} sigue en el estado de costeo {string} sin cambios")]
    public async Task EntoncesSinCambios(string cotizacion, string estado)
    {
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be(estado);
        (await costeo.Columna(cotizacion, "versionNegocio")).Should().Be(_versionLeida, "la original no se toca");
    }

    [Then("Nexo recibió la solicitud de la versión nueva con el peso {int}")]
    public void EntoncesNexoRecibioElPeso(int peso)
    {
        costeo.Nexo.SolicitudesCreadas.Should().HaveCount(_altasAntes + 1);
        costeo.Nexo.UltimaAlta!["lineas"]![0]!["peso"]!.GetValue<decimal>().Should().Be(peso);
    }

    [Then("Nexo no recibió otra solicitud para {string}")]
    public void EntoncesSinOtraAlta(string cotizacion) =>
        costeo.Nexo.SolicitudesCreadas.Should().HaveCount(_altasAntes, $"{cotizacion} no se reenvía a Nexo");

    [Then("la cotización {string} conserva su PDF enviado sin cambios")]
    public async Task EntoncesConservaPdf(string cotizacion)
    {
        _pdfAntes.Should().NotBeNull();
        (await costeo.Columna(cotizacion, "pdfPath")).Should().Be(_pdfAntes);
        (await costeo.Columna(cotizacion, "estadoEnvio")).Should().Be("enviada");
    }

    [Then("la respuesta trae un ETag")]
    public async Task EntoncesEtag()
    {
        web.UltimoEstado.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        web.UltimoEtag.Should().NotBeNullOrEmpty();
        var folio = web.UltimoJson().GetProperty("folio").GetString()!;
        web.UltimoEtag.Should().Be($"\"{await ColumnaFolioAsync(folio, "versionNegocio")}\"", "el ETag es la versión vigente de la cotización resultante");
    }

    private string? _versionLeida;

    private string FolioNuevo => _folioNuevo ?? throw new InvalidOperationException("Falta el paso que lee el folio de la versión nueva.");

    private Task ReSolicitarAsync(string alias, string cotizacion, JsonObject cuerpo, string? ifMatch) =>
        web.EnviarAsync(HttpMethod.Post, Ruta(cotizacion), web.Sesion(alias).Access, cuerpo.ToJsonString(), ifMatch);

    private string Ruta(string cotizacion) => $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo/re-solicitar";

    /// <summary><c>If-Match</c> con la versión de negocio vigente (<c>versionNegocio</c>, entre comillas).</summary>
    private async Task<string> VersionVigenteAsync(string cotizacion)
    {
        _versionLeida = await costeo.Columna(cotizacion, "versionNegocio");
        if (_altasAntes == 0)
        {
            _altasAntes = costeo.Nexo.SolicitudesCreadas.Count;
        }

        return $"\"{_versionLeida}\"";
    }

    private static Task<string?> ColumnaFolioAsync(string folio, string columna) =>
        clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT CAST({columna} AS nvarchar(max)) FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'");

    /// <summary>Valor de una celda de la tabla como JSON: número, booleano o texto.</summary>
    private static JsonNode? Valor(string texto) =>
        decimal.TryParse(texto, NumberStyles.Number, CultureInfo.InvariantCulture, out var numero) ? JsonValue.Create(numero)
        : bool.TryParse(texto, out var booleano) ? JsonValue.Create(booleano)
        : JsonValue.Create(texto);
}
