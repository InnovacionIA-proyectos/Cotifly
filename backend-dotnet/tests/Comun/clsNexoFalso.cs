using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Nexo falso con estado (ADR-015) para las pruebas por HTTP y los escenarios BDD: un <see cref="HttpMessageHandler"/> que nunca
/// sale a la red y responde con la forma de <c>CONTRATO.md</c> de Nexo las rutas que usa Cotifly: búsqueda de clientes, alta de
/// solicitudes, consulta de la solicitud, "crear o recuperar" la cotización y marcar el resultado. Las pruebas cambian el estado
/// (costear, entregar, descartar, caer) como lo haría la Mesa de Nexo. Seguro para varios hilos.
/// </summary>
public sealed class clsNexoFalso : HttpMessageHandler
{
    /// <summary>Raíz del servicio en las pruebas (sin <c>/api</c>, ADR-015 §8).</summary>
    public const string UrlBase = clsNexoClientesFalso.UrlBase;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly object _candado = new();
    private readonly ConcurrentQueue<clsPeticionNexo> _peticiones = new();
    private readonly Dictionary<string, clsSolicitudFalsa> _solicitudes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Nit, string Nombre, string Origen, string? Bp)> _clientes = [];

    /// <summary>Si tiene valor, TODAS las peticiones responden este estado (Nexo caído o rechazando).</summary>
    public HttpStatusCode? Caido { get; set; }

    /// <summary>Si tiene valor, solo el alta responde este estado (con un cuerpo de error de Nexo).</summary>
    public HttpStatusCode? AltaFalla { get; set; }

    /// <summary>Si es verdadero, el alta auto-costea la línea con <see cref="FleteAutocosteo"/> (como la Biblioteca de Nexo).</summary>
    public bool AutoCostear { get; set; }

    public decimal FleteAutocosteo { get; set; } = 2_550_000m;

    public IReadOnlyList<clsPeticionNexo> Peticiones => [.. _peticiones];

    /// <summary>Agrega un cliente (o prospecto sin BP) al catálogo de <c>GET /api/captura-solicitudes/clientes</c>.</summary>
    public clsNexoFalso ConCliente(string nit, string nombre, string origen = "cliente", string? bp = "15927")
    {
        lock (_candado)
        {
            _clientes.Add((nit, nombre, origen, bp));
        }

        return this;
    }

    /// <summary>Ids de las solicitudes creadas, en orden.</summary>
    public IReadOnlyList<string> SolicitudesCreadas
    {
        get
        {
            lock (_candado)
            {
                return [.. _solicitudes.Values.OrderBy(s => s.Orden).Select(s => s.Id)];
            }
        }
    }

    /// <summary>Cuerpo JSON del último alta recibido.</summary>
    public JsonNode? UltimaAlta { get; private set; }

    /// <summary>La Mesa de Nexo agrega el flete (y opcionalmente aprovisionamiento/retorno) a la línea de la solicitud.</summary>
    public void Costear(string solicitudId, decimal flete, decimal? aprovisionamiento = null, decimal? retorno = null) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Flete = flete;
            s.Cotizacion.Aprovisionamiento = aprovisionamiento;
            s.Cotizacion.Retorno = retorno;
            s.Estado = "EnCosteo";
        });

    /// <summary>Agrega un servicio conexo a la cotización de Nexo.</summary>
    public void AgregarConexo(string solicitudId, string nombre, decimal valorAplicado, string categoria = "logistico", decimal cantidad = 1m) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Conexos.Add((Guid.NewGuid().ToString(), nombre, categoria, cantidad, valorAplicado));
        });

    /// <summary><c>POST /api/cotizaciones/{id}/entregar</c> de la Mesa: cotización y solicitud en <c>Entregada</c>.</summary>
    public void Entregar(string solicitudId) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Estado = "Entregada";
            s.Estado = "Entregada";
        });

    public void Descartar(string solicitudId) => Con(solicitudId, s => s.Estado = "Descartada");

    /// <summary>Olvida la solicitud (Nexo responde 404 "id no reconocido").</summary>
    public void Olvidar(string solicitudId)
    {
        lock (_candado)
        {
            _solicitudes.Remove(solicitudId);
        }
    }

    /// <summary>Resultado registrado en la cotización de Nexo de la solicitud (<c>SinDefinir</c> si no hay).</summary>
    public string Resultado(string solicitudId)
    {
        lock (_candado)
        {
            return _solicitudes.TryGetValue(solicitudId, out var s) ? s.Cotizacion?.Resultado ?? "SinDefinir" : "SinDefinir";
        }
    }

    /// <summary>Id de la cotización de Nexo de la solicitud (o <c>null</c> si todavía no hay).</summary>
    public string? CotizacionDe(string solicitudId)
    {
        lock (_candado)
        {
            return _solicitudes.TryGetValue(solicitudId, out var s) ? s.Cotizacion?.Id : null;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _peticiones.Enqueue(new clsPeticionNexo(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("X-Portal-Usuario-Id", out var valores) ? valores.FirstOrDefault() : null,
            request.Headers.Accept.ToString(),
            cuerpo));

        if (Caido is { } caido)
        {
            return Error(caido, "NEXO_CAIDO", "Nexo no disponible (prueba).");
        }

        var ruta = request.RequestUri!.AbsolutePath.TrimEnd('/');
        lock (_candado)
        {
            return (request.Method.Method, ruta) switch
            {
                ("GET", "/api/captura-solicitudes/clientes") => Clientes(request.RequestUri),
                ("POST", "/api/captura-solicitudes") => Alta(cuerpo),
                ("GET", var r) when r.StartsWith("/api/solicitudes/", StringComparison.Ordinal) => Solicitud(r["/api/solicitudes/".Length..]),
                ("POST", "/api/cotizaciones") => CrearORecuperar(cuerpo),
                ("POST", var r) when r.StartsWith("/api/cotizaciones/", StringComparison.Ordinal) && r.EndsWith("/resultado", StringComparison.Ordinal) =>
                    MarcarResultado(r["/api/cotizaciones/".Length..^"/resultado".Length], cuerpo),
                _ => Error(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", $"{request.Method} {ruta}"),
            };
        }
    }

    private HttpResponseMessage Clientes(Uri url)
    {
        var q = Uri.UnescapeDataString(url.Query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("q=", StringComparison.Ordinal))?[2..] ?? string.Empty);
        var digitos = new string(q.Where(char.IsAsciiDigit).ToArray());
        var filas = _clientes
            .Where(c => (digitos.Length > 0 && new string(c.Nit.Where(char.IsAsciiDigit).ToArray()).Contains(digitos, StringComparison.Ordinal))
                        || c.Nombre.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new { clienteId = (string?)null, bp = c.Bp, nit = c.Nit, nombre = c.Nombre, origen = c.Origen })
            .ToList();
        return Json(HttpStatusCode.OK, filas);
    }

    private HttpResponseMessage Alta(string? cuerpo)
    {
        if (AltaFalla is { } falla)
        {
            return Error(falla, "VALIDATION_ERROR", "Rechazada por el Nexo falso.");
        }

        UltimaAlta = JsonNode.Parse(cuerpo ?? "{}");
        var solicitud = new clsSolicitudFalsa(Guid.NewGuid().ToString(), _solicitudes.Count, Guid.NewGuid().ToString());
        if (AutoCostear)
        {
            solicitud.Cotizacion = new clsCotizacionFalsa(Guid.NewGuid().ToString()) { Flete = FleteAutocosteo };
        }

        _solicitudes[solicitud.Id] = solicitud;
        return Json(HttpStatusCode.Created, new
        {
            id = solicitud.Id,
            lineas = 1,
            rutasCreadas = 0,
            vehiculosCreados = 0,
            scorePrioridad = 0.5m,
            fletesCalculados = AutoCostear ? 1 : 0,
            lineasPorCostear = AutoCostear ? 0 : 1,
            costeoEnProceso = false,
        });
    }

    private HttpResponseMessage Solicitud(string id)
    {
        if (!_solicitudes.TryGetValue(id, out var s))
        {
            return Error(HttpStatusCode.NotFound, "SOLICITUD_NO_ENCONTRADA", "Solicitud no encontrada.");
        }

        return Json(HttpStatusCode.OK, new
        {
            solicitud = new
            {
                id = s.Id,
                clienteId = Guid.Empty,
                tipo = "Spot",
                estado = s.Estado,
                scorePrioridad = 0.5m,
                createdAt = DateTimeOffset.UtcNow,
                lineas = new[] { new { id = s.LineaId, estadoLinea = s.Cotizacion?.Flete is null ? "PorCostear" : "AutoResuelta" } },
            },
            tiempos = new { horasCargue = 4m, horasDescargue = 4m },
            lineas = Array.Empty<object>(),
        });
    }

    private HttpResponseMessage CrearORecuperar(string? cuerpo)
    {
        var id = JsonNode.Parse(cuerpo ?? "{}")?["solicitudId"]?.GetValue<string>();
        if (id is null || !_solicitudes.TryGetValue(id, out var s))
        {
            return Error(HttpStatusCode.BadRequest, "SOLICITUD_INVALIDA", "La solicitud indicada no existe.");
        }

        s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
        return Json(HttpStatusCode.Created, Cotizacion(s));
    }

    private HttpResponseMessage MarcarResultado(string cotizacionId, string? cuerpo)
    {
        var s = _solicitudes.Values.FirstOrDefault(x => x.Cotizacion?.Id == cotizacionId);
        if (s?.Cotizacion is not { } c)
        {
            return Error(HttpStatusCode.NotFound, "COTIZACION_NO_ENCONTRADA", "Cotización no encontrada.");
        }

        var nodo = JsonNode.Parse(cuerpo ?? "{}");
        var resultado = nodo?["resultado"]?.GetValue<string>() switch
        {
            "ganada" => "Ganada",
            "perdida" => "Perdida",
            _ => null,
        };
        if (resultado is null)
        {
            return Error(HttpStatusCode.BadRequest, "VALIDATION_ERROR", "Resultado debe ser 'ganada' o 'perdida'.");
        }

        if (c.Resultado == resultado)
        {
            return Json(HttpStatusCode.OK, Cotizacion(s));
        }

        if (c.Resultado != "SinDefinir")
        {
            return Error(HttpStatusCode.Conflict, "RESULTADO_YA_DEFINIDO", $"La cotización ya está marcada como {c.Resultado} y no puede cambiarse a {resultado}.");
        }

        if (c.Estado != "Entregada")
        {
            return Error(HttpStatusCode.Conflict, "COTIZACION_NO_ENTREGADA", "Solo se puede marcar el resultado de una cotización entregada.");
        }

        c.Resultado = resultado;
        c.Motivo = nodo?["motivo"]?.GetValue<string>();
        c.Comentario = nodo?["comentario"]?.GetValue<string>();
        return Json(HttpStatusCode.OK, Cotizacion(s));
    }

    private static object Cotizacion(clsSolicitudFalsa s)
    {
        var c = s.Cotizacion!;
        var lineas = c.Flete is { } flete
            ? new[]
            {
                new
                {
                    id = c.LineaId, lineaSolicitudId = (string?)s.LineaId, valorFlete = flete, fuente = "Sicetac",
                    valorAprovisionamiento = c.Aprovisionamiento, valorRetornoContenedor = c.Retorno,
                },
            }
            : [];
        var conexos = c.Conexos.Select(x => new { id = x.Id, servicioConexoId = (string?)null, nombre = x.Nombre, categoria = x.Categoria, cantidad = x.Cantidad, valorAplicado = x.Valor }).ToList();
        return new
        {
            id = c.Id,
            solicitudId = s.Id,
            estado = c.Estado,
            resultado = c.Resultado,
            total = lineas.Sum(l => l.valorFlete + (l.valorAprovisionamiento ?? 0m) + (l.valorRetornoContenedor ?? 0m)) + conexos.Sum(x => x.valorAplicado),
            entregadaAt = c.Estado == "Entregada" ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
            lineas,
            conexos,
        };
    }

    private void Con(string solicitudId, Action<clsSolicitudFalsa> cambio)
    {
        lock (_candado)
        {
            cambio(_solicitudes.TryGetValue(solicitudId, out var s) ? s : throw new InvalidOperationException($"No existe la solicitud {solicitudId} en el Nexo falso."));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, object cuerpo) =>
        new(estado) { Content = new StringContent(JsonSerializer.Serialize(cuerpo, _json), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode estado, string codigo, string mensaje) =>
        Json(estado, new { code = codigo, message = mensaje, timestamp = DateTimeOffset.UtcNow });

    private sealed class clsSolicitudFalsa(string id, int orden, string lineaId)
    {
        public string Id { get; } = id;
        public int Orden { get; } = orden;
        public string LineaId { get; } = lineaId;
        public string Estado { get; set; } = "Pendiente";
        public clsCotizacionFalsa? Cotizacion { get; set; }
    }

    private sealed class clsCotizacionFalsa(string id)
    {
        public string Id { get; } = id;
        public string LineaId { get; } = Guid.NewGuid().ToString();
        public string Estado { get; set; } = "Borrador";
        public string Resultado { get; set; } = "SinDefinir";
        public string? Motivo { get; set; }
        public string? Comentario { get; set; }
        public decimal? Flete { get; set; }
        public decimal? Aprovisionamiento { get; set; }
        public decimal? Retorno { get; set; }
        public List<(string Id, string Nombre, string Categoria, decimal Cantidad, decimal Valor)> Conexos { get; } = [];
    }
}
