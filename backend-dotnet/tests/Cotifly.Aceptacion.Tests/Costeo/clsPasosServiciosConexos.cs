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
/// Pasos de <c>specs/features/servicios_conexos_nexo.feature</c> (HU-013.3, #99, ADR-018 §4, §8 y §11): la API real en memoria, SQL
/// Server 2019 y el Nexo falso de <see cref="clsPasosCosteo"/> con su catálogo de servicios conexos. Las mutaciones envían
/// <c>If-Match</c> con la versión vigente de la fila (leída de la base) salvo que el paso diga otra cosa.
/// </summary>
[Binding]
public sealed class clsPasosServiciosConexos(
    clsContextoWeb web, clsPasosCosteo costeo, clsPasosLicitaciones licitaciones, clsPasosReSolicitudCosteo reSolicitud)
{
    private readonly Dictionary<string, string> _licitaciones = new(StringComparer.OrdinalIgnoreCase);

    [Given("el catálogo de Nexo tiene estos servicios conexos:")]
    public void DadoElCatalogo(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        foreach (var fila in tabla.Rows)
        {
            costeo.Nexo.ConServicioConexo(fila["id"], fila["codigo"], fila["nombre"], fila["categoria"], fila["unidad"],
                decimal.Parse(fila["costo_unitario"], CultureInfo.InvariantCulture), bool.Parse(fila["activo"]));
        }
    }

    // --- Catálogo ---

    [When("{string} busca servicios conexos con el texto {string}")]
    public Task CuandoBusca(string alias, string texto) =>
        web.EnviarAsync(HttpMethod.Get, $"/web/servicios-conexos?q={Uri.EscapeDataString(texto)}", web.Sesion(alias).Access);

    [When("{string} busca servicios conexos de la categoría {string} en la página {int} de {int}")]
    public Task CuandoBuscaPorCategoria(string alias, string categoria, int pagina, int tamano) =>
        web.EnviarAsync(HttpMethod.Get,
            $"/web/servicios-conexos?categoria={Uri.EscapeDataString(categoria)}&page={pagina}&page_size={tamano}", web.Sesion(alias).Access);

    [Given("que Nexo responde {int} al catálogo de servicios conexos")]
    public void DadoQueElCatalogoFalla(int estado) => costeo.Nexo.CatalogoFalla = (HttpStatusCode)estado;

    [Then("la página trae {int} servicios con el total {int}")]
    [Then("la página trae {int} servicio con el total {int}")]
    public void EntoncesLaPagina(int servicios, int total)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var pagina = web.UltimoJson();
        pagina.GetProperty("items").GetArrayLength().Should().Be(servicios);
        pagina.GetProperty("total").GetInt32().Should().Be(total);
    }

    [Then("ninguna respuesta del catálogo contiene el campo {string} ni el valor {int}")]
    public void EntoncesSinCosto(string campo, int valor)
    {
        web.UltimoCuerpo.Should().NotContain($"\"{campo}\"").And.NotContain("costo")
            .And.NotContain(valor.ToString(CultureInfo.InvariantCulture), "Cotifly nunca expone el costo de Nexo");
    }

    [Then("Nexo recibió la consulta del catálogo con {string} y la identidad de {string}")]
    public async Task EntoncesNexoRecibioLaConsulta(string parametro, string alias)
    {
        var consulta = costeo.Nexo.Peticiones.Last(p => p.Url.AbsolutePath == "/api/servicios-conexos");
        consulta.Url.Query.Should().Contain(parametro);
        var externo = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT CAST(idUsuarioExterno AS nvarchar(20)) FROM dbo_cotifly.UsuariosWeb WHERE id = '{web.Sesion(alias).UsuarioId}'");
        consulta.IdPortalUsuario.Should().NotBeNullOrEmpty().And.Be(externo);
    }

    [Then("el primer servicio de la página es {string}")]
    public void EntoncesElPrimero(string nombre) =>
        web.UltimoJson().GetProperty("items")[0].GetProperty("nombre").GetString().Should().Be(nombre);

    // --- Agregar ---

    [Given("Nexo costeó la solicitud de {string} con un flete de {int} y el conexo {string} de {int} y el polling lo registró")]
    public async Task DadoCosteadaConConexo(string cotizacion, int flete, string conexo, int valor)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Costear(solicitud, flete);
        await web.Api.ProcesarPollingAsync(costeo.Folio(cotizacion));
        (await costeo.Columna(cotizacion, "estadoCosteo")).Should().Be("autocosteo");
        await costeo.AgregarConexoEnCotiflyAsync(cotizacion, conexo, valor);
    }

    [When("{string} agrega a {string} en la línea {int} el servicio conexo {string} con la cantidad {int}")]
    public async Task CuandoAgrega(string alias, string cotizacion, int linea, string servicio, int cantidad) =>
        await AgregarAsync(alias, cotizacion, Cuerpo([(linea, servicio, cantidad)]), await VersionAsync(cotizacion));

    [When("{string} agrega a {string} en la línea {int} el servicio conexo {string} con la cantidad {int} sin If-Match")]
    public Task CuandoAgregaSinIfMatch(string alias, string cotizacion, int linea, string servicio, int cantidad) =>
        AgregarAsync(alias, cotizacion, Cuerpo([(linea, servicio, cantidad)]), null);

    [When("{string} agrega a {string} estos servicios conexos:")]
    public async Task CuandoAgregaVarios(string alias, string cotizacion, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var items = tabla.Rows.Select(f => (int.Parse(f["linea_orden"], CultureInfo.InvariantCulture), f["servicio_id"],
            int.Parse(f["cantidad"], CultureInfo.InvariantCulture))).ToList();
        await AgregarAsync(alias, cotizacion, Cuerpo(items), await VersionAsync(cotizacion));
    }

    [When("{string} agrega a {string} {int} items del servicio conexo {string}")]
    public async Task CuandoAgregaNItems(string alias, string cotizacion, int cantidadItems, string servicio) =>
        await AgregarAsync(alias, cotizacion, Cuerpo([.. Enumerable.Range(0, cantidadItems).Select(_ => (1, servicio, 1))]), await VersionAsync(cotizacion));

    [When("{string} agrega dos veces a {string} en la línea {int} el servicio conexo {string} con la cantidad {int} y la misma Idempotency-Key")]
    public async Task CuandoAgregaDosVeces(string alias, string cotizacion, int linea, string servicio, int cantidad)
    {
        var version = await VersionAsync(cotizacion);
        var clave = Guid.NewGuid().ToString();
        var cuerpo = Cuerpo([(linea, servicio, cantidad)]);
        var respuestas = reSolicitud.RespuestasDobleClic;
        respuestas.Clear();
        for (var i = 0; i < 2; i++)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Post, Ruta(cotizacion));
            peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", web.Sesion(alias).Access);
            peticion.Headers.TryAddWithoutValidation("If-Match", version);
            peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
            peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
            using var respuesta = await web.Api.Cliente.SendAsync(peticion);
            respuestas.Add((respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync()));
        }
    }

    // "las dos respuestas tienen el estado {int} y el mismo cuerpo" es de clsPasosReSolicitudCosteo (una sola definición).

    [Given("que Nexo responde 403 al agregar conexos porque {string} no tiene el rol Comercial ni Admin")]
    public void DadoSinRol(string alias)
    {
        web.Sesion(alias).Should().NotBeNull();
        costeo.Nexo.ConexosFalla = HttpStatusCode.Forbidden;
    }

    [Given("que Nexo responde {int} al agregar el conexo {string}")]
    public void DadoQueNexoRechazaElConexo(int estado, string servicio) => costeo.Nexo.RechazosConexo[servicio] = (HttpStatusCode)estado;

    [Given("que Nexo agrega el conexo pero falla al leer de nuevo la cotización de {string}")]
    public void DadoRelecturaFallida(string cotizacion)
    {
        costeo.Folio(cotizacion).Should().NotBeNullOrEmpty();
        costeo.Nexo.FallarRelecturaTrasConexos = true;
    }

    [Given("que Cotifly no tiene guardado el id de la línea de la cotización de Nexo para la línea {int} de {string}")]
    public Task DadoSinIdDeLinea(int linea, string cotizacion) =>
        clsBaseDatosPruebas.EjecutarAsync(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "UPDATE v SET lineaSolicitudNexoId = NULL FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {linea.ToString(CultureInfo.InvariantCulture)}");

    [Given("que {string} creó la licitación {string} del cliente {string} y Nexo costeó todas sus líneas")]
    public async Task DadoLaLicitacionCosteada(string alias, string licitacion, string nit)
    {
        await licitaciones.CuandoCrea(alias, licitacion, nit, 3);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        var folio = licitaciones.Folio(licitacion);
        _licitaciones[licitacion] = folio;
        var solicitud = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT solicitudNexoId FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");
        costeo.Nexo.Costear(solicitud!, 2_550_000m);
        await web.Api.ProcesarPollingAsync(folio);
    }

    [When("{string} deja incluidos en {string} solo los servicios conexos {string}")]
    public async Task CuandoDejaIncluidos(string alias, string cotizacion, string nombres)
    {
        var elegidos = nombres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{Folio(cotizacion)}/costeo", web.Sesion(alias).Access);
        var ids = web.UltimoJson().GetProperty("servicios_conexos").EnumerateArray()
            .Where(s => elegidos.Contains(s.GetProperty("nombre").GetString() ?? string.Empty))
            .Select(s => s.GetProperty("id").GetString())
            .ToList();
        await web.EnviarAsync(HttpMethod.Put, Ruta(cotizacion), web.Sesion(alias).Access, clsApiWebPruebas.Json(new { incluidos = ids }),
            await VersionAsync(cotizacion));
    }

    // --- Verificaciones ---

    [Then("Nexo recibió para la cotización de {string} el conexo {string} con la cantidad {int} en el lineaId de la línea {int} de su cotización y sin valorAplicado")]
    public async Task EntoncesNexoRecibioElConexo(string cotizacion, string servicio, int cantidad, int linea)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        var peticion = costeo.Nexo.PeticionesConexos(costeo.Nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject;
        var item = peticion["items"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        item["servicioConexoId"]!.GetValue<string>().Should().Be(servicio);
        item["cantidad"]!.GetValue<decimal>().Should().Be(cantidad);
        item["lineaId"]!.GetValue<string>().Should().Be(costeo.Nexo.LineaCotizacionDe(solicitud, linea - 1), "la línea de la COTIZACIÓN de Nexo, no la de la solicitud");
        item.ContainsKey("valorAplicado").Should().BeFalse("Nexo calcula el valor: es la fuente de verdad del costo");
    }

    [Then("el servicio conexo {string} de {string} está incluido con el precio {int}")]
    public async Task EntoncesElConexoIncluido(string nombre, string cotizacion, int precio)
    {
        var conexo = (await ConexosAsync(cotizacion)).Single(c => c.GetProperty("nombre").GetString() == nombre);
        conexo.GetProperty("incluido").GetBoolean().Should().BeTrue();
        conexo.GetProperty("precio").GetDecimal().Should().Be(precio);
    }

    [Then("{string} tiene {int} servicios conexos incluidos")]
    [Then("{string} tiene {int} servicio conexo incluido")]
    public async Task EntoncesConexosIncluidos(string cotizacion, int incluidos) =>
        (await ConexosAsync(cotizacion)).Count(c => c.GetProperty("incluido").GetBoolean()).Should().Be(incluidos);

    [Then("Nexo no recibió conexos para la cotización de {string}")]
    public Task EntoncesNexoNoRecibio(string cotizacion) => EntoncesNexoRecibioPeticiones(0, cotizacion);

    [Then("Nexo recibió {int} petición de conexos para la cotización de {string}")]
    public async Task EntoncesNexoRecibioPeticiones(int peticiones, string cotizacion)
    {
        var solicitud = await costeo.Columna(cotizacion, "costeoId");
        var cotizacionNexo = solicitud is null ? null : costeo.Nexo.CotizacionDe(solicitud);
        (cotizacionNexo is null ? [] : costeo.Nexo.PeticionesConexos(cotizacionNexo)).Should().HaveCount(peticiones);
    }

    [Then("el detalle del error tiene agregado_en_nexo {string}")]
    public void EntoncesAgregadoEnNexo(string valor) =>
        web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("agregado_en_nexo").GetBoolean().Should().Be(bool.Parse(valor));

    [Then("el detalle del error tiene el motivo {string}")]
    public void EntoncesElMotivo(string motivo) =>
        web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be(motivo);

    private Task AgregarAsync(string alias, string cotizacion, string cuerpo, string? ifMatch) =>
        web.EnviarAsync(HttpMethod.Post, Ruta(cotizacion), web.Sesion(alias).Access, cuerpo, ifMatch);

    /// <summary>
    /// Conexos guardados, leídos de la base sin depender de la sesión del dueño. HU-021: son filas de la ruta
    /// (<c>CotizacionServiciosConexos</c>): todas están incluidas y el precio es su valor con el margen de la ruta.
    /// </summary>
    private async Task<List<JsonElement>> ConexosAsync(string cotizacion)
    {
        var json = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT s.idServicioConexo AS id, s.nombreServicioConexo AS nombre, CAST(1 AS bit) AS incluido, "
            + "ROUND(s.precio * s.cantidad / (1 - ISNULL(l.margenAplicado, 0)), 0) AS precio "
            + "FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{Folio(cotizacion)}' FOR JSON PATH");
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var documento = JsonDocument.Parse(json);
        return [.. documento.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary><c>If-Match</c> con la versión de negocio vigente (<c>versionNegocio</c>, entre comillas).</summary>
    private async Task<string> VersionAsync(string cotizacion)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{Folio(cotizacion)}'");
        return $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
    }

    private string Folio(string cotizacion) => _licitaciones.TryGetValue(cotizacion, out var folio) ? folio : costeo.Folio(cotizacion);

    private string Ruta(string cotizacion) => $"/web/cotizaciones/{Folio(cotizacion)}/costeo/servicios-conexos";

    private static string Cuerpo(IEnumerable<(int Linea, string Servicio, int Cantidad)> items) =>
        clsApiWebPruebas.Json(new { items = items.Select(i => new { linea_orden = i.Linea, servicio_id = i.Servicio, cantidad = i.Cantidad }).ToArray() });
}
