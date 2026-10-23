using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/captura_rutas.feature</c> (HU-021.6, #132): el formulario por rutas contra la API real, SQL Server en
/// Testcontainers y el Nexo falso de <see cref="clsPasosCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosCapturaRutas(clsContextoWeb web, clsPasosCosteo costeo)
{
    private static readonly HashSet<string> _enteras = new(StringComparer.Ordinal) { "origen_municipio_id", "destino_municipio_id" };
    private static readonly HashSet<string> _decimales = new(StringComparer.Ordinal) { "peso_kg", "peso_con_tara_kg" };

    [Given("el catálogo de servicios conexos de Nexo tiene el servicio {string} {string} con un costo de {int}")]
    public void DadoElServicioConexo(string id, string nombre, int costo) =>
        costeo.Nexo.ConServicioConexo(id, id, nombre, "logistico", "UND", costo);

    [When("{string} envía a costeo la solicitud {string} del cliente {string} con estas rutas:")]
    public Task CuandoEnviaConRutas(string alias, string cotizacion, string nit, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var rutas = new JsonArray();
        foreach (var fila in tabla.Rows)
        {
            var ruta = new JsonObject { ["tipologia"] = "CargaSeca", ["valor_mercancia"] = 50_000_000m };
            foreach (var (columna, valor) in fila.Where(c => !string.IsNullOrWhiteSpace(c.Value)))
            {
                if (columna == "tipo_mercancia")
                {
                    ruta["mercancias"] = new JsonArray(Mercancia(valor == "sin tipo" ? null : valor));
                    continue;
                }

                ruta[columna] = _enteras.Contains(columna) ? JsonValue.Create(int.Parse(valor, CultureInfo.InvariantCulture))
                    : _decimales.Contains(columna) ? JsonValue.Create(decimal.Parse(valor, CultureInfo.InvariantCulture))
                    : JsonValue.Create(valor);
            }

            rutas.Add(ruta);
        }

        return EnviarAsync(alias, cotizacion, nit, rutas);
    }

    [When("{string} envía a costeo la solicitud {string} del cliente {string} con la ruta Buenaventura a Bogotá y {int} del servicio {string}")]
    public Task CuandoEnviaConConexo(string alias, string cotizacion, string nit, int cantidad, string servicio) =>
        EnviarAsync(alias, cotizacion, nit, new JsonArray(new JsonObject
        {
            ["linea_negocio"] = "Importacion",
            ["origen_municipio_id"] = 76109,
            ["origen"] = "Buenaventura",
            ["destino_municipio_id"] = 11001,
            ["destino"] = "Bogotá",
            ["tipologia"] = "CargaSeca",
            ["peso_kg"] = 28_000m,
            // HU-032 (#197): la ruta solo trae el peso: el vehículo elegido es obligatorio.
            ["tipo_vehiculo"] = "3S3",
            ["servicios_conexos"] = new JsonArray(new JsonObject { ["id_servicio_conexo"] = servicio, ["cantidad"] = cantidad }),
        }));

    [Then("la solicitud {string} tiene estas rutas guardadas:")]
    public async Task EntoncesTieneEstasRutas(string cotizacion, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var guardadas = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CONCAT(r.orden, '|', r.lineaNegocio, '|', r.origenNombre, '|', r.destinoNombre, '|', r.camiones, '|', r.peso, '|', r.vehiculo), ';') "
            + "WITHIN GROUP (ORDER BY r.orden) FROM ("
            + "SELECT l.orden, p.lineaNegocio, l.origenNombre, l.destinoNombre, COUNT(v.id) AS camiones, MAX(v.pesoKg) AS peso, MAX(v.tipoVehiculo) AS vehiculo "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + "LEFT JOIN dbo_cotifly.MargenesPolitica p ON p.id = l.lineaNegocioId "
            + "LEFT JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' GROUP BY l.orden, p.lineaNegocio, l.origenNombre, l.destinoNombre) r");
        var esperadas = string.Join(';', tabla.Rows.Select(f =>
            $"{f["orden"]}|{f["linea_negocio"]}|{f["origen"]}|{f["destino"]}|{f["camiones"]}|{f["peso_camion"]}|{f["vehiculo"]}"));
        guardadas.Should().Be(esperadas);
    }

    [Then("Nexo recibió una solicitud con {int} líneas con los tipos de vehículo {string}")]
    public void EntoncesNexoRecibioLineasConVehiculo(int lineas, string vehiculos)
    {
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        var enviados = alta["lineas"]!.AsArray().Select(l => l!["tipoVehiculo"]?.GetValue<string>()).ToList();
        enviados.Should().HaveCount(lineas);
        enviados.Should().Equal(vehiculos.Split(", "));
    }

    [Then("Nexo recibió una solicitud con {int} líneas sin tipo de vehículo")]
    public void EntoncesNexoRecibioLineasSinVehiculo(int lineas)
    {
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        var enviadas = alta["lineas"]!.AsArray();
        enviadas.Should().HaveCount(lineas);
        enviadas.Should().OnlyContain(l => l!["tipoVehiculo"] == null && l["carroceria"] == null);
    }

    [Then("la ruta {int} de {string} tiene el conexo {string} {string} con precio {int} y cantidad {int}")]
    public async Task EntoncesLaRutaTieneElConexo(int orden, string cotizacion, string id, string nombre, int precio, int cantidad)
    {
        var conexo = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT CONCAT(s.idServicioConexo, '|', s.nombreServicioConexo, '|', CAST(s.precio AS decimal(18,0)), '|', CAST(s.cantidad AS decimal(10,0)), '|', "
            + "CAST(l.costoServiciosConexos AS decimal(18,0))) FROM dbo_cotifly.CotizacionServiciosConexos s "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
            + "JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}");
        conexo.Should().Be($"{id}|{nombre}|{precio}|{cantidad}|{precio * cantidad}", "el precio del conexo es el costo de Nexo y suma al costo de la ruta");
    }

    [Then("la respuesta no trae el costo {int}")]
    public void EntoncesLaRespuestaNoTraeElCosto(int costo) =>
        web.UltimoJson().GetRawText().Should().NotContain(costo.ToString(CultureInfo.InvariantCulture), "el costo de Nexo es interno");

    [Then("el error trae exactamente estos errores por ruta:")]
    public void EntoncesElErrorTraeEstosErrores(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var errores = web.UltimoJson().GetProperty("error").GetProperty("detalle").GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("ruta").ValueKind == JsonValueKind.Null ? (int?)null : e.GetProperty("ruta").GetInt32(),
                e.GetProperty("campo").GetString(), e.GetProperty("codigo").GetString()))
            .ToList();
        errores.Should().BeEquivalentTo(tabla.Rows.Select(f => ((int?)int.Parse(f["ruta"], CultureInfo.InvariantCulture), f["campo"], f["codigo"])));
    }

    private static JsonObject Mercancia(string? tipo)
    {
        var mercancia = new JsonObject
        {
            ["nombre_mercancia"] = "Cajas",
            ["cantidad"] = 1,
            ["largo_cm"] = 120,
            ["ancho_cm"] = 100,
            ["alto_cm"] = 150,
            ["peso_kg"] = 1000,
        };
        if (tipo is not null)
        {
            mercancia["codigo_mercancia"] = tipo;
        }

        return mercancia;
    }

    private async Task EnviarAsync(string alias, string cotizacion, string nit, JsonArray rutas)
    {
        var solicitud = new JsonObject
        {
            ["tipo"] = "spot",
            ["nit_cliente"] = nit,
            ["cliente_nombre"] = "Longyin",
            ["rutas"] = rutas,
        };
        await web.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", web.Sesion(alias).Access, solicitud.ToJsonString());
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            costeo.Registrar(cotizacion, web.UltimoJson().GetProperty("folio").GetString()!);
        }
    }
}
