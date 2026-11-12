using System.Globalization;
using System.Net;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/envio_conexos_nexo.feature</c>: una ruta capturada con un servicio conexo del
/// catálogo de Nexo, la Mesa que costea y entrega, y el polling que envía el conexo una sola vez a la línea del primer camión.
/// </summary>
[Binding]
public sealed class clsPasosEnvioConexosNexo(clsContextoWeb web, clsPasosCosteo costeo)
{
    private readonly Dictionary<string, string> _servicios = new(StringComparer.OrdinalIgnoreCase);

    [Given("que Nexo tiene en su catálogo el servicio conexo {string} con un costo de {int}")]
    public void DadoElServicioEnElCatalogo(string nombre, int costo)
    {
        var id = Guid.NewGuid().ToString("D");
        costeo.Nexo.ConServicioConexo(id, $"SC-{id[..8]}", nombre, "logistico", "UND", costo);
        _servicios[nombre] = id;
    }

    [Given("{string} envió a costeo una ruta del cliente {string} con {int} del servicio conexo {string} como {string}")]
    public async Task DadoQueEnvioUnaRutaConElConexo(string alias, string nit, int cantidad, string servicio, string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", web.Sesion(alias).Access, clsApiWebPruebas.Json(new
        {
            tipo = "spot",
            nit_cliente = nit,
            linea_negocio = "Importacion",
            rutas = new object[]
            {
                new
                {
                    origen_municipio_id = 76109,
                    origen = "Buenaventura",
                    destino_municipio_id = 11001,
                    destino = "Bogotá D.C.",
                    tipologia = "Contenedor",
                    clase_viaje = "Contenedor",
                    clase_contenedor = "CNT40.HC",
                    exclusivo = true,
                    consolidado = false,
                    retorna_contenedor = false,
                    levante_contenedor_diferente = false,
                    peso_kg = 28000,
                    valor_mercancia = 250000000,
                    mercancias = new object[]
                    {
                        new { codigo_mercancia = clsPasosCosteo.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 },
                    },
                    servicios_conexos = new object[] { new { id_servicio_conexo = _servicios[servicio], cantidad } },
                },
            },
        }));
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        costeo.Registrar(cotizacion, web.UltimoJson().GetProperty("folio").GetString()!);
    }

    [When("la Mesa de Costeo de Nexo costea la solicitud de {string} con un flete de {int} y la entrega")]
    public async Task CuandoLaMesaCosteaYEntrega(string cotizacion, int flete)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        costeo.Nexo.Costear(solicitud, flete);
        costeo.Nexo.Entregar(solicitud);
    }

    [Then("Nexo recibió una sola vez {int} del servicio conexo {string} de {string} en la línea de su primer camión")]
    public async Task EntoncesNexoRecibioUnaSolaVez(int cantidad, string servicio, string cotizacion)
    {
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        var peticion = costeo.Nexo.PeticionesConexos(costeo.Nexo.CotizacionDe(solicitud)!).Should().ContainSingle("decisión 10: una sola vez").Subject;
        var item = peticion["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["servicioConexoId"]!.GetValue<string>().Should().Be(_servicios[servicio]);
        item["cantidad"]!.GetValue<decimal>().Should().Be(cantidad);
        item["lineaId"]!.GetValue<string>().Should().Be(costeo.Nexo.LineaCotizacionDe(solicitud, 0));
    }

    [Then("el servicio conexo {string} de {string} conserva el costo {int} del catálogo")]
    public async Task EntoncesConservaElCosto(string servicio, string cotizacion, int costo)
    {
        var precios = await clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CAST(CAST(s.precio AS decimal(18,2)) AS nvarchar(30)), '|') FROM dbo_cotifly.CotizacionServiciosConexos s "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND s.idServicioConexo = '{_servicios[servicio]}'");
        precios.Should().Be(costo.ToString("0.00", CultureInfo.InvariantCulture), "una sola fila, con el costo del catálogo (no se reimporta desde Nexo)");
    }
}
