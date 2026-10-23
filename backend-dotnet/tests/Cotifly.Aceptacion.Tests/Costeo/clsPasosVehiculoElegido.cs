using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/vehiculo_elegido.feature</c> (HU-032, #197, ADR-022): el vehículo elegido como respaldo del cubicador,
/// contra la API real, SQL Server en Testcontainers, el Nexo falso de <see cref="clsPasosCosteo"/> y el cubicador falso de
/// <see cref="clsPasosCubicador"/>.
/// </summary>
[Binding]
public sealed class clsPasosVehiculoElegido(clsContextoWeb web, clsPasosCosteo costeo)
{
    private static readonly HashSet<string> _numericas = new(StringComparer.Ordinal) { "cantidad", "peso_kg", "largo_cm", "ancho_cm", "alto_cm" };

    private static string Cadena => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [When("{string} envía a costeo la ruta {string} de {string} con el vehículo {string} y {int} kg sin mercancías")]
    public Task CuandoEnviaSoloPeso(string alias, string cotizacion, string clase, string vehiculo, int peso) =>
        EnviarAsync(alias, cotizacion, Ruta(clase, vehiculo, peso, []));

    [When("{string} envía a costeo la ruta {string} de {string} sin vehículo y {int} kg sin mercancías")]
    public Task CuandoEnviaSinVehiculo(string alias, string cotizacion, string clase, int peso) =>
        EnviarAsync(alias, cotizacion, Ruta(clase, null, peso, []));

    [When("{string} envía a costeo la ruta {string} de {string} con el vehículo {string} y estas mercancías:")]
    public Task CuandoEnviaConMercancias(string alias, string cotizacion, string clase, string vehiculo, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var mercancias = tabla.Rows.Select(fila =>
        {
            var mercancia = new JsonObject();
            foreach (var (columna, valor) in fila.Where(c => !string.IsNullOrWhiteSpace(c.Value)))
            {
                mercancia[columna] = _numericas.Contains(columna)
                    ? JsonValue.Create(decimal.Parse(valor, CultureInfo.InvariantCulture))
                    : JsonValue.Create(valor);
            }

            return (JsonNode)mercancia;
        }).ToArray();
        return EnviarAsync(alias, cotizacion, Ruta(clase, vehiculo, null, mercancias));
    }

    [When("{string} envía a costeo la ruta {string} de {string} con el vehículo {string} y una mercancía cubicable de {int} kg")]
    public Task CuandoEnviaCubicable(string alias, string cotizacion, string clase, string vehiculo, int peso) =>
        EnviarAsync(alias, cotizacion, Ruta(clase, vehiculo, null,
        [
            new JsonObject
            {
                ["codigo_mercancia"] = clsPasosCosteo.CodigoMercancia, ["nombre_mercancia"] = "Café", ["cantidad"] = 1,
                ["largo_cm"] = 120, ["ancho_cm"] = 100, ["alto_cm"] = 150, ["peso_kg"] = peso,
            },
        ]));

    [Given("{string} envió a costeo la ruta {string} de {string} con el vehículo {string} y {int} kg sin mercancías y Nexo no la recibió")]
    public async Task DadoEnvioSinNexo(string alias, string cotizacion, string clase, string vehiculo, int peso)
    {
        costeo.Nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        await EnviarAsync(alias, cotizacion, Ruta(clase, vehiculo, peso, []));
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        costeo.Nexo.AltaFalla = null;
    }

    [When("{string} edita la solicitud {string} cambiando solo el vehículo a {string}")]
    public async Task CuandoEditaElVehiculo(string alias, string cotizacion, string vehiculo)
    {
        var folio = costeo.Folio(cotizacion);
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/solicitud", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var etag = web.UltimoEtag ?? throw new InvalidOperationException("GET .../solicitud no trajo ETag.");
        var modelo = JsonNode.Parse(web.UltimoCuerpo)!;
        modelo["modo_esperado"] = modelo["edicion"]!["modo"]!.GetValue<string>();
        modelo["rutas"]![0]!["tipo_vehiculo"] = vehiculo;
        await web.EnviarAsync(HttpMethod.Put, $"/web/cotizaciones/{folio}/solicitud", web.Sesion(alias).Access, modelo.ToJsonString(), etag);
    }

    [Then("la edición de {string} no es {string} y replanificó la ruta {int}")]
    public void EntoncesLaEdicionReplanifico(string cotizacion, string sinCambios, int ruta)
    {
        _ = sinCambios;
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var json = web.UltimoJson();
        json.GetProperty("folio").GetString().Should().Be(costeo.Folio(cotizacion));
        json.GetProperty("sin_cambios").GetBoolean().Should().BeFalse("cambiar solo el vehículo elegido de una ruta sin mercancías cubicables es un cambio");
        json.GetProperty("rutas_replanificadas").EnumerateArray().Select(r => r.GetInt32()).Should().Contain(ruta);
    }

    [Then("la ruta {int} de {string} tiene un camión con el vehículo {string} y guarda el vehículo elegido {string}")]
    public async Task EntoncesCamionYElegido(int orden, string cotizacion, string vehiculo, string elegido) =>
        (await CamionYElegidoAsync(orden, cotizacion)).Should().Be($"1|{vehiculo}|{elegido}");

    [Then("la ruta {int} de {string} tiene un camión sin vehículo y guarda el vehículo elegido {string}")]
    public async Task EntoncesCamionSinVehiculo(int orden, string cotizacion, string elegido) =>
        (await CamionYElegidoAsync(orden, cotizacion)).Should().Be($"1||{elegido}");

    [Then("Nexo recibió una línea con el vehículo {string}, el peso {int} y sin posiciones")]
    public void EntoncesNexoRecibio(string vehiculo, int peso)
    {
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        var linea = alta["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["tipoVehiculo"]!.GetValue<string>().Should().Be(vehiculo);
        linea["peso"]!.GetValue<decimal>().Should().Be(peso);
        linea["posiciones"].Should().BeNull("las mercancías sin medidas no viajan a Nexo como posiciones");
    }

    [Then("la ruta {int} de {string} guarda {int} mercancía sin medidas")]
    public async Task EntoncesMercanciaSinMedidas(int orden, string cotizacion, int mercancias) =>
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden} AND m.largoCm IS NULL AND m.anchoCm IS NULL AND m.altoCm IS NULL"))
            .Should().Be(mercancias);

    [Then("la respuesta avisa {string} en la ruta {int}")]
    public void EntoncesLaRespuestaAvisa(string codigo, int orden)
    {
        var ruta = web.UltimoJson().GetProperty("cubicacion").EnumerateArray().Single(r => r.GetProperty("linea_orden").GetInt32() == orden);
        var aviso = ruta.GetProperty("advertencias").EnumerateArray().Should().ContainSingle(a => a.GetProperty("codigo").GetString() == codigo).Subject;
        aviso.GetProperty("mensaje").GetString().Should().NotBeNullOrWhiteSpace();
        aviso.GetProperty("vehiculo_elegido").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        aviso.GetProperty("vehiculos_finales").GetArrayLength().Should().BeGreaterThan(0);
    }

    private async Task<string?> CamionYElegidoAsync(int orden, string cotizacion) =>
        await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT CONCAT(COUNT(v.id), '|', MAX(v.tipoVehiculo), '|', MAX(l.tipoVehiculoElegido)) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + "LEFT JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}");

    private static JsonObject Ruta(string clase, string? vehiculo, int? peso, JsonNode[] mercancias)
    {
        var ruta = new JsonObject
        {
            ["linea_negocio"] = "Importacion",
            ["origen_municipio_id"] = 76109,
            ["origen"] = "Buenaventura",
            ["destino_municipio_id"] = 11001,
            ["destino"] = "Bogotá",
            ["tipologia"] = "CargaSeca",
            ["clase_viaje"] = clase,
            ["valor_mercancia"] = 50_000_000m,
        };
        if (clase == "Contenedor")
        {
            ruta["clase_contenedor"] = "CNT40.HC";
        }

        if (vehiculo is not null)
        {
            ruta["tipo_vehiculo"] = vehiculo;
        }

        if (peso is not null)
        {
            ruta["peso_kg"] = peso.Value;
        }

        if (mercancias.Length > 0)
        {
            ruta["mercancias"] = new JsonArray(mercancias);
        }

        return ruta;
    }

    private async Task EnviarAsync(string alias, string cotizacion, JsonObject ruta)
    {
        var solicitud = new JsonObject
        {
            ["tipo"] = "spot",
            ["nit_cliente"] = "900555111",
            ["cliente_nombre"] = "Longyin",
            ["rutas"] = new JsonArray(ruta),
        };
        await web.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", web.Sesion(alias).Access, solicitud.ToJsonString());
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            costeo.Registrar(cotizacion, web.UltimoJson().GetProperty("folio").GetString()!);
        }
    }
}
