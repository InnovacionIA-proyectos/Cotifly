using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Cubicador falso (HU-021.5, #133): un <see cref="HttpMessageHandler"/> que nunca sale a la red y sirve el contrato provisional de
/// <c>POST /recomendaciones</c> y <c>GET /tipos-mercancia</c> (el cubicador real aún no los tiene, C1). Registra cada petición (método,
/// URL, <c>Authorization</c> y cuerpo) y permite simular las respuestas: vehículos por ruta, "sin selección", "condicionada", caída,
/// demora (para el tiempo agotado) o un cuerpo crudo.
/// </summary>
public sealed class clsCubicadorFalso : HttpMessageHandler
{
    /// <summary>Raíz del API del cubicador en las pruebas.</summary>
    public const string UrlBase = "http://cubicador-test.local/api";

    /// <summary>Llave del cubicador en las pruebas (ficticia).</summary>
    public const string ApiKey = "llave-ficticia-del-cubicador-para-pruebas";

    private readonly ConcurrentQueue<clsPeticionCubicador> _peticiones = new();

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<clsPeticionCubicador> Peticiones => [.. _peticiones];

    /// <summary>Las peticiones a <c>POST /recomendaciones</c>, con su cuerpo leído.</summary>
    public IReadOnlyList<JsonNode> Recomendaciones =>
        [.. Peticiones.Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath.EndsWith("/recomendaciones", StringComparison.Ordinal))
            .Select(p => JsonNode.Parse(p.Cuerpo!)!)];

    /// <summary>Catálogo de tipos de mercancía (<c>GET /tipos-mercancia</c>).</summary>
    public List<(Guid Id, string Nombre)> TiposMercancia { get; } = [];

    /// <summary>Si tiene valor, toda petición responde ese estado (cubicador caído).</summary>
    public HttpStatusCode? Caido { get; set; }

    /// <summary>Si tiene valor, cada petición espera este tiempo antes de responder (para probar el tiempo agotado).</summary>
    public TimeSpan? Demora { get; set; }

    /// <summary>Si tiene valor, solo <c>GET /tipos-mercancia</c> espera este tiempo (catálogo colgado).</summary>
    public TimeSpan? DemoraCatalogo { get; set; }

    /// <summary>
    /// Filas de la recomendación de cada ruta (<c>idTipoMercancia</c> y demás datos de la petición en el argumento). Por defecto, un
    /// vehículo <c>SELECCIONADO</c> (<c>3S3</c> / <c>FURGON</c>) con la suma de los pesos de las mercancías.
    /// </summary>
    public Func<JsonNode, IReadOnlyList<object>>? Recomendar { get; set; }

    /// <summary>Cuerpo crudo de la respuesta de la recomendación (para probar respuestas inválidas); gana sobre <see cref="Recomendar"/>.</summary>
    public string? CuerpoRecomendacion { get; set; }

    /// <summary>Una fila de la Tabla 7 tal como la devuelve el cubicador, con un id nuevo.</summary>
    public static object Fila(string? tipoVehiculo, string? carroceria, decimal? pesoTotalKg, string resultado = "SELECCIONADO", string? motivo = null) =>
        new Dictionary<string, object?>
        {
            ["idRecomendacionVehiculo"] = Guid.NewGuid(),
            ["tipoVehiculo"] = tipoVehiculo,
            ["carroceria"] = carroceria,
            ["pesoTotalKg"] = pesoTotalKg,
            ["resultado"] = resultado,
            ["motivo"] = motivo,
        };

    /// <summary>Los mismos <paramref name="vehiculos"/> para toda ruta.</summary>
    public clsCubicadorFalso RecomendarSiempre(params object[] vehiculos)
    {
        Recomendar = _ => vehiculos;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _peticiones.Enqueue(new clsPeticionCubicador(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), cuerpo));
        if (Demora is { } demora)
        {
            await Task.Delay(demora, cancellationToken);
        }

        if (Caido is { } estado)
        {
            return Json(estado, """{"error":"cubicador caído"}""");
        }

        var ruta = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && ruta.EndsWith("/tipos-mercancia", StringComparison.Ordinal))
        {
            if (DemoraCatalogo is { } colgado)
            {
                await Task.Delay(colgado, cancellationToken);
            }

            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(TiposMercancia.Select(t => new { id = t.Id, nombre = t.Nombre })));
        }

        if (request.Method == HttpMethod.Post && ruta.EndsWith("/recomendaciones", StringComparison.Ordinal))
        {
            if (CuerpoRecomendacion is not null)
            {
                return Json(HttpStatusCode.OK, CuerpoRecomendacion);
            }

            var peticion = JsonNode.Parse(cuerpo!)!;
            var filas = Recomendar?.Invoke(peticion) ?? [Fila("3S3", "FURGON", PesoTotal(peticion))];
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { recomendaciones = filas }));
        }

        return Json(HttpStatusCode.NotFound, """{"error":"ruta desconocida"}""");
    }

    /// <summary>Suma de <c>cantidad × pesoKg</c> de las mercancías de la petición.</summary>
    public static decimal PesoTotal(JsonNode peticion) =>
        peticion["mercancias"]!.AsArray().Sum(m => m!["cantidad"]!.GetValue<int>() * (m["pesoKg"]?.GetValue<decimal>() ?? 0m));

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Petición recibida por <see cref="clsCubicadorFalso"/>. <paramref name="Cuerpo"/> es <c>null</c> en un GET.</summary>
public sealed record clsPeticionCubicador(HttpMethod Metodo, Uri Url, string? Autorizacion, string? Cuerpo);
