using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicador en vivo (HU-021.5, #133; <c>CAMBIOS_BACKEND</c> §6), mismo patrón que <c>clsCosteoNexo</c>: cliente HTTP propio
/// (<see cref="NombreCliente"/>), <b>sin reintentar los POST</b>, opciones validadas (<see cref="clsOpcionesCubicador"/>) y todo vía
/// <see cref="clsResultado{TValor}"/>. Rutas bajo <c>{Cubicador:UrlBase}</c>:
/// <list type="bullet">
/// <item><c>POST /recomendaciones</c> con <c>{idLineaSolicitud, origenSistema: "COTIFLY", mercancias: [{idTipoMercancia, cantidad, largoCm,
/// anchoCm, altoCm, pesoKg}]}</c>; responde las filas de su Tabla 7 (<c>{idRecomendacionVehiculo, tipoVehiculo, carroceria, pesoTotalKg,
/// resultado, motivo}</c>), como arreglo o dentro de <c>recomendaciones</c>.</item>
/// <item><c>GET /tipos-mercancia</c>: <c>[{id, nombre}]</c> (o dentro de <c>items</c>), con la caché de <see cref="clsCacheTiposMercancia"/>.</item>
/// </list>
/// <b>Contrato provisional:</b> el cubicador aún no tiene estos endpoints (C1); los nombres salen del diseño
/// (<c>PLAN-IMPLEMENTACION-MOTOR-SELECCION-VEHICULOS.md</c>) y la lectura acepta los alias razonables (<c>id</c>, <c>pesoKg</c>,
/// <c>descripcion</c>...). Reconfirmar al publicarse.
/// </summary>
/// <remarks>
/// Autenticación <c>Authorization: Bearer {Cubicador:ApiKey}</c> (C5). Errores: sin configurar, 4xx, 5xx, red, circuito abierto, tiempo
/// agotado, respuesta demasiado grande o JSON inválido → <c>CUBICADOR_NO_DISPONIBLE</c> con el motivo en el detalle; una línea de
/// warning por fallo, sin la llave ni la traza (401/403 dejan una línea de error: revisar la llave). La cancelación del llamador se
/// propaga.
/// </remarks>
public sealed class clsCubicadorHttp(
    HttpClient http,
    IOptions<clsOpcionesCubicador> opciones,
    clsCacheTiposMercancia cache,
    ILogger<clsCubicadorHttp> logger) : IClsCubicador
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> del cubicador (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "Cubicador";

    /// <summary>Valor de <c>OrigenSistema</c> en <c>POST /recomendaciones</c>.</summary>
    public const string OrigenSistema = "COTIFLY";

    public const string MensajeNoDisponible = "El cubicador no está disponible en este momento: intenta de nuevo más tarde.";

    /// <summary>Reintentos de las consultas GET (los POST no se reintentan nunca).</summary>
    public const int ReintentosLectura = 2;

    /// <summary>Tamaño máximo de una respuesta del cubicador.</summary>
    public const long LimiteBytes = 1024 * 1024;

    /// <summary>Filas máximas que se leen de una respuesta (recomendación o catálogo).</summary>
    public const int MaximoFilas = 5_000;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurado => _urlBase is not null && !string.IsNullOrWhiteSpace(opciones.Value.ApiKey);

    public bool RespaldoSiFalla => opciones.Value.RespaldoSiFalla;

    public TimeSpan PresupuestoCaptura => TimeSpan.FromSeconds(opciones.Value.PresupuestoCapturaSegundos);

    public async Task<clsResultado<clsRecomendacionCubicador>> RecomendarAsync(
        Guid rutaId, IReadOnlyList<clsMercanciaCubicador> mercancias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mercancias);
        var cuerpo = new clsRecomendacionWire(
            rutaId.ToString("D"),
            OrigenSistema,
            [.. mercancias.Select(m => new clsMercanciaWire(m.IdTipoMercancia, m.Cantidad, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg))]);
        var respuesta = await EnviarAsync(HttpMethod.Post, "/recomendaciones", cuerpo, cancellationToken);
        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var filas = Filas(documento.RootElement, "recomendaciones", "vehiculos");
        if (filas is null)
        {
            return NoDisponible("respuesta de la recomendación sin filas");
        }

        return new clsRecomendacionCubicador([.. filas.Take(MaximoFilas).Select(Vehiculo)]);
    }

    public async Task<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>> ListarTiposMercanciaAsync(CancellationToken cancellationToken = default)
    {
        var minutos = opciones.Value.MinutosCacheCatalogos;
        if (minutos > 0 && cache.Obtener() is { } enCache)
        {
            return clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(enCache);
        }

        var respuesta = await EnviarAsync(HttpMethod.Get, "/tipos-mercancia", null, cancellationToken);
        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var filas = Filas(documento.RootElement, "items", "tiposMercancia");
        if (filas is null)
        {
            return NoDisponible("catálogo de tipos de mercancía sin filas");
        }

        var tipos = new List<clsTipoMercanciaCubicador>();
        var vistos = new HashSet<Guid>();
        foreach (var fila in filas.Take(MaximoFilas))
        {
            var id = Texto(fila, "id", "idTipoMercancia");
            var nombre = Texto(fila, "nombre", "descripcion", "nombreMercancia");
            if (Guid.TryParse(id, out var guid) && guid != Guid.Empty && !string.IsNullOrWhiteSpace(nombre) && vistos.Add(guid))
            {
                tipos.Add(new clsTipoMercanciaCubicador(guid, nombre.Trim()));
            }
        }

        if (tipos.Count == 0)
        {
            return NoDisponible("catálogo de tipos de mercancía vacío");
        }

        IReadOnlyList<clsTipoMercanciaCubicador> lista = [.. tipos.OrderBy(t => t.Nombre, StringComparer.CurrentCultureIgnoreCase)];
        if (minutos > 0)
        {
            cache.Guardar(lista, TimeSpan.FromMinutes(minutos));
        }

        return clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(lista);
    }

    /// <summary><c>{urlBase}{ruta}</c>, con <paramref name="ruta"/> empezando por <c>/</c>.</summary>
    public static Uri ConstruirUrl(string urlBase, string ruta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        return new Uri($"{urlBase.TrimEnd('/')}{ruta}", UriKind.Absolute);
    }

    /// <summary>
    /// Tubería del cliente <see cref="NombreCliente"/>: <paramref name="tiempo"/> por intento; en total, lo que caben el intento y los
    /// <see cref="ReintentosLectura"/> reintentos de un GET con sus esperas (<see cref="TiempoTotal"/>; un POST no se reintenta, así que
    /// su límite real es el del intento). El circuito con una ventana de al menos el doble del intento (lo exige la tubería estándar).
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia, TimeSpan tiempo)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        resiliencia.TotalRequestTimeout.Timeout = TiempoTotal(tiempo);
        resiliencia.AttemptTimeout.Timeout = tiempo;
        resiliencia.Retry.MaxRetryAttempts = ReintentosLectura;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        resiliencia.Retry.DisableForUnsafeHttpMethods();
        resiliencia.CircuitBreaker.SamplingDuration = tiempo * 2 > TimeSpan.FromSeconds(30) ? tiempo * 2 : TimeSpan.FromSeconds(30);
    }

    /// <summary>Límite total de una petición: el intento más los reintentos de un GET y sus esperas (300 ms, 600 ms y holgura del jitter).</summary>
    public static TimeSpan TiempoTotal(TimeSpan intento) => (intento * (ReintentosLectura + 1)) + TimeSpan.FromSeconds(2);

    private async Task<clsResultado<JsonDocument>> EnviarAsync(HttpMethod metodo, string ruta, object? cuerpo, CancellationToken cancellationToken)
    {
        if (!EstaConfigurado)
        {
            return NoDisponible("cubicador sin configurar (Cubicador:UrlBase y Cubicador:ApiKey)");
        }

        using var peticion = new HttpRequestMessage(metodo, ConstruirUrl(_urlBase!, ruta));
        if (cuerpo is not null)
        {
            peticion.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: _json);
        }

        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opciones.Value.ApiKey!.Trim());
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("El cubicador respondió {Estado} a {Metodo} {Ruta}: revisar Cubicador:ApiKey.", (int)respuesta.StatusCode, metodo, ruta);
                // La llave no vale para ninguna ruta: se trata como caída (las demás rutas de la captura no vuelven a llamar).
                return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible, $"HTTP {(int)respuesta.StatusCode}")
                {
                    Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true },
                };
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                // 5xx (o cualquier otro no exitoso que no sea 4xx) es una caída; un 4xx es un rechazo de la petición.
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode}) en {metodo} {ruta}",
                    caida: (int)respuesta.StatusCode is < 400 or >= 500);
            }

            if (respuesta.Content.Headers.ContentLength is { } largo && largo > LimiteBytes)
            {
                return NoDisponible($"respuesta demasiado grande ({largo.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var acotado = await LeerAcotadoAsync(flujo, LimiteBytes, cancellationToken);
            return acotado is null
                ? NoDisponible($"respuesta de más de {LimiteBytes.ToString(CultureInfo.InvariantCulture)} bytes")
                : await JsonDocument.ParseAsync(acotado, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída del cubicador
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            return NoDisponible($"tiempo agotado en {metodo} {ruta}", ex, caida: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or IOException)
        {
            return NoDisponible(ex is Polly.CircuitBreaker.BrokenCircuitException ? "circuito abierto tras fallos repetidos" : $"error de red en {metodo} {ruta}", ex,
                caida: true);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary>Las filas de la respuesta: el arreglo raíz o el de la primera propiedad de <paramref name="contenedores"/> que lo traiga.</summary>
    private static List<JsonElement>? Filas(JsonElement raiz, params string[] contenedores)
    {
        if (raiz.ValueKind == JsonValueKind.Array)
        {
            return [.. raiz.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object)];
        }

        foreach (var nombre in contenedores)
        {
            if (Propiedad(raiz, nombre) is { ValueKind: JsonValueKind.Array } arreglo)
            {
                return [.. arreglo.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object)];
            }
        }

        return null;
    }

    private static clsVehiculoRecomendado Vehiculo(JsonElement fila)
    {
        var tipo = Texto(fila, "tipoVehiculo", "codigoVehiculo", "vehiculo");
        var resultado = Texto(fila, "resultado", "estado")?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(resultado))
        {
            resultado = string.IsNullOrWhiteSpace(tipo) ? clsResultadosRecomendacion.SinSeleccionAutomatica : clsResultadosRecomendacion.Seleccionado;
        }

        var id = Texto(fila, "idRecomendacionVehiculo", "id");
        return new clsVehiculoRecomendado(
            Guid.TryParse(id, out var guid) && guid != Guid.Empty ? guid : null,
            tipo,
            Texto(fila, "carroceria", "tipoCarroceria", "codigoCarroceria"),
            Numero(fila, "pesoTotalKg", "pesoKg", "peso"),
            resultado,
            Texto(fila, "motivo", "mensaje"));
    }

    /// <summary>La primera de <paramref name="nombres"/> presente en <paramref name="objeto"/>, sin distinguir mayúsculas.</summary>
    private static JsonElement? Propiedad(JsonElement objeto, params string[] nombres)
    {
        if (objeto.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var nombre in nombres)
        {
            foreach (var propiedad in objeto.EnumerateObject())
            {
                if (string.Equals(propiedad.Name, nombre, StringComparison.OrdinalIgnoreCase) && propiedad.Value.ValueKind != JsonValueKind.Null)
                {
                    return propiedad.Value;
                }
            }
        }

        return null;
    }

    private static string? Texto(JsonElement objeto, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.String } valor => valor.GetString(),
        { ValueKind: JsonValueKind.Number } valor => valor.GetRawText(),
        _ => null,
    };

    private static decimal? Numero(JsonElement objeto, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.Number } valor when valor.TryGetDecimal(out var numero) => numero,
        { ValueKind: JsonValueKind.String } valor when decimal.TryParse(valor.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var numero) => numero,
        _ => null,
    };

    private static async Task<MemoryStream?> LeerAcotadoAsync(Stream flujo, long limite, CancellationToken cancellationToken)
    {
        var memoria = new MemoryStream();
        var bufer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await flujo.ReadAsync(bufer, cancellationToken)) > 0)
        {
            if (memoria.Length + leidos > limite)
            {
                await memoria.DisposeAsync();
                return null;
            }

            memoria.Write(bufer, 0, leidos);
        }

        memoria.Position = 0;
        return memoria;
    }

    /// <summary>
    /// Error <c>CUBICADOR_NO_DISPONIBLE</c> con el motivo en <see cref="clsErrorDominio.Detalle"/> (solo para el log: quien lo expone por
    /// HTTP lo quita) y, si es una caída, la marca <see cref="clsErroresCubicador.ClaveCaida"/>.
    /// </summary>
    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null, bool caida = false)
    {
        logger.LogWarning("Cubicador no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del cubicador ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible, motivo)
        {
            Datos = caida ? new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true } : null,
        };
    }

    // --- Forma de la petición de POST /recomendaciones (camelCase; contrato provisional, C1) ---

    private sealed record clsRecomendacionWire(string IdLineaSolicitud, string OrigenSistema, IReadOnlyList<clsMercanciaWire> Mercancias);

    private sealed record clsMercanciaWire(Guid? IdTipoMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);
}
