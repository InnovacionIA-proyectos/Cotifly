using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Cubicador falso: un <see cref="HttpMessageHandler"/> que nunca sale a la red y sirve
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

    /// <summary>Catálogo de vehículos (<c>GET /vehiculos-catalogo</c>); con <see cref="SinTiposVehiculo"/> la ruta responde 404.</summary>
    public List<(string Codigo, string Nombre)> TiposVehiculo { get; } =
        [("2S2", "Tractocamión 2S2"), ("2S3", "Tractocamión 2S3"), ("3S3", "Tractocamión 3S3"), ("C2", "Sencillo C2"), ("C3", "Dobletroque C3")];

    /// <summary>El catálogo <c>GET /vehiculos-catalogo</c> responde 404.</summary>
    public bool SinTiposVehiculo { get; set; }

    /// <summary>
    /// Corridas guardadas por <c>idLineaSolicitud</c> (la última gana), las que devuelve
    /// <c>GET /recomendaciones?idLineaSolicitud=…&amp;origenSistema=COTIFLY</c>. Cada <c>POST</c> que responde guarda la suya; con
    /// <see cref="TiempoAgotadoTrasGuardar"/> el POST guarda la corrida y después no responde (se cuelga hasta la cancelación).
    /// </summary>
    public ConcurrentDictionary<string, string> Corridas { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El POST guarda la corrida y se cuelga (simula un tiempo agotado con la corrida ya creada).</summary>
    public bool TiempoAgotadoTrasGuardar { get; set; }

    /// <summary>El POST se cuelga SIN guardar la corrida (la reconsulta no encuentra nada: 404).</summary>
    public bool TiempoAgotadoSinGuardar { get; set; }

    /// <summary>Las peticiones a <c>GET /recomendaciones</c> (reconsultas).</summary>
    public IReadOnlyList<clsPeticionCubicador> Reconsultas =>
        [.. Peticiones.Where(p => p.Metodo == HttpMethod.Get && p.Url.AbsolutePath.EndsWith("/recomendaciones", StringComparison.Ordinal))];

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

    /// <summary>Cuerpo crudo del error de <see cref="ErrorRecomendacion"/> (por ejemplo el <c>{"detail": [...]}</c> de un 422 de FastAPI).</summary>
    public string? CuerpoErrorRecomendacion { get; set; }

    /// <summary>Error de la recomendación según la petición (por ejemplo, una sola ruta); <c>null</c> = responde normal.</summary>
    public Func<JsonNode, (HttpStatusCode Estado, string Codigo)?>? ErrorPara { get; set; }

    /// <summary>Espera de cada <c>POST /recomendaciones</c> (para medir cuántas llamadas hay en paralelo).</summary>
    public TimeSpan? DemoraRecomendacion { get; set; }

    /// <summary>
    /// Opción C/D: si tiene valor, cada <c>POST /recomendaciones</c> espera a que se complete (simula el motor calculando una carga grande
    /// el tiempo que la prueba quiera). <see cref="RecomendacionesIniciadas"/> cuenta los POST que ya llegaron.
    /// </summary>
    public TaskCompletionSource? CompuertaRecomendacion { get; set; }

    /// <summary>Opción C: espera de cada reconsulta <c>GET /recomendaciones</c> (el cubicador la responde cuando el motor termina).</summary>
    public TimeSpan? DemoraReconsulta { get; set; }

    private int _iniciadas;

    /// <summary>POST a <c>/recomendaciones</c> que ya llegaron al falso (estén o no esperando la compuerta).</summary>
    public int RecomendacionesIniciadas => Volatile.Read(ref _iniciadas);

    /// <summary>Crea una compuerta nueva (cerrada) y la deja en <see cref="CompuertaRecomendacion"/>.</summary>
    public TaskCompletionSource CerrarCompuerta()
    {
        CompuertaRecomendacion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return CompuertaRecomendacion;
    }

    private int _enCurso;
    private int _maximoEnCurso;

    /// <summary>Máximo de <c>POST /recomendaciones</c> en curso a la vez que ha visto el falso (petición P6).</summary>
    public int MaximoEnParalelo => Volatile.Read(ref _maximoEnCurso);

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

    /// <summary>Un vehículo con los campos del contrato: número, nombre, alertas y lo que lleva por posición.</summary>
    public static Dictionary<string, object?> VehiculoCompleto(
        int numero, string codigoVehiculo, string nombreVehiculo, string codigoCarroceria, decimal pesoTotalKg, bool alertaBajoLlenado,
        bool heterogenea, params object[] mercancias)
    {
        var vehiculo = Vehiculo(codigoVehiculo, codigoCarroceria, pesoTotalKg);
        vehiculo["numeroVehiculo"] = numero;
        vehiculo["nombreVehiculo"] = nombreVehiculo;
        vehiculo["alertaBajoLlenado"] = alertaBajoLlenado;
        vehiculo["heterogenea"] = heterogenea;
        vehiculo["mercancias"] = mercancias.ToList();
        return vehiculo;
    }

    /// <summary>Una fila de <c>vehiculos[].mercancias[]</c> (<paramref name="indicePosicion"/> puede ir en <c>null</c>).</summary>
    public static Dictionary<string, object?> MercanciaVehiculo(int? indicePosicion, string codigo, int cantidad, decimal pesoKg, decimal volumenM3) =>
        new()
        {
            ["indicePosicion"] = indicePosicion,
            ["idTipoMercancia"] = "2B21A9C4-7E55-4D7B-9A10-3C1F0E2D4B6A",
            ["codigoMercancia"] = codigo,
            ["cantidad"] = cantidad,
            ["pesoKg"] = pesoKg,
            ["volumenM3"] = volumenM3,
        };

    /// <summary>Respuesta <c>SIN_SELECCION_AUTOMATICA</c> con su motivo y sin vehículos.</summary>
    public static Dictionary<string, object?> SinSeleccion(string motivo) => Respuesta("SIN_SELECCION_AUTOMATICA", motivo, []);

    /// <summary>La misma respuesta marcada como condicionada, con las mercancías que la condicionan.</summary>
    public static Dictionary<string, object?> Condicionada(Dictionary<string, object?> respuesta, params string[] codigos)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        respuesta["condicionada"] = true;
        // Guía del cubicador §5.4: una lista de códigos; el comentario sale de GET /tipos-mercancia.
        respuesta["mercanciasCondicionadas"] = codigos.ToList();
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

        if (request.Method == HttpMethod.Get && ruta.EndsWith("/recomendaciones", StringComparison.Ordinal))
        {
            var consulta = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            if (DemoraReconsulta is { } demoraReconsulta)
            {
                await Task.Delay(demoraReconsulta, cancellationToken);
            }

            if (!string.Equals(consulta["origenSistema"], "COTIFLY", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.UnprocessableEntity, """{"detail":"origenSistema es obligatorio"}""");
            }

            return consulta["idLineaSolicitud"] is { } linea && Corridas.TryGetValue(linea, out var corrida)
                ? Json(HttpStatusCode.OK, corrida)
                : Json(HttpStatusCode.NotFound, """{"codigo":"NO_ENCONTRADO","mensaje":"sin corrida para la línea"}""");
        }

        if (request.Method == HttpMethod.Get && ruta.EndsWith("/vehiculos-catalogo", StringComparison.Ordinal))
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
            var enCurso = Interlocked.Increment(ref _enCurso);
            Interlocked.Increment(ref _iniciadas);
            try
            {
                for (var maximo = Volatile.Read(ref _maximoEnCurso); enCurso > maximo; maximo = Volatile.Read(ref _maximoEnCurso))
                {
                    if (Interlocked.CompareExchange(ref _maximoEnCurso, enCurso, maximo) == maximo)
                    {
                        break;
                    }
                }

                if (DemoraRecomendacion is { } espera)
                {
                    await Task.Delay(espera, cancellationToken);
                }

                if (CompuertaRecomendacion is { } compuerta)
                {
                    await compuerta.Task.WaitAsync(cancellationToken);
                }

                if (TiempoAgotadoSinGuardar)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                var respuesta = Recomendacion(cuerpo);
                if (respuesta.StatusCode == HttpStatusCode.OK && JsonNode.Parse(cuerpo!)?["idLineaSolicitud"]?.GetValue<string>() is { } idLinea)
                {
                    var guardada = await respuesta.Content.ReadAsStringAsync(cancellationToken);
                    if (JsonNode.Parse(guardada) is JsonObject nodo)
                    {
                        nodo["idLineaSolicitud"] = idLinea;
                        nodo["origenSistema"] = "COTIFLY";
                        Corridas[idLinea] = nodo.ToJsonString();
                    }
                }

                if (TiempoAgotadoTrasGuardar)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return respuesta;
            }
            finally
            {
                Interlocked.Decrement(ref _enCurso);
            }
        }

        return Json(HttpStatusCode.NotFound, """{"codigo":"NO_ENCONTRADO","mensaje":"ruta desconocida"}""");
    }

    private HttpResponseMessage Recomendacion(string? cuerpo)
    {
        if (ErrorPara?.Invoke(JsonNode.Parse(cuerpo!)!) is { } puntual)
        {
            return Json(puntual.Estado, JsonSerializer.Serialize(new { codigo = puntual.Codigo, mensaje = $"Error {puntual.Codigo} del cubicador falso." }));
        }

        if (ErrorRecomendacion is { } error)
        {
            return Json(error.Estado, CuerpoErrorRecomendacion
                ?? JsonSerializer.Serialize(new { codigo = error.Codigo, mensaje = $"Error {error.Codigo} del cubicador falso." }));
        }

        if (CuerpoRecomendacion is not null)
        {
            return Json(HttpStatusCode.OK, CuerpoRecomendacion);
        }

        var peticion = JsonNode.Parse(cuerpo!)!;
        var respuesta = Recomendar?.Invoke(peticion) ?? Asignado(Vehiculo("3S3", "FURGON", PesoTotal(peticion)));
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(respuesta));
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
/// Ejemplos de respuesta de cada caso del contrato del cubicador (petición P2), armados con la
/// hoja de ruta del contrato (sección 2). Las pruebas del adaptador los validan.
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

    /// <summary><c>SIN_SELECCION_AUTOMATICA</c> con el motivo dado y la extradimensión (estructura vigente) si aplica.</summary>
    public static string SinSeleccion(string motivo)
    {
        var extradimension = motivo == "EXTRADIMENSIONADO"
            ? """{"hayExtradimension":true,"medidasEstandar":{"largoMaxCm":1220,"anchoMaxCm":260,"altoMaxCm":300,"pesoMaxKg":34000},"piezas":[{"indicePosicion":0,"idTipoMercancia":null,"codigoMercancia":"VIGAS_ESTRUCTURAS","excesos":["alto 320.0 cm > 300.0 cm"]}]}"""
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

    /// <summary>Los cinco motivos de "sin selección" (con BUSQUEDA_AGOTADA).</summary>
    public static readonly IReadOnlyList<string> Motivos =
        ["EXTRADIMENSIONADO", "SIN_CANDIDATOS_DIMENSIONALES", "SUPERA_K_MAX", "SIN_CARROCERIA_COMPATIBLE", "BUSQUEDA_AGOTADA"];

    /// <summary>Los rechazos 400 del contrato (CUBICACION_DEMASIADO_GRANDE).</summary>
    public static readonly IReadOnlyList<string> Rechazos =
        ["MERCANCIAS_REQUERIDAS", "MERCANCIA_DESCONOCIDA", "MERCANCIA_NO_CUBICABLE", "DIMENSIONES_INVALIDAS", "CUBICACION_DEMASIADO_GRANDE"];

    /// <summary>
    /// Ejemplo exacto de la guía del cubicador §5.1 (<c>ASIGNADO</c>), completado con un segundo camión de otro tipo (flota mixta) y con
    /// <c>CAJAS_CARTON</c> repetido en dos filas del mismo camión (<c>indicePosicion</c> 0 y 1).
    /// </summary>
    public const string GuiaAsignadoFlotaMixta = """
        {
          "idRecomendacion": "C17B9E44-2A31-4F6B-9D02-1E8C3A5F7B64",
          "idLineaSolicitud": "8f3a1c20-5b7d-4e8a-9c21-7d4e5f6a8b90",
          "origenSistema": "COTIFLY",
          "resultado": "ASIGNADO",
          "motivoSinSeleccion": null,
          "condicionada": false,
          "mercanciasCondicionadas": [],
          "vehiculos": [
            {
              "idRecomendacionVehiculo": "A4D2E6F8-1B3C-4D5E-8F90-A1B2C3D4E5F6", "numeroVehiculo": 1,
              "codigoVehiculo": "3S3", "nombreVehiculo": "Tractomula 3S3", "codigoCarroceria": "FURGON",
              "pesoTotalKg": 10900.0, "volumenTotalM3": 31.2,
              "porcentajeLlenadoVolumen": 90.0, "porcentajeLlenadoPeso": 31.0,
              "alertaBajoLlenado": false, "heterogenea": false,
              "mercancias": [
                { "indicePosicion": 0, "idTipoMercancia": "2B21A9C4-7E55-4D7B-9A10-3C1F0E2D4B6A", "codigoMercancia": "CAJAS_CARTON", "cantidad": 500, "pesoKg": 10000.0, "volumenM3": 18.0 },
                { "indicePosicion": 1, "idTipoMercancia": "2B21A9C4-7E55-4D7B-9A10-3C1F0E2D4B6A", "codigoMercancia": "CAJAS_CARTON", "cantidad": 300, "pesoKg": 900.0,   "volumenM3": 13.2 }
              ]
            },
            {
              "idRecomendacionVehiculo": "B5E3F7A9-2C4D-4E6F-9A01-B2C3D4E5F6A7", "numeroVehiculo": 2,
              "codigoVehiculo": "C2", "nombreVehiculo": "Sencillo C2", "codigoCarroceria": "ESTACAS",
              "pesoTotalKg": 2400.0, "volumenTotalM3": 9.6,
              "porcentajeLlenadoVolumen": 40.0, "porcentajeLlenadoPeso": 24.0,
              "alertaBajoLlenado": true, "heterogenea": false,
              "mercancias": [
                { "indicePosicion": 2, "idTipoMercancia": "3C32BAD5-8F66-4E8C-AB21-4D2F1F3E5C7B", "codigoMercancia": "BEBIDAS", "cantidad": 120, "pesoKg": 2400.0, "volumenM3": 9.6 }
              ]
            }
          ],
          "advertencias": [],
          "extradimension": null
        }
        """;

    /// <summary>Ejemplo exacto de la guía del cubicador §5.3 (<c>EXTRADIMENSIONADO</c> con la estructura vigente de <c>extradimension</c>).</summary>
    public const string GuiaExtradimension = """
        { "resultado": "SIN_SELECCION_AUTOMATICA", "motivoSinSeleccion": "EXTRADIMENSIONADO", "vehiculos": [],
          "advertencias": ["2 pieza(s) exceden las medidas estándar."],
          "extradimension": { "hayExtradimension": true,
            "medidasEstandar": { "largoMaxCm": 1220, "anchoMaxCm": 260, "altoMaxCm": 300, "pesoMaxKg": 34000 },
            "piezas": [ { "indicePosicion": 1, "idTipoMercancia": "9F2C4A6E-1D3B-4F5A-8C7E-0B1D2F3A4C5E", "codigoMercancia": "VIGAS_ESTRUCTURAS",
                          "excesos": ["largo 1400.0 cm > 1220.0 cm"] } ] } }
        """;

    /// <summary>Ejemplo de la guía §5.4: <c>ASIGNADO</c> condicionada, con la lista de códigos (el comentario sale de <c>/tipos-mercancia</c>).</summary>
    public const string GuiaCondicionada = """
        {"idRecomendacion":"D28CAF55-3B42-4A7C-8E13-2F9D4B6A8C75","resultado":"ASIGNADO","motivoSinSeleccion":null,"condicionada":true,
         "mercanciasCondicionadas":["BOBINAS_ACERO"],
         "vehiculos":[{"idRecomendacionVehiculo":"E39DB066-4C53-4B8D-9F24-3A0E5C7B9D86","numeroVehiculo":1,"codigoVehiculo":"3S3","codigoCarroceria":"ESTACAS",
           "pesoTotalKg":24000,"volumenTotalM3":20,"porcentajeLlenadoVolumen":30,"porcentajeLlenadoPeso":70.6,"alertaBajoLlenado":false,"heterogenea":false,
           "mercancias":[{"indicePosicion":0,"idTipoMercancia":"4D43CBE6-9A77-4F9D-BC32-5E3A2A4F6D8C","codigoMercancia":"BOBINAS_ACERO","cantidad":3,"pesoKg":24000,"volumenM3":20}]}],
         "advertencias":[],"extradimension":null}
        """;
}
