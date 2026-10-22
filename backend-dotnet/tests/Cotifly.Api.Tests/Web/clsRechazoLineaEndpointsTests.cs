using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Rechazo de una línea en la Mesa de Costeo de Nexo (HU-028.1, #183, Nexo #320) sobre el formulario web por rutas, de punta a punta: envío
/// por HTTP, rechazo en el Nexo falso, una vuelta del polling y <c>GET .../costeo</c> validado contra el contrato OpenAPI, con SQL Server
/// real. La ruta rechazada no tiene precio ni suma en el total, no admite margen (409 <c>RUTA_RECHAZADA</c>) y, con todas rechazadas, el
/// PDF se bloquea (409 <c>SIN_RUTAS_COTIZABLES</c>).
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsRechazoLineaEndpointsTests : IDisposable
{
    private const string Motivo = "Sin operación en la ruta";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsApiWebPruebas _api;

    public clsRechazoLineaEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task TrasElPollingLaRutaRechazadaSaleSinPrecioYElTotalLaExcluye()
    {
        var (sesion, folio) = await EnviarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.RechazarLinea(solicitud, 1, Motivo);

        await _api.ProcesarPollingAsync(folio);

        var (costeo, _) = await CosteoAsync(sesion, folio);
        costeo.GetProperty("estado_costeo").GetString().Should().Be("autocosteo", "la ruta rechazada no deja la cotización pendiente");
        var rutas = Rutas(costeo);
        rutas[0].GetProperty("estado_costeo").GetString().Should().NotBe("rechazada");
        var precioRuta1 = rutas[0].GetProperty("precio_final_linea").GetDecimal();
        precioRuta1.Should().BePositive();
        rutas[1].GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        rutas[1].GetProperty("motivo_rechazo").GetString().Should().Be(Motivo);
        rutas[1].GetProperty("precio_final_linea").ValueKind.Should().Be(JsonValueKind.Null);
        var camion = rutas[1].GetProperty("camiones").EnumerateArray().Should().ContainSingle().Subject;
        camion.GetProperty("estado_costeo").GetString().Should().Be("rechazada");
        camion.GetProperty("motivo_rechazo").GetString().Should().Be(Motivo);
        costeo.GetProperty("precio_final").GetDecimal().Should().Be(precioRuta1, "el total solo suma las rutas con precio");
    }

    [Fact]
    public async Task UnAjusteDeMargenSobreLaRutaRechazadaEs409RutaRechazadaYNoSeRegistra()
    {
        var (sesion, folio) = await EnviarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.RechazarLinea(solicitud, 1, Motivo);
        await _api.ProcesarPollingAsync(folio);
        var (_, etag) = await CosteoAsync(sesion, folio);

        using var ajuste = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            """{"margen":0.20,"linea_orden":2}""", etag);

        ajuste.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(ajuste, "RUTA_RECHAZADA");
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{folio}'")).Should().Be(0);

        using var otraRuta = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            """{"margen":0.20,"linea_orden":1}""", etag);
        await VerificarContratoAsync(otraRuta, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConTodasLasRutasRechazadasYLaCotizacionEntregadaElPdfSeBloquea()
    {
        var (sesion, folio) = await EnviarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.RechazarLinea(solicitud, 0, Motivo);
        _nexo.RechazarLinea(solicitud, 1, Motivo);
        _nexo.Entregar(solicitud);

        await _api.ProcesarPollingAsync(folio);

        var (costeo, _) = await CosteoAsync(sesion, folio);
        costeo.GetProperty("estado_costeo").GetString().Should().Be("entregada");
        costeo.GetProperty("precio_final").ValueKind.Should().Be(JsonValueKind.Null);
        Rutas(costeo).Should().OnlyContain(r => r.GetProperty("estado_costeo").GetString() == "rechazada");

        using var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", sesion.Access);
        pdf.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(pdf, "SIN_RUTAS_COTIZABLES");
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> EnviarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        _nexo.LineasDe(_nexo.SolicitudesCreadas[^1]).Should().HaveCount(2, "un camión por ruta");
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    private async Task<(JsonElement Costeo, string? Etag)> CosteoAsync(clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return (json, respuesta.Headers.ETag?.Tag);
    }

    private static List<JsonElement> Rutas(JsonElement costeo) =>
        [.. costeo.GetProperty("rutas").EnumerateArray().OrderBy(r => r.GetProperty("linea_orden").GetInt32())];

    /// <summary>Dos rutas de la misma línea de negocio (Importacion), un camión cada una, sin conexos.</summary>
    private static string Formulario() => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Importacion",
        deadline = "2026-10-15T17:00:00-05:00",
        horas_cargue = 4,
        horas_descargue = 6,
        rutas = new object[] { Ruta(76109, "Buenaventura", 11001, "Bogotá D.C.", 28000), Ruta(5001, "Medellín", 13001, "Cartagena", 12500) },
    });

    private static object Ruta(int origenId, string origen, int destinoId, string destino, decimal peso) => new
    {
        origen_municipio_id = origenId,
        origen,
        destino_municipio_id = destinoId,
        destino,
        tipologia = "Contenedor",
        clase_viaje = "Contenedor",
        clase_contenedor = "CNT40.HC",
        exclusivo = true,
        consolidado = false,
        retorna_contenedor = false,
        levante_contenedor_diferente = false,
        peso_kg = peso,
        valor_mercancia = 90000000,
        mercancias = new object[]
        {
            new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = peso / 10 },
        },
    };

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
