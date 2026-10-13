using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Cubicador falso (HU-021.5, #133; contrato de HU-023.1, #156): un <see cref="HttpMessageHandler"/> que nunca sale a la red y sirve
/// <c>POST /recomendaciones</c>, <c>GET /tipos-mercancia</c>, <c>GET /tipos-carroceria</c>, <c>GET /tipos-vehiculo</c> (P1) y
/// <c>GET /health</c> (P7). Registra cada petición (método, URL, <c>Authorization</c> y cuerpo) y permite simular las respuestas: flota
/// mixta, "sin selección" con cada motivo, condicionada, extradimensión, cada error del contrato, caída, demora o un cuerpo crudo. Los
/// cuerpos de cada caso del contrato (petición P2) están en <see cref="clsEjemplosContratoCubicador"/>.
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
    public List<clsTipoMercanciaFalso> TiposMercancia { get; } = [];

    /// <summary>Catálogo de carrocerías (<c>GET /tipos-carroceria</c>).</summary>
    public List<(string Codigo, string Nombre)> TiposCarroceria { get; } = [("FURGON", "Furgón"), ("ESTACAS", "Estacas"), ("CAMA_BAJA", "Cama baja")];

    /// <summary>Catálogo de vehículos (<c>GET /tipos-vehiculo</c>); con <see cref="SinTiposVehiculo"/> la ruta responde 404 (P1 sin publicar).</summary>
    public List<(string Codigo, string Nombre)> TiposVehiculo { get; } =
        [("2S2", "Tractocamión 2S2"), ("2S3", "Tractocamión 2S3"), ("3S3", "Tractocamión 3S3"), ("C2", "Sencillo C2"), ("C3", "Dobletroque C3")];

    /// <summary>El cubicador todavía no publica <c>GET /tipos-vehiculo</c> (404).</summary>
    public bool SinTiposVehiculo { get; set; }

    /// <summary>Si tiene valor, toda petición responde ese estado (cubicador caído).</summary>
    public HttpStatusCode? Caido { get; set; }

    /// <summary>Si tiene valor, cada petición espera este tiempo antes de responder (para probar el tiempo agotado).</summary>
    public TimeSpan? Demora { get; set; }

    /// <summary>Si tiene valor, solo los catálogos esperan este tiempo (catálogo colgado).</summary>
    public TimeSpan? DemoraCatalogo { get; set; }

    /// <summary>
    /// Respuesta de la recomendación de cada ruta (el cuerpo de la petición en el argumento). Por defecto, <see cref="Asignado"/> con un
    /// vehículo <c>3S3</c> / <c>FURGON</c> y la suma de los pesos de las mercancías.
    /// </summary>
    public Func<JsonNode, object>? Recomendar { get; set; }

    /// <summary>Cuerpo crudo de la respuesta de la recomendación (para probar respuestas inválidas); gana sobre <see cref="Recomendar"/>.</summary>
    public string? CuerpoRecomendacion { get; set; }

    /// <summary>Si tiene valor, la recomendación responde ese error con <c>{codigo, mensaje}</c>.</summary>
    public (HttpStatusCode Estado, string Codigo)? ErrorRecomendacion { get; set; }

    /// <summary>Un vehículo de la respuesta <c>ASIGNADO</c>, con un id nuevo.</summary>
    public static Dictionary<string, object?> Vehiculo(
        string codigoVehiculo, string codigoCarroceria, decimal pesoTotalKg, decimal volumenTotalM3 = 40m,
        decimal porcentajeLlenadoVolumen = 80m, decimal porcentajeLlenadoPeso = 70m) =>
        new()
        {
            ["idRecomendacionVehiculo"] = Guid.NewGuid(),
            ["codigoVehiculo"] = codigoVehiculo,
            ["codigoCarroceria"] = codigoCarroceria,
            ["pesoTotalKg"] = pesoTotalKg,
            ["volumenTotalM3"] = volumenTotalM3,
            ["porcentajeLlenadoVolumen"] = porcentajeLlenadoVolumen,
            ["porcentajeLlenadoPeso"] = porcentajeLlenadoPeso,
        };

    /// <summary>Respuesta <c>ASIGNADO</c> con estos vehículos (flota mixta si son de tipos distintos).</summary>
    public static Dictionary<string, object?> Asignado(params object[] vehiculos) => Respuesta("ASIGNADO", null, vehiculos);

    /// <summary>Respuesta <c>SIN_SELECCION_AUTOMATICA</c> con su motivo y sin vehículos.</summary>
    public static Dictionary<string, object?> SinSeleccion(string motivo) => Respuesta("SIN_SELECCION_AUTOMATICA", motivo, []);

    /// <summary>La misma respuesta marcada como condicionada, con las mercancías que la condicionan.</summary>
    public static Dictionary<string, object?> Condicionada(Dictionary<string, object?> respuesta, params string[] codigos)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        respuesta["condicionada"] = true;
        respuesta["mercanciasCondicionadas"] = codigos.Select(c => new { codigoMercancia = c, comentarioCondicion = "Requiere aprobación" }).ToList();
        return respuesta;
    }

    /// <summary>Respuesta completa del contrato.</summary>
    public static Dictionary<string, object?> Respuesta(string resultado, string? motivo, IEnumerable<object> vehiculos) =>
        new()
        {
            ["idRecomendacion"] = Guid.NewGuid(),
            ["resultado"] = resultado,
            ["motivoSinSeleccion"] = motivo,
            ["condicionada"] = false,
            ["mercanciasCondicionadas"] = Array.Empty<object>(),
            ["vehiculos"] = vehiculos.ToList(),
            ["advertencias"] = Array.Empty<object>(),
            ["extradimension"] = null,
        };

    /// <summary>Los mismos <paramref name="vehiculos"/> (<see cref="Asignado"/>) para toda ruta.</summary>
    public clsCubicadorFalso RecomendarSiempre(params object[] vehiculos)
    {
        Recomendar = _ => Asignado(vehiculos);
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
            return Json(estado, """{"codigo":"CUBICADOR_NO_DISPONIBLE","mensaje":"cubicador caído"}""");
        }

        var ruta = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && ruta.EndsWith("/health", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"estado":"ok"}""");
        }

        if (request.Method == HttpMethod.Get && ruta.EndsWith("/tipos-mercancia", StringComparison.Ordinal))
        {
            await ColgarCatalogoAsync(cancellationToken);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(TiposMercancia.Select(t => new
            {
                id = t.Id,
                codigoMercancia = t.Codigo,
                nombre = t.Nombre,
                categoria = t.Categoria,
                cubicable = t.Cubicable,
                condicionadaCubicacion = t.Condicionada,
                comentarioCondicion = t.Condicionada ? "Requiere aprobación de AdminTarifas" : null,
                activo = t.Activo,
            })));
        }

        if (request.Method == HttpMethod.Get && ruta.EndsWith("/tipos-carroceria", StringComparison.Ordinal))
        {
            await ColgarCatalogoAsync(cancellationToken);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(TiposCarroceria.Select(t => new { codigoCarroceria = t.Codigo, nombre = t.Nombre, activo = true })));
        }

        if (request.Method == HttpMethod.Get && ruta.EndsWith("/tipos-vehiculo", StringComparison.Ordinal))
        {
            await ColgarCatalogoAsync(cancellationToken);
            return SinTiposVehiculo
                ? Json(HttpStatusCode.NotFound, """{"codigo":"NO_ENCONTRADO","mensaje":"ruta desconocida"}""")
                : Json(HttpStatusCode.OK, JsonSerializer.Serialize(TiposVehiculo.Select(t => new
                {
                    codigoVehiculo = t.Codigo,
                    nombreVehiculo = t.Nombre,
                    carrocerias = TiposCarroceria.Select(c => c.Codigo).ToList(),
                    activo = true,
                })));
        }

        if (request.Method == HttpMethod.Post && ruta.EndsWith("/recomendaciones", StringComparison.Ordinal))
        {
            if (ErrorRecomendacion is { } error)
            {
                return Json(error.Estado, JsonSerializer.Serialize(new { codigo = error.Codigo, mensaje = $"Error {error.Codigo} del cubicador falso." }));
            }

            if (CuerpoRecomendacion is not null)
            {
                return Json(HttpStatusCode.OK, CuerpoRecomendacion);
            }

            var peticion = JsonNode.Parse(cuerpo!)!;
            var respuesta = Recomendar?.Invoke(peticion) ?? Asignado(Vehiculo("3S3", "FURGON", PesoTotal(peticion)));
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(respuesta));
        }

        return Json(HttpStatusCode.NotFound, """{"codigo":"NO_ENCONTRADO","mensaje":"ruta desconocida"}""");
    }

    /// <summary>Suma de <c>cantidad × pesoKg</c> de las mercancías de la petición.</summary>
    public static decimal PesoTotal(JsonNode peticion) =>
        peticion["mercancias"]!.AsArray().Sum(m => m!["cantidad"]!.GetValue<int>() * (m["pesoKg"]?.GetValue<decimal>() ?? 0m));

    private async Task ColgarCatalogoAsync(CancellationToken cancellationToken)
    {
        if (DemoraCatalogo is { } colgado)
        {
            await Task.Delay(colgado, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// Un tipo de mercancía del cubicador falso. Desde una tupla <c>(id, nombre)</c> el código se deriva del nombre (sin tildes, en
/// mayúsculas y con guiones bajos: "Café en estibas" → <c>CAFE_EN_ESTIBAS</c>).
/// </summary>
public sealed record clsTipoMercanciaFalso(
    string Codigo, string Nombre, Guid? Id = null, string? Categoria = "GENERAL", bool Cubicable = true, bool Condicionada = false, bool Activo = true)
{
    public static implicit operator clsTipoMercanciaFalso((Guid Id, string Nombre) tipo) => new(CodigoDe(tipo.Nombre), tipo.Nombre, tipo.Id);

    /// <summary>El código que el falso deriva de un nombre.</summary>
    public static string CodigoDe(string nombre)
    {
        ArgumentNullException.ThrowIfNull(nombre);
        var sinTildes = new string([.. nombre.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]);
        return string.Join('_', sinTildes.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Petición recibida por <see cref="clsCubicadorFalso"/>. <paramref name="Cuerpo"/> es <c>null</c> en un GET.</summary>
public sealed record clsPeticionCubicador(HttpMethod Metodo, Uri Url, string? Autorizacion, string? Cuerpo);

/// <summary>
/// Ejemplos de respuesta de cada caso del contrato del cubicador (petición P2: mientras el cubicador no los publique, se arman con la
/// hoja de ruta del 2026-10-08, sección 2). Cuando lleguen los reales, se reemplazan aquí y las pruebas del adaptador los validan.
/// </summary>
public static class clsEjemplosContratoCubicador
{
    /// <summary><c>ASIGNADO</c> con flota mixta (dos vehículos de tipos distintos).</summary>
    public const string FlotaMixta = """
        {"idRecomendacion":"0b9c3f0e-6d2a-4c1b-9e7f-1a2b3c4d5e6f","resultado":"ASIGNADO","motivoSinSeleccion":null,"condicionada":false,
         "mercanciasCondicionadas":[],
         "vehiculos":[
           {"idRecomendacionVehiculo":"5f0e1d2c-3b4a-4968-8776-655443322110","codigoVehiculo":"3S3","nombreVehiculo":"Tractocamión 3S3","codigoCarroceria":"FURGON","pesoTotalKg":16000,"volumenTotalM3":62.5,"porcentajeLlenadoVolumen":81.25,"porcentajeLlenadoPeso":47.06},
           {"idRecomendacionVehiculo":"6a1f2e3d-4c5b-4a69-8877-665544332211","codigoVehiculo":"C2","codigoCarroceria":"ESTACAS","pesoTotalKg":4500.5,"volumenTotalM3":18.25,"porcentajeLlenadoVolumen":60,"porcentajeLlenadoPeso":75.01}],
         "advertencias":[{"codigo":"REFRIGERADO","mensaje":"La mercancía refrigerada va en FURGON: confirmar el equipo de frío."}],
         "extradimension":null}
        """;

    /// <summary><c>SIN_SELECCION_AUTOMATICA</c> con el motivo dado y la marca de extradimensión si aplica.</summary>
    public static string SinSeleccion(string motivo)
    {
        var extradimension = motivo == "EXTRADIMENSIONADO"
            ? """{"extradimensionado":true,"dimensiones":["ALTO"],"detalle":"Supera el alto máximo de la flota."}"""
            : "null";
        return $$"""
            {"idRecomendacion":"1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f","resultado":"SIN_SELECCION_AUTOMATICA","motivoSinSeleccion":"{{motivo}}",
             "condicionada":false,"mercanciasCondicionadas":[],"vehiculos":[],"advertencias":[],"extradimension":{{extradimension}}}
            """;
    }

    /// <summary><c>ASIGNADO</c> condicionada (la ruta va a la Mesa de Costeo), con la mercancía que la condiciona.</summary>
    public const string Condicionada = """
        {"idRecomendacion":"2d3e4f5a-6b7c-4d8e-9f0a-1b2c3d4e5f6a","resultado":"ASIGNADO","condicionada":true,
         "mercanciasCondicionadas":[{"codigoMercancia":"QUIMICOS","comentarioCondicion":"Requiere aprobación de AdminTarifas"}],
         "vehiculos":[{"idRecomendacionVehiculo":"7b2a3f4e-5d6c-4b7a-9988-776655443322","codigoVehiculo":"2S2","codigoCarroceria":"FURGON","pesoTotalKg":9000,"volumenTotalM3":30,"porcentajeLlenadoVolumen":55.5,"porcentajeLlenadoPeso":40}],
         "advertencias":["Mercancía condicionada: la Mesa de Costeo debe aprobarla."],"extradimension":false}
        """;

    /// <summary>Un error del contrato: <c>{codigo, mensaje}</c>.</summary>
    public static string Error(string codigo) => $$"""{"codigo":"{{codigo}}","mensaje":"Mensaje del cubicador para {{codigo}}."}""";

    /// <summary>Los cuatro motivos de "sin selección".</summary>
    public static readonly IReadOnlyList<string> Motivos = ["EXTRADIMENSIONADO", "SIN_CANDIDATOS_DIMENSIONALES", "SUPERA_K_MAX", "SIN_CARROCERIA_COMPATIBLE"];

    /// <summary>Los cuatro rechazos 400 del contrato.</summary>
    public static readonly IReadOnlyList<string> Rechazos = ["MERCANCIAS_REQUERIDAS", "MERCANCIA_DESCONOCIDA", "MERCANCIA_NO_CUBICABLE", "DIMENSIONES_INVALIDAS"];
}
