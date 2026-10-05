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
    private readonly List<(string Nit, string Nombre, string Origen, string? Bp, string? ClienteId)> _clientes = [];

    /// <summary>Si tiene valor, TODAS las peticiones responden este estado (Nexo caído o rechazando).</summary>
    public HttpStatusCode? Caido { get; set; }

    /// <summary>Si tiene valor, solo el alta responde este estado (con un cuerpo de error de Nexo).</summary>
    public HttpStatusCode? AltaFalla { get; set; }

    /// <summary>Si es verdadero, el alta auto-costea la línea con <see cref="FleteAutocosteo"/> (como la Biblioteca de Nexo).</summary>
    public bool AutoCostear { get; set; }

    public decimal FleteAutocosteo { get; set; } = 2_550_000m;

    public IReadOnlyList<clsPeticionNexo> Peticiones => [.. _peticiones];

    /// <summary>Peticiones que no son de los catálogos maestros (<c>/api/catalogos/*</c>): las del costeo y la búsqueda.</summary>
    public IReadOnlyList<clsPeticionNexo> PeticionesSinCatalogos =>
        [.. _peticiones.Where(p => !p.Url.AbsolutePath.StartsWith("/api/catalogos/", StringComparison.Ordinal))];

    /// <summary>Catálogo de <c>GET /api/catalogos/tipos-vehiculo</c> (HU-013.10): <c>codigo</c>, <c>descripcion</c>, <c>esCatalogoOficial</c>.</summary>
    public List<(string? Codigo, string? Descripcion, bool Oficial)> TiposVehiculo { get; } =
    [
        ("2", "Camión 2 ejes", true), ("2S3", "Tractocamión 2 ejes, semi 3", true), ("3S3", "Tractocamión 3 ejes, semi 3", true),
    ];

    /// <summary>Descripciones de <c>GET /api/catalogos/clases-viaje</c>.</summary>
    public List<string> ClasesViaje { get; } = ["Contenedor", "Cupo", "Carga Suelta", "Extradimensionado"];

    /// <summary>Descripciones de <c>GET /api/catalogos/clases-contenedor</c>.</summary>
    public List<string> ClasesContenedor { get; } = ["CNT20.IT", "CNT20.RE", "CNT20.ST", "CNT40.HC", "CNT40.RE"];

    /// <summary>Descripciones de <c>GET /api/catalogos/lineas-negocio</c>.</summary>
    public List<string> LineasNegocio { get; } = ["PuertoAInterior", "InteriorAPuerto", "Nacional", "DevolucionContenedor", "Andino", "Licitaciones"];

    /// <summary>Si es mayor que cero, las siguientes N peticiones a <c>/api/catalogos/*</c> responden 503 (fallo transitorio).</summary>
    public int FallasCatalogos { get; set; }

    /// <summary>Si tiene valor, solo <c>/api/catalogos/*</c> responde este estado.</summary>
    public HttpStatusCode? CatalogosCaidos { get; set; }

    /// <summary>Código de Nexo con el que responde el alta cuando <see cref="AltaFalla"/> tiene valor (por defecto <c>VALIDATION_ERROR</c>).</summary>
    public string CodigoAltaFalla { get; set; } = "VALIDATION_ERROR";

    /// <summary>Agrega un cliente (o prospecto sin BP) al catálogo de <c>GET /api/captura-solicitudes/clientes</c>.</summary>
    public clsNexoFalso ConCliente(string nit, string nombre, string origen = "cliente", string? bp = "15927", string? clienteId = null)
    {
        lock (_candado)
        {
            _clientes.Add((nit, nombre, origen, bp, clienteId));
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

    /// <summary>La Mesa de Nexo agrega el flete (y opcionalmente aprovisionamiento/retorno) a TODAS las líneas de la solicitud.</summary>
    public void Costear(string solicitudId, decimal flete, decimal? aprovisionamiento = null, decimal? retorno = null) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            for (var i = 0; i < s.LineaIds.Count; i++)
            {
                s.Cotizacion.Fletes[i] = (flete, aprovisionamiento, retorno);
            }

            s.Estado = "EnCosteo";
        });

    /// <summary>La Mesa de Nexo costea solo la línea <paramref name="indice"/> (base 0) de una solicitud de varias líneas (HU-011).</summary>
    public void CostearLinea(string solicitudId, int indice, decimal flete) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Fletes[indice] = (flete, null, null);
            s.Estado = "EnCosteo";
        });

    /// <summary>Ids de las líneas de la solicitud en Nexo, en el orden de captura.</summary>
    public IReadOnlyList<string> LineasDe(string solicitudId)
    {
        lock (_candado)
        {
            return _solicitudes.TryGetValue(solicitudId, out var s) ? [.. s.LineaIds] : [];
        }
    }

    /// <summary>Si tiene valor, <c>analizar-excel</c> responde este estado con <see cref="CodigoAnalisisFalla"/>.</summary>
    public HttpStatusCode? AnalisisFalla { get; set; }

    public string CodigoAnalisisFalla { get; set; } = "EXCEL_INVALIDO";

    /// <summary>Respuesta de <c>analizar-excel</c> (un <c>AnalisisExcelDto</c>); por defecto una hoja "Pliego" con 3 filas.</summary>
    public object Analisis { get; set; } = new
    {
        archivo = "pliego.xlsx",
        hojas = new[]
        {
            new
            {
                nombre = "Pliego",
                filaEncabezado = 1,
                encabezados = new[] { "ITEM", "ORIGEN", "DESTINO", "TON" },
                filas = new[] { new[] { "1", "Buenaventura", "Bogotá", "32" }, new[] { "2", "Cali", "Cartagena", "28" }, new[] { "3", "Cúcuta", "Bogotá", "8" } },
                totalFilas = 3,
            },
        },
    };

    /// <summary>Tamaño en bytes del último archivo recibido por <c>analizar-excel</c> (multipart <c>archivo</c>), o <c>null</c>.</summary>
    public long? UltimoArchivoAnalizado { get; private set; }

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
        var esMultipart = request.Content?.Headers.ContentType?.MediaType == "multipart/form-data";
        var cuerpo = request.Content is null || esMultipart ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (esMultipart)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            UltimoArchivoAnalizado = bytes.LongLength;
        }
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
                ("GET", var r) when r.StartsWith("/api/catalogos/", StringComparison.Ordinal) => Catalogo(r["/api/catalogos/".Length..]),
                ("GET", "/api/captura-solicitudes/clientes") => Clientes(request.RequestUri),
                ("POST", "/api/captura-solicitudes/analizar-excel") => AnalisisFalla is { } fallaAnalisis
                    ? Error(fallaAnalisis, CodigoAnalisisFalla, "No se pudo leer el archivo Excel (prueba).")
                    : Json(HttpStatusCode.OK, Analisis),
                ("POST", "/api/captura-solicitudes") => Alta(cuerpo),
                ("GET", var r) when r.StartsWith("/api/solicitudes/", StringComparison.Ordinal) => Solicitud(r["/api/solicitudes/".Length..]),
                ("POST", "/api/cotizaciones") => CrearORecuperar(cuerpo),
                ("POST", var r) when r.StartsWith("/api/cotizaciones/", StringComparison.Ordinal) && r.EndsWith("/resultado", StringComparison.Ordinal) =>
                    MarcarResultado(r["/api/cotizaciones/".Length..^"/resultado".Length], cuerpo),
                _ => Error(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", $"{request.Method} {ruta}"),
            };
        }
    }

    private HttpResponseMessage Catalogo(string nombre)
    {
        if (CatalogosCaidos is { } caidos)
        {
            return Error(caidos, "NEXO_CAIDO", "Catálogos no disponibles (prueba).");
        }

        if (FallasCatalogos > 0)
        {
            FallasCatalogos--;
            return Error(HttpStatusCode.ServiceUnavailable, "NEXO_CAIDO", "Fallo transitorio (prueba).");
        }

        return nombre switch
        {
            "tipos-vehiculo" => Json(HttpStatusCode.OK, TiposVehiculo.Select(t => new { codigo = t.Codigo, descripcion = t.Descripcion, esCatalogoOficial = t.Oficial }).ToList()),
            "clases-viaje" => Json(HttpStatusCode.OK, ClasesViaje.Select((d, i) => new { id = i + 1, descripcion = d }).ToList()),
            "clases-contenedor" => Json(HttpStatusCode.OK, ClasesContenedor.Select((d, i) => new { id = i + 1, descripcion = d, tamanoContenedor = (int?)null }).ToList()),
            "lineas-negocio" => Json(HttpStatusCode.OK, LineasNegocio.Select((d, i) => new { id = i + 1, descripcion = d }).ToList()),
            _ => Error(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", $"GET /api/catalogos/{nombre}"),
        };
    }

    private HttpResponseMessage Clientes(Uri url)
    {
        var q = Uri.UnescapeDataString(url.Query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("q=", StringComparison.Ordinal))?[2..] ?? string.Empty);
        var digitos = new string(q.Where(char.IsAsciiDigit).ToArray());
        var filas = _clientes
            .Where(c => (digitos.Length > 0 && new string(c.Nit.Where(char.IsAsciiDigit).ToArray()).Contains(digitos, StringComparison.Ordinal))
                        || c.Nombre.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new { clienteId = c.ClienteId, bp = c.Bp, nit = c.Nit, nombre = c.Nombre, origen = c.Origen })
            .ToList();
        return Json(HttpStatusCode.OK, filas);
    }

    private HttpResponseMessage Alta(string? cuerpo)
    {
        if (AltaFalla is { } falla)
        {
            return Error(falla, CodigoAltaFalla, "Rechazada por el Nexo falso.");
        }

        UltimaAlta = JsonNode.Parse(cuerpo ?? "{}");
        var numero = Math.Max(1, UltimaAlta?["lineas"] is JsonArray arreglo ? arreglo.Count : 1);
        var solicitud = new clsSolicitudFalsa(Guid.NewGuid().ToString(), _solicitudes.Count, [.. Enumerable.Range(0, numero).Select(_ => Guid.NewGuid().ToString())]);
        if (UltimaAlta?["lineas"] is JsonArray recibidas)
        {
            solicitud.Lineas.AddRange(recibidas.Select(l => new clsLineaFalsa(
                l?["origen"]?.GetValue<string>() ?? string.Empty,
                l?["destino"]?.GetValue<string>() ?? string.Empty,
                l?["tipologia"]?.GetValue<string>(),
                l?["peso"]?.GetValue<decimal>(),
                l?["tipoVehiculo"]?.GetValue<string>(),
                l?["carroceria"]?.GetValue<string>())));
        }

        while (solicitud.Lineas.Count < numero)
        {
            solicitud.Lineas.Add(new clsLineaFalsa(string.Empty, string.Empty, null, null, null, null));
        }
        if (AutoCostear)
        {
            solicitud.Cotizacion = new clsCotizacionFalsa(Guid.NewGuid().ToString());
            for (var i = 0; i < numero; i++)
            {
                solicitud.Cotizacion.Fletes[i] = (FleteAutocosteo, null, null);
            }
        }

        // Como Nexo: con más de 10 líneas el auto-costeo va en segundo plano (costeoEnProceso).
        var enProceso = numero > 10;
        _solicitudes[solicitud.Id] = solicitud;
        return Json(HttpStatusCode.Created, new
        {
            id = solicitud.Id,
            lineas = numero,
            rutasCreadas = 0,
            vehiculosCreados = 0,
            scorePrioridad = 0.5m,
            fletesCalculados = AutoCostear && !enProceso ? numero : 0,
            lineasPorCostear = AutoCostear && !enProceso ? 0 : numero,
            costeoEnProceso = enProceso,
        });
    }

    private HttpResponseMessage Solicitud(string id)
    {
        if (!_solicitudes.TryGetValue(id, out var s))
        {
            return Error(HttpStatusCode.NotFound, "SOLICITUD_NO_ENCONTRADA", "Solicitud no encontrada.");
        }

        // Como ObtenerCosteoQuery de Nexo: una descartada responde 409, y abrirla pasa Pendiente -> EnCosteo.
        if (s.Estado == "Descartada")
        {
            return Error(HttpStatusCode.Conflict, "SOLICITUD_DESCARTADA", "La solicitud está descartada y no puede costearse.");
        }

        if (s.Estado == "Pendiente")
        {
            s.Estado = "EnCosteo";
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
                // Como Nexo (Include(Lineas) sin OrderBy, ids GUID): el orden NO es el de captura. El falso las devuelve al revés.
                lineas = Indices(s).Select(i => new { id = s.LineaIds[i], estadoLinea = s.Cotizacion?.Fletes.ContainsKey(i) == true ? "AutoResuelta" : "PorCostear" }).ToArray(),
            },
            tiempos = new { horasCargue = 4m, horasDescargue = 4m },
            // LineaCosteoDto: el contenido de cada línea, con la ruta "Origen → Destino" (ObtenerCosteoQuery de Nexo).
            lineas = Indices(s).Select(i => new
            {
                lineaId = s.LineaIds[i],
                ruta = $"{s.Lineas[i].Origen} → {s.Lineas[i].Destino}",
                tipologia = s.Lineas[i].Tipologia,
                peso = s.Lineas[i].Peso,
                tipoVehiculo = s.Lineas[i].TipoVehiculo,
                carroceria = s.Lineas[i].Carroceria,
            }).ToArray(),
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
        var lineas = c.Fletes.OrderByDescending(f => f.Key).Where(f => f.Key < s.LineaIds.Count).Select(f => new
        {
            id = Guid.NewGuid().ToString(), lineaSolicitudId = (string?)s.LineaIds[f.Key], valorFlete = f.Value.Flete, fuente = "Sicetac",
            valorAprovisionamiento = f.Value.Aprovisionamiento, valorRetornoContenedor = f.Value.Retorno,
        }).ToArray();
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

    /// <summary>Índices de las líneas en el orden en que las devuelve el falso: al revés del de captura.</summary>
    private static IEnumerable<int> Indices(clsSolicitudFalsa s) => Enumerable.Range(0, s.LineaIds.Count).Reverse();

    private sealed record clsLineaFalsa(string Origen, string Destino, string? Tipologia, decimal? Peso, string? TipoVehiculo, string? Carroceria);

    private sealed class clsSolicitudFalsa(string id, int orden, IReadOnlyList<string> lineaIds)
    {
        public string Id { get; } = id;
        public int Orden { get; } = orden;
        public IReadOnlyList<string> LineaIds { get; } = lineaIds;
        public List<clsLineaFalsa> Lineas { get; } = [];
        public string Estado { get; set; } = "Pendiente";
        public clsCotizacionFalsa? Cotizacion { get; set; }
    }

    private sealed class clsCotizacionFalsa(string id)
    {
        public string Id { get; } = id;
        public string Estado { get; set; } = "Borrador";
        public string Resultado { get; set; } = "SinDefinir";
        public string? Motivo { get; set; }
        public string? Comentario { get; set; }
        /// <summary>Fletes por índice de línea de la solicitud (base 0).</summary>
        public Dictionary<int, (decimal Flete, decimal? Aprovisionamiento, decimal? Retorno)> Fletes { get; } = [];
        public List<(string Id, string Nombre, string Categoria, decimal Cantidad, decimal Valor)> Conexos { get; } = [];
    }
}
