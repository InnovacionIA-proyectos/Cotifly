using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicación rápida contra Nexo en vivo: <c>POST {Nexo:UrlBase}/api/cubicacion</c> — la única llamada síncrona de
/// Nexo al servicio Cubicador (todo lo demás de cubicación en Nexo es asíncrono por colas, no aplica aquí). Nexo no
/// tiene login propio: cada petición identifica al comercial con <c>X-Portal-Usuario-Id: {idUsuarioExterno}</c>
/// (<c>UsuariosWeb.idUsuarioExterno</c>), igual que
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo"/>. Se registra vía
/// <c>clsFabricaCubicacion</c>: sin <c>Nexo:UrlBase</c> configurada se usa <see cref="clsCubicacionNoDisponible"/> en
/// su lugar. Comparte el cliente HTTP con nombre
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente"/> y su misma tubería de
/// resiliencia (mismo servicio Nexo, ver <c>clsDependenciasInfraestructura</c>).
/// </summary>
/// <remarks>
/// <para><b>Unidades.</b> Centímetros y kilogramos (distinto del resto del catálogo "Plan de Cargue" de Nexo, que usa
/// milímetros).</para>
/// <para><b><c>unidad_ids</c>.</b> Siempre <c>null</c>: Cotifly evalúa contra todo el catálogo estándar de vehículos,
/// nunca restringe por Id (complejidad que no se expone al comercial).</para>
/// <para><b>Respuesta reducida.</b> Deserializa el JSON completo de Nexo con DTOs internos que reflejan su forma real,
/// pero descarta <c>matrices</c> (matrices de factibilidad por vehículo) y <c>sugerenciaFlota.disposiciones</c>
/// (colocaciones 3D detalladas): decisión de alcance confirmada, <see cref="clsResultadoCubicacion"/> nunca los
/// expone.</para>
/// <para><b>400 de Nexo.</b> Cuando Cubicador rechaza la solicitud (típicamente por superar el límite de bultos por
/// solicitud, pero también por medidas inválidas: Nexo no distingue el motivo en su propio código de error, siempre
/// <c>CUBICACION_INVALIDA</c>) Nexo responde 400; aquí se traduce a <c>CUBICACION_DEMASIADO_GRANDE</c>, con el
/// mensaje de Nexo como detalle.</para>
/// <para><b>Otros fallos.</b> Nunca lanza: usuario sin <c>idUsuarioExterno</c>, cualquier otro HTTP, red o tiempo
/// agotado devuelven <c>CUBICACION_NO_DISPONIBLE</c> (Result), igual que <see cref="clsCubicacionNoDisponible"/>. Una
/// sola línea de warning por fallo, sin la traza completa ni el header <c>X-Portal-Usuario-Id</c>.</para>
/// </remarks>
public sealed class clsCubicacionNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsCubicacionNexo> logger)
    : IClsCubicacionProvider
{
    public const string Mensaje = "La cubicación de Nexo no está disponible en este momento: intenta de nuevo más tarde.";

    public const string MensajeDemasiadoGrande =
        "La carga no se pudo cubicar: revisa que las medidas sean válidas o divídela en varias solicitudes (límite de bultos por solicitud).";

    private static readonly JsonSerializerOptions _jsonNexo = new(JsonSerializerDefaults.Web);

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurada => _urlBase is not null;

    public async Task<clsResultado<clsResultadoCubicacion>> CubicarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        var cuerpo = new clsCubicarWire(
            [.. items.Select(i => new clsItemCubicacionWire(i.Descripcion, i.Cantidad, i.LargoCm, i.AnchoCm, i.AltoCm, i.PesoKg, null))],
            null,
            claseViaje);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, ConstruirUrl(_urlBase))
        {
            Content = JsonContent.Create(cuerpo, options: _jsonNexo),
        };
        peticion.Headers.Add("X-Portal-Usuario-Id", idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (respuesta.StatusCode == HttpStatusCode.BadRequest)
            {
                var detalle = await LeerDetalleErrorAsync(respuesta, cancellationToken);
                logger.LogWarning("Cubicación rechazada por Nexo/Cubicador (400): {Detalle}", detalle);
                return new clsErrorDominio(clsCodigosError.CubicacionDemasiadoGrande, MensajeDemasiadoGrande, detalle);
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            var resultado = await JsonSerializer.DeserializeAsync<clsCubicacionRapidaResultadoNexo>(flujo, _jsonNexo, cancellationToken);
            return Convertir(resultado);
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

    /// <summary>Sin parámetros de ruta: el cuerpo lleva los ítems a cubicar.</summary>
    public static Uri ConstruirUrl(string urlBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        return new Uri($"{urlBase.TrimEnd('/')}/api/cubicacion", UriKind.Absolute);
    }

    /// <summary>
    /// <c>clsCubicacionRapidaResultado</c> de Nexo, reducido a lo que necesita el frontend: descarta <c>matrices</c>
    /// (no las deserializa siquiera: <see cref="clsCubicacionRapidaResultadoNexo"/> no declara esa propiedad) y
    /// <c>sugerenciaFlota.disposiciones</c> (idem, <see cref="clsSugerenciaFlotaNexo"/> no la declara).
    /// </summary>
    private static clsResultado<clsResultadoCubicacion> Convertir(clsCubicacionRapidaResultadoNexo? resultado)
    {
        if (resultado is null)
        {
            return new clsResultadoCubicacion(null, null, [], null);
        }

        return new clsResultadoCubicacion(
            resultado.Sugerencia is { } s
                ? new clsSugerenciaVehiculo(s.UnidadId, s.UnidadNombre, s.Banda, s.FactorUtilizacion, s.ConfigVehiculo, s.Carroceria, s.ClaseViaje, s.ClaseContenedor)
                : null,
            resultado.SugerenciaFlota is { } f
                ? new clsSugerenciaFlota(f.UnidadId, f.UnidadNombre, f.CantidadVehiculos, f.UtilizacionPromedio, f.ConfigVehiculo, f.Carroceria, f.ClaseViaje, f.ClaseContenedor)
                : null,
            resultado.Advertencias ?? [],
            resultado.Extradimension is { } e
                ? new clsDeteccionExtradimension(
                    e.HayExtradimension,
                    e.MedidasEstandar is { } m ? new clsMedidasEstandar(m.LargoMaxCm, m.AnchoMaxCm, m.AltoMaxCm, m.PesoMaxKg) : new clsMedidasEstandar(0, 0, 0, 0),
                    [.. (e.Piezas ?? []).Select(p => new clsPiezaExcedida(p.Descripcion, p.Excesos ?? [], p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))],
                    e.Sugerencia)
                : null);
    }

    /// <summary><c>clsApiError</c> de Nexo (<c>{code, message, timestamp, details}</c>): usa <c>message</c> si es JSON válido.</summary>
    private static async Task<string> LeerDetalleErrorAsync(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var documento = JsonDocument.Parse(texto);
            if (documento.RootElement.ValueKind == JsonValueKind.Object
                && documento.RootElement.TryGetProperty("message", out var mensaje) && mensaje.ValueKind == JsonValueKind.String)
            {
                return Recortar(mensaje.GetString() ?? texto);
            }
        }
        catch (JsonException)
        {
            // cae al texto recortado
        }

        return Recortar(texto);
    }

    private static string Recortar(string texto) =>
        string.IsNullOrWhiteSpace(texto) ? "(sin cuerpo)"
        : texto.Length <= 400 ? texto.Trim()
        : texto[..400].Trim() + "…";

    private clsResultado<clsResultadoCubicacion> NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Cubicación en Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de la cubicación en Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CubicacionNoDisponible, Mensaje);
    }

    // --- Forma exacta del cuerpo que espera Nexo (camelCase, JsonSerializerDefaults.Web) ---

    private sealed record clsItemCubicacionWire(
        string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg, string? TipoMercancia);

    private sealed record clsCubicarWire(IReadOnlyList<clsItemCubicacionWire> Items, IReadOnlyList<string>? UnidadIds, string? ClaseViaje);

    // --- Espejo de la respuesta real de Nexo (camelCase): SIN "Matrices" ni "Disposiciones" a propósito, para no
    // deserializar (ni cargar en memoria) lo que Cotifly nunca expone. ---

    private sealed record clsSugerenciaVehiculoNexo(
        string UnidadId, string UnidadNombre, string Banda, decimal FactorUtilizacion,
        string? ConfigVehiculo, string? Carroceria, string? ClaseViaje, string? ClaseContenedor);

    private sealed record clsSugerenciaFlotaNexo(
        string UnidadId, string UnidadNombre, int CantidadVehiculos, decimal UtilizacionPromedio,
        string? ConfigVehiculo, string? Carroceria, string? ClaseViaje, string? ClaseContenedor);

    private sealed record clsMedidasEstandarNexo(decimal LargoMaxCm, decimal AnchoMaxCm, decimal AltoMaxCm, decimal PesoMaxKg);

    private sealed record clsPiezaExcedidaNexo(string Descripcion, IReadOnlyList<string>? Excesos, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

    private sealed record clsDeteccionExtradimensionNexo(
        bool HayExtradimension, clsMedidasEstandarNexo? MedidasEstandar, IReadOnlyList<clsPiezaExcedidaNexo>? Piezas, string? Sugerencia);

    private sealed record clsCubicacionRapidaResultadoNexo(
        clsSugerenciaVehiculoNexo? Sugerencia, IReadOnlyList<string>? Advertencias, clsSugerenciaFlotaNexo? SugerenciaFlota, clsDeteccionExtradimensionNexo? Extradimension);
}
