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

    // Fuentes de la TRM falsas (ADR-014): la API real nunca sale a la red. DolarApi refrescó "hoy" a las 08:00 en Bogotá (reloj real).
    private readonly clsDolarApiFalso _dolarApi = new() { FechaActualizacion = clsDolarApiFalso.HoyALasOchoEnBogota() };
    private readonly clsTrmFalso _superfinanciera = new();
    private readonly Dictionary<string, string> _folios = new(StringComparer.OrdinalIgnoreCase);

    [Given("la API web de Cotifly conectada a Nexo con el cliente {string} de BP {string}")]
    public void DadoLaApiConNexo(string nit, string bp)
    {
        _nexo.ConCliente(nit, $"CLIENTE {nit} S.A.S.", "cliente", bp);
        web.Iniciar(clsApiWebPruebas.ConNexo(clsEntornoAceptacion.BaseDatos, _nexo, dolarApi: _dolarApi, superfinanciera: _superfinanciera));
    }

    [Given("que DolarApi publica hoy la TRM {string}")]
    public void DadoQueDolarApiPublicaHoy(string valor)
    {
        _dolarApi.Valor = decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);
        _dolarApi.FechaActualizacion = clsDolarApiFalso.HoyALasOchoEnBogota();
    }

    [Given("que las fuentes de la TRM no responden")]
    public void DadoQueLasFuentesDeLaTrmNoResponden()
    {
        _dolarApi.Caida(10);
        _superfinanciera.Caida(10);
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

    [When("Nexo costea la solicitud de {string} con un flete de {int}")]
    public async Task CuandoNexoCostea(string cotizacion, int flete) =>
        _nexo.Costear((await Columna(cotizacion, "costeoId"))!, flete);

    [When("Nexo entrega la cotización de {string}")]
    public async Task CuandoNexoEntrega(string cotizacion) => _nexo.Entregar((await Columna(cotizacion, "costeoId"))!);

    [When("Nexo descarta la solicitud de {string}")]
    public async Task CuandoNexoDescarta(string cotizacion) => _nexo.Descartar((await Columna(cotizacion, "costeoId"))!);

    [When("Nexo deja de responder")]
    public void CuandoNexoSeCae() => _nexo.Caido = HttpStatusCode.ServiceUnavailable;

    [When("el polling consulta a Nexo")]
    public Task CuandoElPollingConsulta() => ProcesarTodasAsync(null);

    [When("el polling consulta a Nexo {int} horas después del envío")]
    public Task CuandoElPollingConsultaDespues(int horas) => ProcesarTodasAsync(TimeSpan.FromHours(horas));

    [Then("el cambio de estado de {string} queda en la auditoría")]
    public async Task EntoncesElCambioAuditado(string cotizacion) =>
        (await EventosAsync("costeo_nexo_estado", cotizacion, exito: true)).Should().BeGreaterThan(0);

    [Then("el error de consulta de {string} queda en la auditoría")]
    public async Task EntoncesElErrorAuditado(string cotizacion) =>
        (await EventosAsync("costeo_nexo_consulta", cotizacion, exito: false)).Should().BeGreaterThan(0);

    [Then("la cotización {string} ya no se consulta en Nexo")]
    public async Task EntoncesYaNoSeConsulta(string cotizacion) => (await Columna(cotizacion, "costeoSiguienteConsultaAt")).Should().BeNull();

    private async Task ProcesarTodasAsync(TimeSpan? enviadaHace)
    {
        foreach (var folio in _folios.Values)
        {
            await web.Api.ProcesarPollingAsync(folio, enviadaHace);
        }
    }

    private Task<int> EventosAsync(string herramienta, string cotizacion, bool exito) =>
        clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{herramienta}' AND exito = {(exito ? 1 : 0)} AND input LIKE '%{Folio(cotizacion)}%'");

    [Then("\"Mis cotizaciones\" de {string} muestra {string} sin precio final")]
    public async Task EntoncesSinPrecio(string alias, string cotizacion) =>
        (await FilaAsync(alias, cotizacion)).GetProperty("precio_final").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

    [Then("\"Mis cotizaciones\" de {string} muestra {string} en {string} con el precio final {int}")]
    public async Task EntoncesConPrecio(string alias, string cotizacion, string estado, int precio)
    {
        var fila = await FilaAsync(alias, cotizacion);
        fila.GetProperty("estado_costeo").GetString().Should().Be(estado);
        fila.GetProperty("precio_final").GetDecimal().Should().Be(precio);
    }

    [Then("ninguna respuesta de \"Mis cotizaciones\" de {string} contiene el costo {int}")]
    public async Task EntoncesSinCosto(string alias, int costo)
    {
        await web.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", web.Sesion(alias).Access);
        web.UltimoCuerpo.Should().NotContain(costo.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<System.Text.Json.JsonElement> FilaAsync(string alias, string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", web.Sesion(alias).Access);
        return web.UltimoJson().GetProperty("cotizaciones").EnumerateArray().Single(c => c.GetProperty("folio").GetString() == Folio(cotizacion));
    }

    [When("{string} marca el resultado de {string} como {string} con el motivo {string} y el comentario {string}")]
    public Task CuandoMarcaResultado(string alias, string cotizacion, string resultado, string motivo, string comentario) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{Folio(cotizacion)}/resultado", web.Sesion(alias).Access,
            clsApiWebPruebas.Json(new { resultado, motivo, comentario }));

    [Then("Nexo registró la cotización de {string} como {string}")]
    public async Task EntoncesNexoRegistro(string cotizacion, string resultado) =>
        _nexo.Resultado((await Columna(cotizacion, "costeoId"))!).Should().Be(resultado);

    [Then("la cotización {string} queda ganada {string} con el motivo {string}")]
    public async Task EntoncesGanada(string cotizacion, string ganada, string motivo)
    {
        (await Columna(cotizacion, "ganada")).Should().Be(bool.Parse(ganada) ? "1" : "0");
        (await Columna(cotizacion, "motivoRechazo")).Should().Be(motivo);
    }

    [Then("la cotización {string} sigue sin resultado")]
    public async Task EntoncesSinResultado(string cotizacion)
    {
        (await Columna(cotizacion, "ganada")).Should().BeNull();
        (await Columna(cotizacion, "motivoRechazo")).Should().BeNull();
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
            new { descripcion = "Estiba", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1425 },
            new { largo_cm = 1200, ancho_cm = 235, alto_cm = 269 },
        },
    });

    public void Dispose()
    {
        _nexo.Dispose();
        _dolarApi.Dispose();
        _superfinanciera.Dispose();
    }
}
