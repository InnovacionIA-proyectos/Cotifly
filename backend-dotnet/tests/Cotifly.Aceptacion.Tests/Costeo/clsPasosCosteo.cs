using System.Net;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/costeo_nexo.feature</c> (ADR-015): la API real en memoria conectada a un Nexo falso con estado
/// (<see cref="clsNexoFalso"/>). Las cotizaciones se nombran por alias; el folio real lo pone el servidor.
/// </summary>
[Binding]
public sealed class clsPasosCosteo(clsContextoWeb web) : IDisposable
{
    private readonly clsNexoFalso _nexo = new();
    private readonly Dictionary<string, string> _folios = new(StringComparer.OrdinalIgnoreCase);

    [Given("la API web de Cotifly conectada a Nexo con el cliente {string} de BP {string}")]
    public void DadoLaApiConNexo(string nit, string bp)
    {
        _nexo.ConCliente(nit, $"CLIENTE {nit} S.A.S.", "cliente", bp);
        web.Iniciar(clsApiWebPruebas.ConNexo(clsEntornoAceptacion.BaseDatos, _nexo));
    }

    [Given("que Nexo responde {int} al alta de solicitudes")]
    public void DadoQueNexoFallaElAlta(int estado) => _nexo.AltaFalla = (HttpStatusCode)estado;

    [When("Nexo vuelve a responder")]
    public void CuandoNexoVuelve()
    {
        _nexo.AltaFalla = null;
        _nexo.Caido = null;
    }

    [When("{string} envía a costeo el formulario ampliado del cliente {string} como {string}")]
    public async Task CuandoEnviaACosteo(string alias, string nit, string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", web.Sesion(alias).Access, Formulario(nit));
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            _folios[cotizacion] = web.UltimoJson().GetProperty("folio").GetString()!;
        }
    }

    [When("{string} reintenta el envío a costeo de {string}")]
    public Task CuandoReintenta(string alias, string cotizacion) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{Folio(cotizacion)}/costeo/reintentar", web.Sesion(alias).Access);

    [Then("la cotización {string} queda en el estado de costeo {string}")]
    public async Task EntoncesElEstado(string cotizacion, string estado) =>
        (await Columna(cotizacion, "estadoCosteo")).Should().Be(estado);

    [Then("la cotización {string} queda en el estado de costeo {string} con un motivo que menciona {string}")]
    public async Task EntoncesElEstadoConMotivo(string cotizacion, string estado, string texto)
    {
        await EntoncesElEstado(cotizacion, estado);
        (await Columna(cotizacion, "motivoEstadoCosteo")).Should().Contain(texto);
    }

    [Then("Nexo recibió la solicitud de {string} a {string} con el BP {string} y {int} posiciones")]
    public void EntoncesNexoRecibio(string origen, string destino, string bp, int posiciones)
    {
        var alta = _nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        alta["clienteBp"]!.GetValue<string>().Should().Be(bp);
        var linea = alta["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["origen"]!.GetValue<string>().Should().Be(origen);
        linea["destino"]!.GetValue<string>().Should().Be(destino);
        linea["posiciones"]!.AsArray().Should().HaveCount(posiciones);
    }

    /// <summary>Nexo falso del escenario (lo usan los pasos del polling y del resultado).</summary>
    public clsNexoFalso Nexo => _nexo;

    public string Folio(string cotizacion) =>
        _folios.TryGetValue(cotizacion, out var folio) ? folio : throw new InvalidOperationException($"No se envió la cotización '{cotizacion}'.");

    public Task<string?> Columna(string cotizacion, string columna) =>
        clsBaseDatosPruebas.EscalarAsync<string>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT CAST({columna} AS nvarchar(max)) FROM dbo_cotifly.Cotizaciones WHERE folio = '{Folio(cotizacion)}'");

    private static string Formulario(string nit) => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = nit,
        linea_negocio = "Importacion",
        referencia = "OC-7788",
        origen = "Buenaventura",
        destino = "Bogotá D.C.",
        tipo_vehiculo = "3S3",
        carroceria = "PORTACONTENEDORES",
        clase_viaje = "Contenedor",
        clase_contenedor = "CNT40.HC",
        peso_kg = 28500,
        posiciones = new object[]
        {
            new { descripcion = "Estiba", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 850 },
            new { largo_cm = 1200, ancho_cm = 235, alto_cm = 269 },
        },
    });

    public void Dispose() => _nexo.Dispose();
}
