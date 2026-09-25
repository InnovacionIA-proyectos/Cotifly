using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Fuente SAP TM <c>odata</c> en vivo (port de <c>ODataFuente</c>, <c>mcp_server/margen/odata_fuente.py</c>):
/// <c>GET {SapTm:Url}/RentabClienteSet?$filter=SalesOrgId eq '..' and TrqType eq '..' and OrderPartyId eq '..'&amp;$format=json</c>
/// con autenticación Basic (<c>SapTm:User</c>/<c>SapTm:Pass</c>) y devuelve <c>d.results[0].Value</c> crudo (el resolver divide por 100).
/// Sin resultados → <c>null</c>; error HTTP, red, tiempo agotado o respuesta inválida → <see cref="clsFuenteMargenNoDisponibleException"/>.
/// </summary>
/// <remarks>
/// <para><b>Inyección corregida.</b> El Python armaba el <c>$filter</c> con un f-string sin escapar: un BP como
/// <c>15927' or 1 eq 1</c> cambiaba la consulta. Aquí cada valor va como literal de cadena OData (comilla simple duplicada) y
/// el filtro completo se codifica para URL, así un valor no puede cerrar el literal ni agregar parámetros.</para>
/// <para><b>Resiliencia</b> (<c>Microsoft.Extensions.Http.Resilience</c>, registrada en <c>AddInfraestructura</c>):
/// <see cref="TiempoTotal"/> de 10 s como el <c>timeout=10.0</c> del Python, <see cref="TiempoPorIntento"/> y
/// <see cref="Reintentos"/> reintentos ante 5xx, 408, 429, fallos de red o intento agotado (solo GET, idempotente).</para>
/// <para><b>Logs.</b> Un fallo deja una sola línea de warning (motivo, TrqType y organización), sin la traza de cada intento:
/// la excepción completa y los eventos de la tubería (<c>Polly</c>) quedan en Debug.</para>
/// <para>Nunca se registran credenciales: la cabecera <c>Authorization</c> se arma por petición y los logs de
/// <c>IHttpClientFactory</c> la ocultan (<c>RedactLoggedHeaders</c>).</para>
/// </remarks>
public sealed class clsFuenteMargenOData(HttpClient http, IOptions<clsOpcionesSapTm> opciones, ILogger<clsFuenteMargenOData> logger) : IClsFuenteMargen
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "SapTm";

    /// <summary>Conjunto de entidades de rentabilidad por cliente (<c>_ENTITY_SET</c> del Python).</summary>
    public const string ConjuntoEntidades = "RentabClienteSet";

    /// <summary>Reintentos ante fallos transitorios (además del primer intento).</summary>
    public const int Reintentos = 2;

    /// <summary>Límite de toda la consulta, reintentos incluidos (paridad con el <c>timeout=10.0</c> del Python).</summary>
    public static readonly TimeSpan TiempoTotal = TimeSpan.FromSeconds(10);

    /// <summary>Límite de cada intento.</summary>
    public static readonly TimeSpan TiempoPorIntento = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan _esperaEntreIntentos = TimeSpan.FromMilliseconds(200);

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.Url) ? null : opciones.Value.Url.Trim().TrimEnd('/');
    private readonly string _usuario = opciones.Value.User ?? string.Empty;
    private readonly string _clave = opciones.Value.Pass ?? string.Empty;
    private readonly string? _sapClient = string.IsNullOrWhiteSpace(opciones.Value.SapClient) ? null : opciones.Value.SapClient.Trim();

    public string Modo => clsOpcionesSapTm.ModoOData;

    public bool EstaConfigurada => _urlBase is not null;

    public async Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(salesOrg);
        ArgumentNullException.ThrowIfNull(trqType);
        ArgumentNullException.ThrowIfNull(bpCliente);
        if (_urlBase is null)
        {
            throw new clsFuenteMargenNoDisponibleException("La fuente odata de SAP TM no tiene URL configurada (SapTm:Url).");
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(_urlBase, salesOrg, trqType, bpCliente, _sapClient));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_usuario.Length > 0)
        {
            peticion.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_usuario}:{_clave}")));
        }

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                throw NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})", trqType, salesOrg);
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            foreach (var fila in clsFormatoRentabSap.Resultados(documento.RootElement))
            {
                return clsFormatoRentabSap.Valor(fila); // primera fila, como resultados[0] del Python
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de SAP
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ExecutionRejectedException)
        {
            var motivo = ex switch
            {
                OperationCanceledException or Polly.Timeout.TimeoutRejectedException => "tiempo agotado",
                Polly.CircuitBreaker.BrokenCircuitException => "circuito abierto tras fallos repetidos",
                _ => "error de red",
            };
            throw NoDisponible(motivo, trqType, salesOrg, ex);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw NoDisponible(ex is JsonException ? "JSON inválido" : ex.Message, trqType, salesOrg, ex);
        }
    }

    /// <summary>
    /// URL de la consulta: cada valor como literal de cadena OData (<c>'</c> → <c>''</c>) y el <c>$filter</c> codificado
    /// con <see cref="Uri.EscapeDataString"/> (espacios <c>%20</c>, comillas <c>%27</c>, <c>&amp;</c> <c>%26</c>...).
    /// Con <paramref name="sapClient"/> se agrega <c>&amp;sap-client=NNN</c> (validado de 3 dígitos en las opciones).
    /// </summary>
    public static Uri ConstruirUrl(string urlBase, string salesOrg, string trqType, string bpCliente, string? sapClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        var filtro = $"SalesOrgId eq {Literal(salesOrg)} and TrqType eq {Literal(trqType)} and OrderPartyId eq {Literal(bpCliente)}";
        var mandante = string.IsNullOrWhiteSpace(sapClient) ? string.Empty : $"&sap-client={Uri.EscapeDataString(sapClient.Trim())}";
        return new Uri(
            $"{urlBase.TrimEnd('/')}/{ConjuntoEntidades}?$filter={Uri.EscapeDataString(filtro)}&$format=json{mandante}", UriKind.Absolute);
    }

    /// <summary>Configura la tubería estándar de resiliencia del cliente (ver <c>AddInfraestructura</c>).</summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = TiempoTotal;
        resiliencia.AttemptTimeout.Timeout = TiempoPorIntento;
        resiliencia.Retry.MaxRetryAttempts = Reintentos;
        resiliencia.Retry.Delay = _esperaEntreIntentos;
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        // El circuito exige una ventana de muestreo de al menos el doble del tiempo por intento.
        resiliencia.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    }

    private static string Literal(string valor) => $"'{valor.Replace("'", "''", StringComparison.Ordinal)}'";

    private clsFuenteMargenNoDisponibleException NoDisponible(string motivo, string trqType, string salesOrg, Exception? causa = null)
    {
        // Una sola línea de warning, sin traza ni credenciales; la excepción completa solo en Debug. Los eventos de
        // Polly de cada intento también bajan a Debug (ver AddInfraestructura).
        logger.LogWarning("SAP TM OData no disponible ({Motivo}) consultando {TrqType} en {SalesOrg}.", motivo, trqType, salesOrg);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de SAP TM OData ({Motivo}).", motivo);
        }
        var mensaje = string.Create(CultureInfo.InvariantCulture, $"SAP TM no disponible o respuesta inválida: {motivo}.");
        return causa is null ? new clsFuenteMargenNoDisponibleException(mensaje) : new clsFuenteMargenNoDisponibleException(mensaje, causa);
    }
}
