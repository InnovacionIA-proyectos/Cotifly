using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-013.11 (#108): las filas de <c>POST /web/licitaciones</c> cumplen las reglas de captura del formulario, por HTTP contra SQL
/// Server real y el Nexo falso: códigos de catálogo (G2), reglas condicionales (G3), ids de municipio resueltos sin adivinar (G6),
/// horas por línea (G5) y lo mismo en el primer envío y el reintento.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsLicitacionesCapturaEndpointsTests : IDisposable
{
    private const string Ruta = "/web/licitaciones";
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsApiWebPruebas _api;

    public clsLicitacionesCapturaEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task UnPliegoConFilasValidasEInvalidasDevuelveTodosLosErroresJuntosYNoGuardaNada()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        object[] lineas =
        [
            Valida(),
            Valida() with { tipo_vehiculo = "3s3" },
            Valida() with { tipo_vehiculo = "Tractomula", carroceria = "Furgón" },
            Valida() with { tipologia = "cargaseca" },
            Valida() with { clase_viaje = "Carga Suelta", clase_contenedor = "CNT40.HC" },
            Valida() with { clase_viaje = "Contenedor", clase_contenedor = null },
            Valida() with { valor_aprovisionamiento = 150_000m },
            Valida() with { origen = "La Unión" },
            Valida() with { destino = "Macondo" },
            Valida() with { clase_viaje = "contenedor", clase_contenedor = "CNT20.FT" },
            Valida() with { clase_viaje = "Contenedor", clase_contenedor = "CNT40.OP", carroceria = " PORTACONTENEDORES " },
        ];

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(lineas));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("LICITACION_INVALIDA");
        json.GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("indice").GetInt32(), e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .Should().BeEquivalentTo(new[]
            {
                (1, "tipo_vehiculo", "CODIGO_NO_CATALOGO"),
                (2, "carroceria", "CODIGO_NO_CATALOGO"),
                (2, "tipo_vehiculo", "CODIGO_NO_CATALOGO"),
                (3, "tipologia", "CODIGO_NO_CATALOGO"),
                (4, "clase_contenedor", "CAMPO_NO_APLICA"),
                (5, "clase_contenedor", "CAMPO_REQUERIDO"),
                (6, "valor_aprovisionamiento", "CAMPO_NO_APLICA"),
                (7, "origen", "MUNICIPIO_AMBIGUO"),
                (8, "destino", "MUNICIPIO_NO_ENCONTRADO"),
                (9, "clase_contenedor", "CAMPO_NO_APLICA"),
                (9, "clase_viaje", "CODIGO_NO_CATALOGO"),
            }, "las filas 0 y 10 son válidas (la 10 con la clase provisional CNT40.OP y espacios al borde)");
        json.GetProperty("errores").EnumerateArray().Select(e => e.GetProperty("indice").GetInt32()).Should().BeInAscendingOrder();
        _nexo.SolicitudesCreadas.Should().BeEmpty();
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{sesion.UsuarioId}'")).Should().Be("0");
    }

    [Fact]
    public async Task ElRequestANexoLlevaPorLineaLosIdsDeMunicipioLasHorasYLosCodigosNormalizadosYElReintentoMandaLoMismo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        object[] lineas =
        [
            Valida() with
            {
                origen = " BUENAVENTURA ", tipo_vehiculo = " 3S3 ", clase_viaje = "Contenedor", clase_contenedor = "CNT40.FT",
                carroceria = "PORTACONTENEDORES", tipologia = "Contenedor", horas_cargue = 2m, horas_descargue = 3.5m,
            },
            Valida() with
            {
                origen = "La Unión", origen_municipio_id = 52399, destino = "Cali", ciudad_levante_contenedor = "YUMBO",
                valor_aprovisionamiento = 150_000m,
            },
        ];

        using var envio = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(lineas, horasCargue: 4m, horasDescargue: 6m, notas: " Cita previa "));
        var creada = await VerificarContratoAsync(envio, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        creada.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        var folio = creada.GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);

        var altas = _nexo.Peticiones.Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == "/api/captura-solicitudes").Select(p => p.Cuerpo!).ToList();
        altas.Should().HaveCount(2);
        Canonico(altas[0]).Should().Be(Canonico(altas[1]), "el primer envío y el reintento mandan lo guardado");
        var alta = JsonNode.Parse(altas[1])!;
        alta["notas"]!.GetValue<string>().Should().Be("Cita previa");
        var primera = alta["lineas"]![0]!;
        primera["origen"]!.GetValue<string>().Should().Be("BUENAVENTURA");
        primera["origenMunicipioId"]!.GetValue<int>().Should().Be(76109);
        primera["destinoMunicipioId"]!.GetValue<int>().Should().Be(11001);
        primera["tipoVehiculo"]!.GetValue<string>().Should().Be("3S3");
        primera["claseContenedor"]!.GetValue<string>().Should().Be("CNT40.FT");
        primera["horasCargue"]!.GetValue<decimal>().Should().Be(2m);
        primera["horasDescargue"]!.GetValue<decimal>().Should().Be(3.5m);
        var segunda = alta["lineas"]![1]!;
        segunda["origenMunicipioId"]!.GetValue<int>().Should().Be(52399, "el id que eligió el frontend se respeta");
        segunda["destinoMunicipioId"]!.GetValue<int>().Should().Be(76001);
        segunda["horasCargue"]!.GetValue<decimal>().Should().Be(4m, "sin horas propias viajan las del encabezado");
        segunda["horasDescargue"]!.GetValue<decimal>().Should().Be(6m);
        segunda["valorAprovisionamiento"]!.GetValue<decimal>().Should().Be(150_000m);

        (await EscalarAsync("SELECT STRING_AGG(CONCAT(l.origenMunicipioId, '|', l.destinoMunicipioId, '|', l.horasCargue, '|', l.horasDescargue), ';') WITHIN GROUP (ORDER BY l.orden) "
            + $"FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("76109|11001|2.00|3.50;52399|76001|4.00|6.00", "HU-021: la cabecera no guarda horas; la ruta guarda las que viajan");
    }

    [Fact]
    public async Task SinCatalogoMaestroDeNexoEs503YNoSeGuardaNada()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.CatalogosCaidos = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo([Valida()]));

        respuesta.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(respuesta, "CATALOGOS_FLETE_NO_DISPONIBLE");
        _nexo.SolicitudesCreadas.Should().BeEmpty();
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{sesion.UsuarioId}'")).Should().Be("0");
    }

    [Fact]
    public async Task SinCatalogoDeMunicipiosNoSeGuardaSalvoQueLasFilasTraiganSusIds()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.MunicipiosFalla = HttpStatusCode.ServiceUnavailable;

        using var sinIds = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo([Valida()]));
        sinIds.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "igual que CATALOGOS_FLETE_NO_DISPONIBLE");
        await VerificarErrorNegocioAsync(sinIds, "MUNICIPIOS_NO_DISPONIBLE");
        _nexo.SolicitudesCreadas.Should().BeEmpty();

        using var conIds = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access,
            Cuerpo([Valida() with { origen_municipio_id = 76109, destino_municipio_id = 11001 }]));
        await VerificarContratoAsync(conIds, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        _nexo.UltimaAlta!["lineas"]![0]!["origenMunicipioId"]!.GetValue<int>().Should().Be(76109);
    }

    [Fact]
    public async Task ConElCatalogoMaestroCaidoSeReportanIgualLosErroresQueNoDependenDeNexo()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.CatalogosCaidos = HttpStatusCode.ServiceUnavailable;

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access,
            Cuerpo([Valida(), Valida() with { tipologia = "carga seca", clase_contenedor = "CNT40.HC" }]));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("CATALOGOS_FLETE_NO_DISPONIBLE");
        json.GetProperty("errores").EnumerateArray().Select(e => (e.GetProperty("indice").GetInt32(), e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .Should().BeEquivalentTo(new[] { (1, "clase_contenedor", "CAMPO_NO_APLICA"), (1, "tipologia", "CODIGO_NO_CATALOGO") });
        _nexo.SolicitudesCreadas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnaLicitacionGuardadaConTextoLibreNoSeReintentaYPideUnaCargaNueva()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo([Valida(), Valida()]));
        var folio = (await VerificarContratoAsync(envio, Ruta, HttpMethod.Post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        var altasAntes = Altas().Count;

        // Como una licitación guardada antes de #108: texto libre y sin ids de municipio.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE l SET origenMunicipioId = NULL FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.orden = 2; "
            + "UPDATE v SET tipoVehiculo = N'Tractomula' FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND l.orden = 2");

        using var reintento = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);

        var json = await VerificarContratoAsync(reintento, "/web/cotizaciones/{folio}/costeo/reintentar", HttpMethod.Post, HttpStatusCode.BadRequest);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("LICITACION_INVALIDA");
        json.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Be("LICITACION_REQUIERE_NUEVA_CARGA");
        json.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("POST /web/licitaciones");
        var error = json.GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray().Should().ContainSingle().Subject;
        (error.GetProperty("indice").GetInt32(), error.GetProperty("campo").GetString(), error.GetProperty("codigo").GetString())
            .Should().Be((1, "tipo_vehiculo", "CODIGO_NO_CATALOGO"));
        Altas().Should().HaveCount(altasAntes, "nunca llega texto libre a Nexo");
    }

    [Fact]
    public async Task ElReintentoDeUnaLicitacionSinIdsDeMunicipioLosResuelveLosEnviaYLosGuarda()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo([Valida()]));
        var folio = (await VerificarContratoAsync(envio, Ruta, HttpMethod.Post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.AltaFalla = null;
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE l SET origenMunicipioId = NULL, destinoMunicipioId = NULL FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

        using var reintento = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);

        reintento.StatusCode.Should().Be(HttpStatusCode.OK);
        var linea = _nexo.UltimaAlta!["lineas"]![0]!;
        (linea["origenMunicipioId"]!.GetValue<int>(), linea["destinoMunicipioId"]!.GetValue<int>()).Should().Be((76109, 11001));
        (await EscalarAsync("SELECT CONCAT(l.origenMunicipioId, '|', l.destinoMunicipioId) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'"))
            .Should().Be("76109|11001", "el siguiente reenvío manda lo mismo");
    }

    private List<string> Altas() =>
        [.. _nexo.Peticiones.Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == "/api/captura-solicitudes").Select(p => p.Cuerpo!)];

    /// <summary>Fila válida del pliego (snake_case de <c>LineaLicitacionWeb</c>); los nulos no se serializan.</summary>
    private static clsFila Valida() => new();

    private static string Cuerpo(object[] lineas, decimal? horasCargue = null, decimal? horasDescargue = null, string? notas = null) =>
        JsonSerializer.Serialize(new
        {
            nit_cliente = clsCosteoEndpointsTests.Nit,
            cliente_nombre = "Agencia de Aduanas JF",
            linea_negocio = "Nacional",
            referencia = "Pliego 2026-04",
            horas_cargue = horasCargue,
            horas_descargue = horasDescargue,
            notas,
            lineas,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    private static string Canonico(string json)
    {
        static JsonNode? Normalizar(JsonNode? nodo) => nodo switch
        {
            JsonObject o => new JsonObject(o.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Normalizar(p.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Normalizar)]),
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(v.GetValue<decimal>() / 1.000000000000000000000000000000000m),
            _ => nodo?.DeepClone(),
        };

        return Normalizar(JsonNode.Parse(json))!.ToJsonString();
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }

#pragma warning disable IDE1006, SA1300 // snake_case: son los nombres del contrato.
    private sealed record clsFila
    {
        public string origen { get; init; } = "BUENAVENTURA";
        public string destino { get; init; } = "BOGOTA, D.C.";
        public int? origen_municipio_id { get; init; }
        public int? destino_municipio_id { get; init; }
        public string? tipo_vehiculo { get; init; } = "3S3";
        public string? carroceria { get; init; }
        public string? tipologia { get; init; } = "CargaSeca";
        public string? clase_viaje { get; init; }
        public string? clase_contenedor { get; init; }
        public string? ciudad_levante_contenedor { get; init; }
        public decimal? valor_aprovisionamiento { get; init; }
        public decimal? horas_cargue { get; init; }
        public decimal? horas_descargue { get; init; }
        public decimal peso_kg { get; init; } = 28_000m;
    }
#pragma warning restore IDE1006, SA1300
}
