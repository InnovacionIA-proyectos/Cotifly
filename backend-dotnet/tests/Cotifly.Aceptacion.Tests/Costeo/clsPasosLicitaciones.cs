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
/// Pasos de <c>specs/features/licitaciones_excel.feature</c> (HU-011.2, ADR-017). Reutiliza el Nexo falso de
/// <see cref="clsPasosCosteo"/> (mismo escenario) y el pliego de <see cref="clsPliegoMuestra"/>.
/// </summary>
[Binding]
public sealed class clsPasosLicitaciones(clsContextoWeb web, clsPasosCosteo costeo)
{
    private readonly Dictionary<string, string> _folios = new(StringComparer.OrdinalIgnoreCase);

    [When("{string} analiza el pliego de {int} filas")]
    public Task CuandoAnaliza(string alias, int filas) =>
        web.EnviarAsync(HttpMethod.Post, "/web/licitaciones/analizar-excel", web.Sesion(alias).Access, clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel(filas)));

    [Then("Nexo recibió el archivo para analizarlo")]
    public void EntoncesNexoRecibioElArchivo() => costeo.Nexo.UltimoArchivoAnalizado.Should().BeGreaterThan(0);

    [When("{string} crea la licitación {string} del cliente {string} con el pliego de {int} filas mapeadas")]
    public Task CuandoCrea(string alias, string licitacion, string nit, int filas) =>
        CrearAsync(alias, licitacion, clsPliegoMuestra.Json(nit, clsPliegoMuestra.Lineas(filas)), clsPliegoMuestra.Excel(filas));

    [When("{string} crea la licitación {string} en {string} del cliente {string} con el pliego de {int} filas mapeadas")]
    public Task CuandoCreaEnMoneda(string alias, string licitacion, string moneda, string nit, int filas) =>
        CrearAsync(alias, licitacion, clsPliegoMuestra.Json(nit, clsPliegoMuestra.Lineas(filas), moneda: moneda), clsPliegoMuestra.Excel(filas));

    [Then("la licitación {string} está en {string} con la TRM {string}")]
    public async Task EntoncesMonedaYTrm(string licitacion, string moneda, string trm)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Folio(licitacion)}", web.Sesion("ana").Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var vista = web.UltimoJson();
        vista.GetProperty("moneda").GetString().Should().Be(moneda);
        vista.GetProperty("trm_aplicada").GetDecimal().Should().Be(decimal.Parse(trm, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Then("cada línea con precio de {string} vale {string}")]
    public async Task EntoncesCadaLineaVale(string licitacion, string precio)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Folio(licitacion)}", web.Sesion("ana").Access);
        var lineas = web.UltimoJson().GetProperty("lineas").EnumerateArray()
            .Where(l => l.GetProperty("precio_final").ValueKind != JsonValueKind.Null)
            .Select(l => l.GetProperty("precio_final").GetDecimal())
            .ToList();
        lineas.Should().NotBeEmpty().And.OnlyContain(p => p == decimal.Parse(precio, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Then("el PDF del pliego contiene {string}")]
    public void EntoncesPdfContiene(string texto) => clsLectorPdf.Leer(web.UltimoContenido).Texto.Should().Contain(texto);

    [When("{string} crea la licitación {string} del cliente {string} con una fila sin destino y otra sin peso")]
    public Task CuandoCreaInvalida(string alias, string licitacion, string nit)
    {
        object[] lineas =
        [
            new { fila = 0, origen = "BUENAVENTURA", destino = "", peso_kg = 32000 },
            new { fila = 1, origen = "CALI", destino = "CARTAGENA", peso_kg = 28000 },
            new { fila = 2, origen = "CUCUTA", destino = "BOGOTA, D.C." },
        ];
        return CrearAsync(alias, licitacion, clsPliegoMuestra.Json(nit, lineas), clsPliegoMuestra.Excel(3));
    }

    [Then("la respuesta tiene el estado {int} con el reporte de la licitación")]
    public void EntoncesElReporte(int estado)
    {
        web.UltimoEstado.Should().Be((HttpStatusCode)estado, web.UltimoCuerpo);
        web.UltimoJson().GetProperty("error").GetProperty("codigo").GetString().Should().Be("LICITACION_INVALIDA");
    }

    [Then("el reporte trae el código {string} en la línea {int} y el código {string} en la línea {int}")]
    public void EntoncesElReporteTrae(string codigo1, int linea1, string codigo2, int linea2)
    {
        var errores = web.UltimoJson().GetProperty("errores").EnumerateArray()
            .Select(e => (Indice: e.GetProperty("indice").ValueKind == JsonValueKind.Null ? -1 : e.GetProperty("indice").GetInt32(), Codigo: e.GetProperty("codigo").GetString()))
            .ToList();
        errores.Should().Contain((linea1, codigo1)).And.Contain((linea2, codigo2));
    }

    [Then("Nexo no recibió ninguna solicitud")]
    public void EntoncesNexoNoRecibio() => costeo.Nexo.SolicitudesCreadas.Should().BeEmpty();

    [Then("Nexo recibió una solicitud de licitación con {int} líneas y el BP {string}")]
    public void EntoncesNexoRecibioLicitacion(int lineas, string bp)
    {
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        alta["clienteBp"]!.GetValue<string>().Should().Be(bp);
        alta["tipo"]!.GetValue<string>().Should().Be("licitacion");
        alta["lineas"]!.AsArray().Should().HaveCount(lineas);
    }

    [Then("la licitación {string} tiene {int} líneas en el estado de costeo {string}")]
    public async Task EntoncesLineasEnEstado(string licitacion, int lineas, string estado) =>
        (await LineasAsync(licitacion, $"l.estadoCosteo = '{estado}'")).Should().Be(lineas);

    [Then("el Excel original de {string} quedó guardado")]
    public async Task EntoncesExcelGuardado(string licitacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionArchivosLicitacion a JOIN dbo_cotifly.Cotizaciones c ON c.id = a.cotizacionId WHERE c.folio = '{Folio(licitacion)}' AND a.hoja = N'{clsPliegoMuestra.Hoja}'"))
            .Should().Be(1);

    [When("Nexo costea las líneas {int} y {int} de {string} con un flete de {int}")]
    public async Task CuandoCosteaDos(int a, int b, string licitacion, int flete)
    {
        var id = await CosteoIdAsync(licitacion);
        costeo.Nexo.CostearLinea(id, a - 1, flete);
        costeo.Nexo.CostearLinea(id, b - 1, flete);
    }

    [When("Nexo costea las líneas {int} de {string} con un flete de {int}")]
    public async Task CuandoCosteaUna(int a, string licitacion, int flete) => costeo.Nexo.CostearLinea(await CosteoIdAsync(licitacion), a - 1, flete);

    [When("el polling consulta a Nexo por {string}")]
    public Task CuandoPolling(string licitacion) => web.Api.ProcesarPollingAsync(Folio(licitacion));

    [Then("la licitación {string} queda en el estado de costeo {string} con {int} líneas con precio")]
    public async Task EntoncesEstadoYPrecios(string licitacion, string estado, int conPrecio)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Folio(licitacion)}", web.Sesion("ana").Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var vista = web.UltimoJson();
        vista.GetProperty("estado_costeo").GetString().Should().Be(estado);
        vista.GetProperty("lineas_costeadas").GetInt32().Should().Be(conPrecio);
        web.UltimoCuerpo.Should().NotContain("\"costo\"", "el costo de Nexo nunca sale al comercial");
    }

    [Then("el precio final de {string} es la suma de sus líneas")]
    public void EntoncesSuma(string licitacion)
    {
        var vista = web.UltimoJson();
        var suma = vista.GetProperty("lineas").EnumerateArray().Sum(l => l.GetProperty("precio_final").GetDecimal());
        vista.GetProperty("precio_final").GetDecimal().Should().Be(suma, licitacion);
    }

    [Then("la licitación {string} queda en el estado {string}")]
    public async Task EntoncesEstado(string licitacion, string estado) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT estadoCosteo FROM dbo_cotifly.Cotizaciones WHERE folio = '{Folio(licitacion)}'")).Should().Be(estado);

    [When("{string} reintenta el envío de la licitación {string}")]
    public async Task CuandoReintenta(string alias, string licitacion)
    {
        // El reintento de un pendiente_envio exige 2 minutos; uno en error_envio va de inmediato (ADR-015 §3).
        await web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{Folio(licitacion)}/costeo/reintentar", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [When("{string} exporta el Excel de la licitación {string}")]
    public async Task CuandoExporta(string alias, string licitacion)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Folio(licitacion)}/excel", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
    }

    [Then("el Excel conserva las columnas originales del pliego")]
    public void EntoncesConservaElPliego()
    {
        using var original = new ClosedXML.Excel.XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        using var exportado = Libro();
        foreach (var celda in original.Worksheet(clsPliegoMuestra.Hoja).RangeUsed()!.Cells())
        {
            exportado.Worksheet(clsPliegoMuestra.Hoja).Cell(celda.Address.RowNumber, celda.Address.ColumnNumber).GetFormattedString()
                .Should().Be(celda.GetFormattedString());
        }
    }

    [Then("el Excel trae el precio final de las líneas {int} y {int} y marca la línea {int} como {string}")]
    public void EntoncesPrecios(int a, int b, int c, string marca)
    {
        using var libro = Libro();
        var hoja = libro.Worksheet(clsPliegoMuestra.Hoja);
        hoja.Cell(clsPliegoMuestra.FilaExcel(a - 1), 7).GetValue<decimal>().Should().BePositive();
        hoja.Cell(clsPliegoMuestra.FilaExcel(b - 1), 7).GetValue<decimal>().Should().BePositive();
        hoja.Cell(clsPliegoMuestra.FilaExcel(c - 1), 7).GetString().Should().Be(marca);
    }

    [Then("el Excel no contiene el costo {int} de Nexo")]
    public void EntoncesSinCosto(int costo)
    {
        using var libro = Libro();
        libro.Worksheets.SelectMany(h => h.CellsUsed())
            .Where(c => c.DataType == ClosedXML.Excel.XLDataType.Number)
            .Select(c => c.GetValue<decimal>())
            .Should().NotContain(costo);
    }

    [When("{string} genera el PDF de la licitación {string}")]
    public async Task CuandoGeneraPdf(string alias, string licitacion)
    {
        await web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{Folio(licitacion)}/costeo/pdf", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        var ruta = new Uri(new Uri("http://localhost"), web.UltimoJson().GetProperty("pdf_url").GetString()!).AbsolutePath;
        await web.EnviarAsync(HttpMethod.Get, ruta, web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
    }

    [Then("el PDF del pliego trae el resumen de {int} rutas con su total")]
    public void EntoncesResumenPdf(int rutas) =>
        clsLectorPdf.Leer(web.UltimoContenido).Texto.Should().Contain("RESUMEN DEL PLIEGO").And.Contain($"TOTAL DEL PLIEGO ({rutas} RUTAS, SIN SERVICIOS CONEXOS)");

    [Then("el PDF del pliego no contiene el costo {int} de Nexo")]
    public void EntoncesPdfSinCosto(int costo) =>
        clsLectorPdf.Leer(web.UltimoContenido).Texto.Should().NotContain(
            "$" + costo.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", ".", StringComparison.Ordinal));

    // HU-013.11 (#108): reglas de captura por fila.
    private static readonly string[] _columnasNumericas = ["valor_aprovisionamiento", "horas_cargue", "horas_descargue"];
    private static readonly string[] _columnasEnteras = ["origen_municipio_id", "destino_municipio_id"];

    [Given("que el catálogo de municipios de Nexo no responde")]
    public void DadoMunicipiosCaidos() => costeo.Nexo.MunicipiosFalla = HttpStatusCode.ServiceUnavailable;

    [When("{string} crea la licitación {string} del cliente {string} con estas filas:")]
    public Task CuandoCreaConFilas(string alias, string licitacion, string nit, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        object[] lineas = [.. tabla.Rows.Select(fila =>
        {
            var linea = new Dictionary<string, object?> { ["peso_kg"] = 28_000m };
            foreach (var (columna, valor) in fila.Where(c => !string.IsNullOrWhiteSpace(c.Value)))
            {
                linea[columna] = _columnasNumericas.Contains(columna) ? decimal.Parse(valor, CultureInfo.InvariantCulture)
                    : _columnasEnteras.Contains(columna) ? (object)int.Parse(valor, CultureInfo.InvariantCulture)
                    : valor;
            }

            return (object)linea;
        })];
        return CrearAsync(alias, licitacion, clsPliegoMuestra.Json(nit, lineas, hoja: null), null);
    }

    [Then("el reporte trae exactamente estos errores:")]
    public void EntoncesElReporteTraeExactamente(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var errores = web.UltimoJson().GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("indice").GetInt32(), e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .ToList();
        errores.Should().BeEquivalentTo(tabla.Rows.Select(f => (int.Parse(f["linea"], CultureInfo.InvariantCulture), f["campo"], f["codigo"])));
    }

    [Then("la línea {int} que recibió Nexo trae {string} igual a {string}")]
    public void EntoncesLaLineaTrae(int linea, string campo, string valor) =>
        LineaDeLaUltimaAlta(linea)[campo]!.GetValue<string>().Should().Be(valor);

    [Then(@"^la línea (\d+) que recibió Nexo trae los municipios (\d+) y (\d+) con ([\d.]+) horas de cargue y ([\d.]+) de descargue$")]
    public void EntoncesLaLineaTraeMunicipiosYHoras(int linea, int origen, int destino, string cargue, string descargue)
    {
        var nodo = LineaDeLaUltimaAlta(linea);
        nodo["origenMunicipioId"]!.GetValue<int>().Should().Be(origen);
        nodo["destinoMunicipioId"]!.GetValue<int>().Should().Be(destino);
        nodo["horasCargue"]!.GetValue<decimal>().Should().Be(decimal.Parse(cargue, CultureInfo.InvariantCulture));
        nodo["horasDescargue"]!.GetValue<decimal>().Should().Be(decimal.Parse(descargue, CultureInfo.InvariantCulture));
    }

    [Then("la línea {int} que recibió Nexo trae los municipios {int} y {int} sin horas propias")]
    public void EntoncesLaLineaTraeMunicipiosSinHoras(int linea, int origen, int destino)
    {
        var nodo = LineaDeLaUltimaAlta(linea);
        nodo["origenMunicipioId"]!.GetValue<int>().Should().Be(origen);
        nodo["destinoMunicipioId"]!.GetValue<int>().Should().Be(destino);
        nodo["horasCargue"].Should().BeNull("sin horas en la fila ni en el encabezado no viajan");
        nodo["horasDescargue"].Should().BeNull();
    }

    [Then("el primer envío y el reintento mandaron a Nexo la misma solicitud")]
    public void EntoncesMismaSolicitud()
    {
        var altas = costeo.Nexo.Peticiones
            .Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == "/api/captura-solicitudes")
            .Select(p => Canonico(p.Cuerpo!))
            .ToList();
        altas.Should().HaveCount(2).And.OnlyContain(a => a == altas[0], "el reintento reenvía lo guardado, normalizado");
    }

    private System.Text.Json.Nodes.JsonNode LineaDeLaUltimaAlta(int linea) =>
        (costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta."))["lineas"]![linea - 1]!;

    /// <summary>JSON con los números sin ceros decimales a la derecha (la escala de un DECIMAL releído no es contenido).</summary>
    private static string Canonico(string json)
    {
        static System.Text.Json.Nodes.JsonNode? Normalizar(System.Text.Json.Nodes.JsonNode? nodo) => nodo switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(
                o.Select(p => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(p.Key, Normalizar(p.Value)))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray([.. a.Select(Normalizar)]),
            System.Text.Json.Nodes.JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
                System.Text.Json.Nodes.JsonValue.Create(v.GetValue<decimal>() / 1.000000000000000000000000000000000m),
            _ => nodo?.DeepClone(),
        };

        return Normalizar(System.Text.Json.Nodes.JsonNode.Parse(json))!.ToJsonString();
    }

    private ClosedXML.Excel.XLWorkbook Libro() => new(new MemoryStream(web.UltimoContenido));

    private async Task CrearAsync(string alias, string licitacion, string json, byte[]? archivo)
    {
        await web.EnviarAsync(HttpMethod.Post, "/web/licitaciones", web.Sesion(alias).Access, clsApiWebPruebas.Licitacion(json, archivo));
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            _folios[licitacion] = web.UltimoJson().GetProperty("folio").GetString()!;
        }
    }

    /// <summary>Folio real de la licitación del escenario (lo usan también los pasos de servicios conexos, HU-013.3).</summary>
    public string Folio(string licitacion) =>
        _folios.TryGetValue(licitacion, out var folio) ? folio : throw new InvalidOperationException($"No se creó la licitación '{licitacion}'.");

    private async Task<string> CosteoIdAsync(string licitacion) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT costeoId FROM dbo_cotifly.Cotizaciones WHERE folio = '{Folio(licitacion)}'"))!;

    private Task<int> LineasAsync(string licitacion, string condicion) =>
        clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.Cotizaciones c ON c.id = l.cotizacionId WHERE c.folio = '{Folio(licitacion)}' AND {condicion}");
}
