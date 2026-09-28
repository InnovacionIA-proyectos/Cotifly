using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Clientes.Services;

/// <summary>
/// Búsqueda de clientes contra Nexo en vivo (HU-005.2, ADR-005): <c>GET {Nexo:UrlBase}/api/captura-solicitudes/clientes?q=</c>,
/// el mismo catálogo (clientes de <c>DimBPSolicitante</c> y prospectos) que usa el formulario "Nueva solicitud" de Nexo.
/// Nexo no tiene login propio: cada petición identifica al comercial con <c>X-Portal-Usuario-Id: {idUsuarioExterno}</c>
/// (<c>UsuariosWeb.idUsuarioExterno</c>, la misma fila de <c>tblSAPUsuariosApp</c> que usa el login de ClientePro) — nunca un
/// token de Cotifly. Se registra vía <c>clsFabricaBuscadorClientes</c>: sin <c>Nexo:UrlBase</c> configurada se usa
/// <see cref="clsBuscadorClientesNoDisponible"/> en su lugar.
/// </summary>
/// <remarks>
/// <para><b>Coincidencia exacta.</b> La respuesta de Nexo (<c>ClienteBusquedaDto[]</c>) no distingue "exacta" de "por
/// similitud": ese criterio sigue pendiente de definir con el equipo de Nexo (ADR-005, tabla de pendientes). Mientras tanto
/// se asume exacta únicamente cuando hay un solo candidato — nunca por el contenido del texto buscado.</para>
/// <para><b>Fallos.</b> Nunca lanza: usuario sin <c>idUsuarioExterno</c>, HTTP, red o tiempo agotado devuelven
/// <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c> (Result), igual que <see cref="clsBuscadorClientesNoDisponible"/> — el llamante
/// (la herramienta del agente o <c>GET /web/clientes</c>) decide qué mostrar. Una sola línea de warning por fallo, sin la
/// traza completa ni el header <c>X-Portal-Usuario-Id</c> (no es secreto, pero no aporta al log).</para>
/// </remarks>
public sealed class clsBuscadorClientesNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsBuscadorClientesNexo> logger)
    : IClsBuscadorClientes
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "Nexo";

    public const string Mensaje = "La búsqueda de clientes en Nexo no está disponible en este momento: intenta de nuevo o ingresa el NIT manualmente.";

    /// <summary>Reintentos ante fallos transitorios (además del primer intento).</summary>
    public const int Reintentos = 2;

    /// <summary>Límite de toda la consulta, reintentos incluidos: la búsqueda es interactiva (autocompletado/chat).</summary>
    public static readonly TimeSpan TiempoTotal = TimeSpan.FromSeconds(8);

    /// <summary>Límite de cada intento.</summary>
    public static readonly TimeSpan TiempoPorIntento = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan _esperaEntreIntentos = TimeSpan.FromMilliseconds(200);

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurada => _urlBase is not null;

    public async Task<clsResultado<clsResultadoBusquedaClientes>> BuscarAsync(Guid usuarioId, string texto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texto);
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(_urlBase, texto));
        peticion.Headers.Add("X-Portal-Usuario-Id", idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            var candidatos = LeerCandidatos(documento.RootElement);
            return new clsResultadoBusquedaClientes(candidatos.Count == 1, candidatos);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de Nexo
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ExecutionRejectedException)
        {
            var motivo = ex switch
            {
                OperationCanceledException or Polly.Timeout.TimeoutRejectedException => "tiempo agotado",
                Polly.CircuitBreaker.BrokenCircuitException => "circuito abierto tras fallos repetidos",
                _ => "error de red",
            };
            return NoDisponible(motivo, ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary><c>q</c> codificado para URL: un texto con <c>&amp;</c> o espacios no puede alterar la consulta.</summary>
    public static Uri ConstruirUrl(string urlBase, string texto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentNullException.ThrowIfNull(texto);
        return new Uri($"{urlBase.TrimEnd('/')}/api/captura-solicitudes/clientes?q={Uri.EscapeDataString(texto.Trim())}", UriKind.Absolute);
    }

    /// <summary>Configura la tubería estándar de resiliencia del cliente (ver <c>AgregarAgente</c>).</summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = TiempoTotal;
        resiliencia.AttemptTimeout.Timeout = TiempoPorIntento;
        resiliencia.Retry.MaxRetryAttempts = Reintentos;
        resiliencia.Retry.Delay = _esperaEntreIntentos;
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        resiliencia.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    }

    /// <summary><c>ClienteBusquedaDto[]</c> de Nexo (<c>CONTRATO.md</c>): filas sin <c>nitCliente</c>/<c>nombre</c>/<c>origen</c> se descartan
    /// (nunca se ofrece un candidato sin los datos mínimos para identificarlo).</summary>
    private static IReadOnlyList<clsClienteEncontrado> LeerCandidatos(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidatos = new List<clsClienteEncontrado>();
        foreach (var fila in raiz.EnumerateArray())
        {
            var nitCliente = Texto(fila, "nitCliente") ?? Texto(fila, "nit");
            var nombre = Texto(fila, "nombre");
            var origen = Texto(fila, "origen");
            if (nitCliente is null || nombre is null || origen is null)
            {
                continue;
            }

            candidatos.Add(new clsClienteEncontrado(nitCliente, nombre, origen, Texto(fila, "bp")));
        }

        return candidatos;
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private clsResultado<clsResultadoBusquedaClientes> NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Búsqueda de clientes en Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de búsqueda de clientes en Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, Mensaje);
    }
}
