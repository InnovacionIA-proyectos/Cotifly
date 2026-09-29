using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Costeo contra Nexo en vivo (ADR-015). Rutas de <c>CONTRATO.md</c> de Nexo bajo <c>{Nexo:UrlBase}</c> (la raíz del servicio, sin
/// <c>/api</c>): <c>POST /api/captura-solicitudes</c> (HU-005.5). Igual que el resto de adaptadores de Nexo, identifica al comercial
/// con <c>X-Portal-Usuario-Id: {UsuariosWeb.idUsuarioExterno}</c> y nunca un token de Cotifly; se registra vía
/// <c>clsFabricaCosteoNexo</c> (sin <c>Nexo:UrlBase</c> → <see cref="clsCosteoNexoNoDisponible"/>).
/// </summary>
/// <remarks>
/// <para><b>Cliente HTTP propio (<see cref="NombreCliente"/>).</b> No usa el cliente <c>Nexo</c> de las búsquedas (8 s, 2
/// reintentos): el alta auto-costea en la misma petición (puede tardar más) y <b>un POST nunca se reintenta</b> (el alta no es
/// idempotente: un reintento automático duplicaría la solicitud en Nexo). Ver <see cref="ConfigurarResiliencia"/>.</para>
/// <para><b>Errores.</b> Nunca lanza por Nexo: tiempo agotado → <c>COSTEO_NEXO_TIEMPO_AGOTADO</c> (la solicitud pudo quedar creada);
/// 4xx → <c>COSTEO_NEXO_RECHAZADA</c> con <c>HTTP {estado} {code}: {message}</c> de Nexo en el detalle; 5xx, red, circuito abierto,
/// JSON inválido, sin configurar o comercial sin <c>idUsuarioExterno</c> → <c>COSTEO_NEXO_NO_DISPONIBLE</c>. Una línea de warning
/// por fallo, sin la traza ni el header <c>X-Portal-Usuario-Id</c>. La cancelación del llamador se propaga.</para>
/// </remarks>
public sealed class clsCosteoNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsCosteoNexo> logger)
    : IClsCosteoNexo
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> del costeo (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "NexoCosteo";

    public const string MensajeNoDisponible = "El costeo de Nexo no está disponible en este momento: intenta de nuevo más tarde.";
    public const string MensajeTiempoAgotado =
        "Nexo no respondió a tiempo: la solicitud pudo quedar creada en Nexo; verifica en Nexo antes de reintentar.";
    public const string MensajeRechazada = "Nexo rechazó la solicitud.";
    public const string MensajeSolicitudInvalida = "Nexo rechazó los datos de la solicitud.";

    /// <summary>Límite de toda la operación y de cada intento (el alta de Nexo auto-costea en línea).</summary>
    public static readonly TimeSpan TiempoTotal = TimeSpan.FromSeconds(30);

    /// <summary>Reintentos de las consultas GET (los POST no se reintentan nunca).</summary>
    public const int ReintentosLectura = 2;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurado => _urlBase is not null;

    public async Task<clsResultado<clsSolicitudCreadaNexo>> EnviarSolicitudAsync(
        Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        var respuesta = await EnviarAsync(usuarioId, HttpMethod.Post, "/api/captura-solicitudes", CuerpoCaptura(solicitud), cancellationToken);
        if (respuesta.EsFallo)
        {
            // Un 400 del alta es validación de los datos (Nexo no cambia de opinión con los mismos datos): error definitivo.
            return respuesta.Error.Codigo == clsCodigosError.CosteoNexoRechazada && respuesta.Error.Detalle?.StartsWith("HTTP 400", StringComparison.Ordinal) == true
                ? new clsErrorDominio(clsCodigosError.CosteoNexoSolicitudInvalida, MensajeSolicitudInvalida, respuesta.Error.Detalle)
                : respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var raiz = documento.RootElement;
        var id = Texto(raiz, "id");
        if (id is null)
        {
            return NoDisponible("respuesta sin id");
        }

        return new clsSolicitudCreadaNexo(
            id,
            Entero(raiz, "lineas") ?? 1,
            Entero(raiz, "fletesCalculados") ?? 0,
            Entero(raiz, "lineasPorCostear") ?? 1,
            raiz.TryGetProperty("costeoEnProceso", out var enProceso) && enProceso.ValueKind == JsonValueKind.True);
    }

    /// <summary><c>{urlBase}{ruta}</c>, con <paramref name="ruta"/> empezando por <c>/api/</c> (la base no lleva <c>/api</c>).</summary>
    public static Uri ConstruirUrl(string urlBase, string ruta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        return new Uri($"{urlBase.TrimEnd('/')}{ruta}", UriKind.Absolute);
    }

    /// <summary>
    /// Tubería del cliente <see cref="NombreCliente"/>: 30 s en total y por intento, reintentos solo para métodos seguros (GET) y el
    /// circuito con una ventana de muestreo de al menos el doble del intento (lo exige la tubería estándar).
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = TiempoTotal;
        resiliencia.AttemptTimeout.Timeout = TiempoTotal;
        resiliencia.Retry.MaxRetryAttempts = ReintentosLectura;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        resiliencia.Retry.DisableForUnsafeHttpMethods();
        resiliencia.CircuitBreaker.SamplingDuration = TiempoTotal * 2;
    }

    /// <summary>
    /// <c>CapturaSolicitudRequest</c> de Nexo (camelCase) con una sola línea (ADR-015): <c>tipo</c> en minúscula (<c>spot</c> |
    /// <c>licitacion</c>, como lo valida Nexo), el cliente por BP y los catálogos con el código de Nexo tal cual. Los nulos no viajan.
    /// </summary>
    internal static object CuerpoCaptura(clsSolicitudNexo solicitud)
    {
        var s = solicitud.Solicitud;
        return new clsCapturaSolicitudWire(
            solicitud.ClienteBp,
            clsCodigoEnum.ACodigo(s.Tipo),
            s.Deadline,
            s.ValorEstimado,
            s.Referencia,
            s.HorasCargue,
            s.HorasDescargue,
            [
                new clsCapturaLineaWire(
                    s.Origen, s.Destino, s.TipoVehiculo, s.Carroceria, s.Tipologia, solicitud.LineaNegocioNexo, s.ClaseViaje, s.ClaseContenedor,
                    s.Exclusivo, s.Consolidado, s.RetornaContenedor, s.CiudadDevolucionContenedor, s.LevanteContenedorDiferente,
                    s.CiudadLevanteContenedor, s.ValorAprovisionamiento, s.PesoKg, s.ValorMercancia,
                    s.Posiciones.Count == 0
                        ? null
                        : [.. s.Posiciones.Select(p => new clsCapturaPosicionWire(p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))]),
            ]);
    }

    /// <summary>
    /// Envía la petición con la identidad del comercial y traduce los fallos. Devuelve el JSON de una respuesta 2xx (el llamador lo
    /// libera) o el error de negocio.
    /// </summary>
    internal async Task<clsResultado<JsonDocument>> EnviarAsync(
        Guid usuarioId, HttpMethod metodo, string ruta, object? cuerpo, CancellationToken cancellationToken)
    {
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        using var peticion = new HttpRequestMessage(metodo, ConstruirUrl(_urlBase, ruta));
        if (cuerpo is not null)
        {
            peticion.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: _json);
        }

        peticion.Headers.Add("X-Portal-Usuario-Id", idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)respuesta.StatusCode is >= 400 and < 500)
            {
                var detalle = await LeerErrorNexoAsync(respuesta, cancellationToken);
                logger.LogWarning("Nexo rechazó {Metodo} {Ruta}: {Detalle}", metodo, ruta, detalle);
                return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, MensajeRechazada, detalle);
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de Nexo
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            logger.LogWarning("Nexo no respondió a tiempo en {Metodo} {Ruta}.", metodo, ruta);
            logger.LogDebug(ex, "Detalle del tiempo agotado en Nexo ({Ruta}).", ruta);
            return new clsErrorDominio(clsCodigosError.CosteoNexoTiempoAgotado, MensajeTiempoAgotado);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            return NoDisponible(ex is Polly.CircuitBreaker.BrokenCircuitException ? "circuito abierto tras fallos repetidos" : "error de red", ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary>Error de Nexo (<c>{code, message, timestamp, details?}</c>) como <c>HTTP {estado} {code}: {message}</c> (máx. 400 caracteres).</summary>
    private static async Task<string> LeerErrorNexoAsync(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        var estado = (int)respuesta.StatusCode;
        var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
        string? codigo = null;
        string? mensaje = null;
        try
        {
            using var documento = JsonDocument.Parse(texto);
            if (documento.RootElement.ValueKind == JsonValueKind.Object)
            {
                codigo = Texto(documento.RootElement, "code");
                mensaje = Texto(documento.RootElement, "message");
            }
        }
        catch (JsonException)
        {
            // cuerpo que no es JSON: se usa el texto recortado
        }

        var explicacion = mensaje ?? (string.IsNullOrWhiteSpace(texto) ? null : texto.Trim());
        var detalle = $"HTTP {estado.ToString(CultureInfo.InvariantCulture)}"
            + (codigo is null ? string.Empty : $" {codigo}")
            + (explicacion is null ? string.Empty : $": {explicacion}");
        return detalle.Length <= 400 ? detalle : detalle[..399] + "…";
    }

    internal static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor)
            ? valor.ValueKind switch
            {
                JsonValueKind.String => valor.GetString(),
                JsonValueKind.Number => valor.GetRawText(),
                _ => null,
            }
            : null;

    internal static int? Entero(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Number
            && valor.TryGetInt32(out var numero)
            ? numero
            : null;

    internal static decimal? Decimal(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Number
            && valor.TryGetDecimal(out var numero)
            ? numero
            : null;

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Costeo en Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del costeo en Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, MensajeNoDisponible, motivo);
    }

    // --- Forma exacta de CapturaSolicitudRequest / CapturaLineaRequest / CapturaPosicionRequest de Nexo (camelCase) ---

    private sealed record clsCapturaSolicitudWire(
        string ClienteBp, string Tipo, DateTimeOffset? Deadline, decimal? ValorEstimado, string? Referencia,
        decimal? HorasCargue, decimal? HorasDescargue, IReadOnlyList<clsCapturaLineaWire> Lineas);

    private sealed record clsCapturaLineaWire(
        string Origen, string Destino, string? TipoVehiculo, string? Carroceria, string? Tipologia, string? LineaNegocio,
        string? ClaseViaje, string? ClaseContenedor, bool? Exclusivo, bool? Consolidado, bool? RetornaContenedor,
        string? CiudadDevolucionContenedor, bool? LevanteContenedorDiferente, string? CiudadLevanteContenedor,
        decimal? ValorAprovisionamiento, decimal? Peso, decimal? ValorMercancia, IReadOnlyList<clsCapturaPosicionWire>? Posiciones);

    private sealed record clsCapturaPosicionWire(string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);
}
