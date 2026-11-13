using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/licitacion_cubicador.feature</c>: la licitación con el cubicador se guarda sin esperar,
/// el procesador en segundo plano cubica sus rutas (solo las de esta licitación: la base es compartida) y la solicitud sale a Nexo una vez.
/// </summary>
[Binding]
public sealed class clsPasosLicitacionCubicador(clsPasosCosteo costeo, clsContextoWeb web)
{
    private string? _folio;

    private string Folio => _folio ?? throw new InvalidOperationException("Falta el paso que crea la licitación.");

    [When("{string} crea una licitación de {int} rutas con la mercancía {string} para el cliente {string}")]
    public async Task CuandoCreaLaLicitacion(string comercial, int rutas, string codigo, string nit)
    {
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            cliente_nombre = "Cliente de la licitación",
            linea_negocio = "Nacional",
            lineas = Enumerable.Range(0, rutas).Select(i => new
            {
                fila = i,
                origen = "BUENAVENTURA",
                destino = "BOGOTA, D.C.",
                tipologia = "CargaSeca",
                peso_kg = 10 * (1000 + i),
                mercancias = new[]
                {
                    new { codigo_mercancia = codigo, nombre_mercancia = "Estiba", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1000 + i },
                },
            }).ToArray(),
        });
        await web.EnviarAsync(HttpMethod.Post, "/web/licitaciones", web.Sesion(comercial).Access, cuerpo);
        web.UltimoEstado.Should().Be(HttpStatusCode.Created, web.UltimoCuerpo);
        _folio = web.UltimoJson().GetProperty("folio").GetString();
    }

    [When("el procesador cubica la licitación")]
    public Task CuandoElProcesadorCubica() => web.Api.ProcesarCubicacionAsync(Folio);

    [Then("la licitación está {string} con {int} rutas pendientes y no se envió a Nexo")]
    public async Task EntoncesEnCursoSinNexo(string estado, int pendientes)
    {
        await EntoncesElProgreso(estado, pendientes);
        costeo.Nexo.SolicitudesCreadas.Should().BeEmpty("la licitación no sale a Nexo mientras se cubica");
    }

    [Then("la licitación está {string} con {int} rutas pendientes")]
    public async Task EntoncesElProgreso(string estado, int pendientes)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/licitaciones/{Folio}", web.Sesiones.Values.First().Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var progreso = web.UltimoJson().GetProperty("progreso_cubicacion");
        progreso.GetProperty("estado").GetString().Should().Be(estado);
        progreso.GetProperty("pendientes").GetInt32().Should().Be(pendientes);
    }

    [Then("la licitación se envió a Nexo una sola vez con {int} líneas")]
    public void EntoncesSeEnvioUnaVez(int lineas)
    {
        costeo.Nexo.SolicitudesCreadas.Should().ContainSingle();
        costeo.Nexo.UltimaAlta!["lineas"]!.AsArray().Should().HaveCount(lineas);
    }
}
