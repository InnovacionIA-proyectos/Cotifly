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
    private readonly List<clsServicioCatalogoFalso> _catalogo = [];

    /// <summary>Si tiene valor, TODAS las peticiones responden este estado (Nexo caído o rechazando).</summary>
    public HttpStatusCode? Caido { get; set; }

    /// <summary>Si tiene valor, solo el alta responde este estado (con un cuerpo de error de Nexo).</summary>
    public HttpStatusCode? AltaFalla { get; set; }

    /// <summary>
    /// HU-030.1 (#184): altas rechazadas con <c>400 PESO_INCONSISTENTE</c>. Como Nexo (<c>CapturarSolicitudValidador</c>, confirmado al
    /// cerrar NEXO#308): una línea con posiciones con peso cuyo <c>peso</c> difiere más del 1 % de <c>Σ cantidad × pesoKg</c> se rechaza.
    /// </summary>
    public int AltasPesoInconsistente => Volatile.Read(ref _altasPesoInconsistente);

    private int _altasPesoInconsistente;

    /// <summary>
    /// HU-023.3 (#158): si tiene valor, el alta (<c>POST /api/captura-solicitudes</c>) lo espera antes de responder: sirve para hacer
    /// otra petición a la API mientras el envío a Nexo está en curso (carreras).
    /// </summary>
    public Func<Task>? AntesDelAlta { get; set; }

    /// <summary>
    /// Si es verdadero, <c>GET /api/solicitudes/{id}</c> responde 503 para las solicitudes cuya cotización ya tiene resultado: la
    /// relectura del costeo justo después de marcar el resultado falla (HU-013.14).
    /// </summary>
    public bool FallarConsultaTrasResultado { get; set; }

    /// <summary>
    /// E2E 2026-10-16: si es mayor que cero, los siguientes N <c>GET /api/solicitudes/{id}</c> responden <c>409 CONFLICTO_CONCURRENCIA</c>
    /// (dos lecturas a la vez: "abrir" la solicitud cambia su estado en Nexo).
    /// </summary>
    public int ConflictosPasajerosSolicitud { get; set; }

    /// <summary>Si es verdadero, <c>GET /api/cotizaciones/por-solicitud/{id}</c> no existe (un Nexo anterior a S5-HU-20.1, #286): 404.</summary>
    public bool SinCotizacionPorSolicitud { get; set; }

    /// <summary>Cuántas peticiones con <paramref name="metodo"/> recibió <paramref name="ruta"/> (ruta exacta, sin la consulta).</summary>
    public int Llamadas(HttpMethod metodo, string ruta) =>
        Peticiones.Count(p => p.Metodo == metodo && p.Url.AbsolutePath.TrimEnd('/') == ruta);

    /// <summary>Escrituras recibidas (POST, PUT, PATCH, DELETE) en <c>/api/cotizaciones</c> y sus subrutas.</summary>
    public int EscriturasCotizaciones =>
        Peticiones.Count(p => p.Metodo != HttpMethod.Get && p.Url.AbsolutePath.StartsWith("/api/cotizaciones", StringComparison.Ordinal));

    /// <summary>Si es mayor que cero, los siguientes N GET responden <c>429 DEMASIADAS_SOLICITUDES</c> con <c>Retry-After</c> (<see cref="SegundosRetryAfter"/>).</summary>
    public int LecturasLimitadas { get; set; }

    /// <summary>Segundos del <c>Retry-After</c> de esos 429 (30 por defecto, el que manda Nexo hoy).</summary>
    public int SegundosRetryAfter { get; set; } = 30;

    /// <summary>Si es verdadero, el alta auto-costea la línea con <see cref="FleteAutocosteo"/> (como la Biblioteca de Nexo).</summary>
    public bool AutoCostear { get; set; }

    public decimal FleteAutocosteo { get; set; } = 2_550_000m;

    /// <summary>
    /// HU-014 (#188): si es verdadero, el alta auto-costea como Nexo según su contrato (<c>COTIFLY-CONTRATO.md</c> §3.4 y §4): una línea
    /// con un <c>tipoVehiculo</c> que no está entre los oficiales de <see cref="TiposVehiculo"/> (comparado como Nexo: sin tildes ni mayúsculas, con <c>_</c>
    /// y <c>-</c> como espacio) nace en cuarentena (<see cref="Cuarentenas"/>) y no se costea; una línea de Cupo (o sin clase de viaje)
    /// sin vehículo queda <c>PorCostear</c>; las demás se costean con <see cref="FleteAutocosteo"/>.
    /// </summary>
    public bool AutoCostearSegunContrato { get; set; }

    /// <summary>Líneas que nacieron en cuarentena (código de vehículo que Nexo no conoce) con <see cref="AutoCostearSegunContrato"/>.</summary>
    public int Cuarentenas { get; private set; }

    /// <summary>HU-018.1 (#155): <c>scorePrioridad</c> que responde el alta (<c>CapturaResultDto</c>); <c>null</c> lo manda en null.</summary>
    public decimal? ScorePrioridadAlta { get; set; } = 0.5m;

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

    /// <summary>
    /// Catálogo de <c>GET /api/captura-solicitudes/municipios</c> (HU-013.11): resuelve las ciudades de <see cref="clsPliegoMuestra"/> y
    /// trae "La Unión" en tres departamentos (el caso ambiguo).
    /// </summary>
    public List<(int Id, string? CodigoDane, string Municipio, string Departamento)> Municipios { get; } =
    [
        (76109, "76109", "Buenaventura", "Valle del Cauca"), (11001, "11001", "Bogotá, D.C.", "Bogotá, D.C."), (76001, "76001", "Cali", "Valle del Cauca"),
        (13001, "13001", "Cartagena", "Bolívar"), (54001, "54001", "Cúcuta", "Norte de Santander"), (5001, "05001", "Medellín", "Antioquia"),
        (50001, "50001", "Villavicencio", "Meta"), (66001, "66001", "Pereira", "Risaralda"), (17001, "17001", "Manizales", "Caldas"),
        (47001, "47001", "Santa Marta", "Magdalena"), (73001, "73001", "Ibagué", "Tolima"), (41001, "41001", "Neiva", "Huila"),
        (8001, "08001", "Barranquilla", "Atlántico"), (68001, "68001", "Bucaramanga", "Santander"), (76892, "76892", "Yumbo", "Valle del Cauca"),
        (5400, "05400", "La Unión", "Antioquia"), (52399, "52399", "La Unión", "Nariño"), (76400, "76400", "La Unión", "Valle del Cauca"),
    ];

    /// <summary>Si tiene valor, <c>GET /api/captura-solicitudes/municipios</c> responde este estado.</summary>
    public HttpStatusCode? MunicipiosFalla { get; set; }

    /// <summary>
    /// Si tiene valor, <c>GET /api/captura-solicitudes/municipios</c> y <c>/ciudades</c> lanzan esta excepción en vez de responder (por
    /// ejemplo un <see cref="TaskCanceledException"/> como el del tiempo agotado de <see cref="HttpClient"/>, o un
    /// <see cref="HttpRequestException"/> de red).
    /// </summary>
    public Exception? MunicipiosExcepcion { get; set; }

    /// <summary>Si es mayor que cero, las siguientes N peticiones a <c>/api/catalogos/*</c> responden 503 (fallo transitorio).</summary>
    public int FallasCatalogos { get; set; }

    /// <summary>Catálogos (<c>tipos-vehiculo</c>, <c>clases-contenedor</c>...) que responden 500 aunque los demás respondan.</summary>
    public HashSet<string> CatalogosFallidos { get; } = new(StringComparer.Ordinal);

    /// <summary>Si tiene valor, el catálogo <c>tipos-vehiculo</c> responde este cuerpo crudo (pruebas de límites).</summary>
    public string? CuerpoTiposVehiculo { get; set; }

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
    public void CostearLinea(string solicitudId, int indice, decimal flete, decimal? retorno = null) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Fletes[indice] = (flete, null, retorno);
            s.Estado = "EnCosteo";
        });

    /// <summary>
    /// Si tiene valor, <c>GET /api/solicitudes/{id}</c> devuelve las líneas barajadas con esta semilla (como Nexo, sin <c>OrderBy</c>);
    /// sin valor, al revés del orden de captura (HU-021.7, #134).
    /// </summary>
    public int? SemillaOrdenLineas { get; set; }

    /// <summary>
    /// La Mesa de Costeo le pone vehículo y carrocería a la línea <paramref name="indice"/> (base 0) que llegó vacía (petición N3 a Nexo):
    /// <c>GET /api/solicitudes/{id}</c> los devuelve en esa línea.
    /// </summary>
    public void AsignarVehiculo(string solicitudId, int indice, string tipoVehiculo, string? carroceria) =>
        Con(solicitudId, s => s.Lineas[indice] = s.Lineas[indice] with { TipoVehiculo = tipoVehiculo, Carroceria = carroceria });

    /// <summary>Estado de cada línea de la solicitud en Nexo (<c>AutoResuelta</c> o <c>PorCostear</c>), en el orden de captura.</summary>
    public IReadOnlyList<string> EstadosLinea(string solicitudId)
    {
        lock (_candado)
        {
            var s = _solicitudes[solicitudId];
            return [.. Enumerable.Range(0, s.LineaIds.Count).Select(i => s.Cotizacion?.Fletes.ContainsKey(i) == true ? "AutoResuelta" : "PorCostear")];
        }
    }

    /// <summary>Como Nexo compara los nombres (§3.4): sin tildes, en mayúsculas y con <c>_</c> y <c>-</c> como espacio.</summary>
    private static string NormalizarComoNexo(string texto)
    {
        var sinTildes = new string([.. texto.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)]);
        return string.Join(' ', sinTildes.Replace('_', ' ').Replace('-', ' ').ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

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

    /// <summary>Si tiene valor, <c>GET /api/servicios-conexos</c> responde este estado (ej. 403: sin rol en Nexo).</summary>
    public HttpStatusCode? CatalogoFalla { get; set; }

    /// <summary>Si tiene valor, <c>POST /api/cotizaciones/{id}/conexos</c> responde este estado (ej. 403: sin rol en Nexo).</summary>
    public HttpStatusCode? ConexosFalla { get; set; }

    /// <summary>
    /// Si es verdadero, después de agregar conexos <c>GET /api/cotizaciones/{id}</c> responde 503 (Nexo agregó el conexo pero la
    /// relectura falla); <c>GET /api/cotizaciones/por-solicitud/{id}</c> y "crear o recuperar" (el polling) siguen respondiendo.
    /// </summary>
    public bool FallarRelecturaTrasConexos { get; set; }

    /// <summary>Si es verdadero, <c>POST .../conexos</c> aplica el conexo y luego corta la conexión (efecto incierto para Cotifly).</summary>
    public bool CortarRespuestaConexos { get; set; }

    /// <summary>Servicios que <c>POST .../conexos</c> rechaza con el estado indicado (ej. 400 o 404), además de las reglas del catálogo.</summary>
    public ConcurrentDictionary<string, HttpStatusCode> RechazosConexo { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Agrega un servicio al catálogo de <c>GET /api/servicios-conexos</c> (<c>clsServicioConexoDto</c>).</summary>
    public clsNexoFalso ConServicioConexo(string id, string codigo, string nombre, string categoria, string unidad, decimal costoUnitario, bool activo = true)
    {
        lock (_candado)
        {
            _catalogo.Add(new clsServicioCatalogoFalso(id, codigo, nombre, categoria, unidad, costoUnitario, activo));
        }

        return this;
    }

    /// <summary>Cuerpos de los <c>POST /api/cotizaciones/{cotizacionId}/conexos</c> recibidos, en orden.</summary>
    public IReadOnlyList<JsonNode> PeticionesConexos(string cotizacionId) =>
        [.. Peticiones
            .Where(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath.TrimEnd('/') == $"/api/cotizaciones/{cotizacionId}/conexos")
            .Select(p => JsonNode.Parse(p.Cuerpo ?? "{}")!)];

    /// <summary>Id de la línea de la COTIZACIÓN de Nexo que corresponde a la línea <paramref name="indice"/> (base 0) de la solicitud.</summary>
    public string? LineaCotizacionDe(string solicitudId, int indice)
    {
        lock (_candado)
        {
            return _solicitudes.TryGetValue(solicitudId, out var s) && s.Cotizacion is { } c && c.Fletes.ContainsKey(indice)
                ? c.LineaId(indice)
                : null;
        }
    }

    /// <summary><c>POST /api/cotizaciones/{id}/entregar</c> de la Mesa: cotización y solicitud en <c>Entregada</c>.</summary>
    public void Entregar(string solicitudId) =>
        Con(solicitudId, s =>
        {
            s.Cotizacion ??= new clsCotizacionFalsa(Guid.NewGuid().ToString());
            s.Cotizacion.Estado = "Entregada";
            s.Estado = "Entregada";
        });

    public void Descartar(string solicitudId) => Con(solicitudId, s => s.Estado = "Descartada");

    /// <summary>HU-028 (#186): si tiene valor, <c>POST /api/solicitudes/{id}/rechazar</c> responde este estado (con <see cref="CodigoRechazoFalla"/>).</summary>
    public HttpStatusCode? RechazoFalla { get; set; }

    /// <summary>Código de Nexo de la respuesta de <see cref="RechazoFalla"/>.</summary>
    public string CodigoRechazoFalla { get; set; } = "NEXO_CAIDO";

    private readonly List<(string SolicitudId, string Motivo, string? Comentario)> _rechazos = [];

    /// <summary>HU-028: los rechazos que Nexo aplicó (204), en orden, con su motivo y su comentario.</summary>
    public IReadOnlyList<(string SolicitudId, string Motivo, string? Comentario)> Rechazos
    {
        get
        {
            lock (_candado)
            {
                return [.. _rechazos];
            }
        }
    }

    /// <summary>HU-028: cuántas veces se llamó a <c>POST /api/solicitudes/{id}/rechazar</c> (con éxito o sin él).</summary>
    public int IntentosRechazo =>
        Peticiones.Count(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath.EndsWith("/rechazar", StringComparison.Ordinal));

    /// <summary>Estado de la solicitud en el Nexo falso (<c>Pendiente</c>, <c>EnCosteo</c>, <c>Entregada</c>, <c>Descartada</c>) o <c>null</c>.</summary>
    public string? EstadoDe(string solicitudId)
    {
        lock (_candado)
        {
            return _solicitudes.TryGetValue(solicitudId, out var s) ? s.Estado : null;
        }
    }

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
        var identidad = request.Headers.TryGetValues(clsPeticionNexo.EncabezadoIdentidad, out var valores) ? valores.FirstOrDefault() : null;
        _peticiones.Enqueue(new clsPeticionNexo(
            request.Method,
            request.RequestUri!,
            identidad,
            request.Headers.Accept.ToString(),
            cuerpo));

        // Como clsIdentidadPortalAuthenticationHandler de Nexo: sin X-Portal-clsUsuario-Id numérico, 401 (HU-023.4, #159).
        if (!long.TryParse(identidad, out _))
        {
            return Error(HttpStatusCode.Unauthorized, "SESION_INVALIDA", "La sesión no es válida. Ingrese nuevamente desde el portal.");
        }

        if (Caido is { } caido)
        {
            return Error(caido, "NEXO_CAIDO", "Nexo no disponible (prueba).");
        }

        // Como el rate limiter de Nexo (por rol): 429 DEMASIADAS_SOLICITUDES con Retry-After: 30 en los GET.
        if (request.Method == HttpMethod.Get && LecturasLimitadas > 0)
        {
            LecturasLimitadas--;
            var limitada = Error(HttpStatusCode.TooManyRequests, "DEMASIADAS_SOLICITUDES",
                "Ha alcanzado el límite de solicitudes para este recurso. Intente de nuevo en unos segundos.");
            limitada.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(SegundosRetryAfter));
            return limitada;
        }

        var ruta = request.RequestUri!.AbsolutePath.TrimEnd('/');
        if (MunicipiosExcepcion is { } excepcionMunicipios && request.Method == HttpMethod.Get
            && ruta is "/api/captura-solicitudes/municipios" or "/api/captura-solicitudes/ciudades")
        {
            throw excepcionMunicipios;
        }

        if (AntesDelAlta is { } gancho && request.Method == HttpMethod.Post && ruta == "/api/captura-solicitudes")
        {
            await gancho();
        }

        lock (_candado)
        {
            return (request.Method.Method, ruta) switch
            {
                ("GET", var r) when r.StartsWith("/api/catalogos/", StringComparison.Ordinal) => Catalogo(r["/api/catalogos/".Length..]),
                ("GET", "/api/captura-solicitudes/clientes") => Clientes(request.RequestUri),
                ("GET", "/api/captura-solicitudes/ciudades") => MunicipiosFalla is { } fallaCiudades
                    ? Error(fallaCiudades, "NEXO_CAIDO", "Ciudades no disponibles (prueba).")
                    : Ciudades(request.RequestUri!),
                ("GET", "/api/captura-solicitudes/municipios") => MunicipiosFalla is { } fallaMunicipios
                    ? Error(fallaMunicipios, "NEXO_CAIDO", "Municipios no disponibles (prueba).")
                    : Json(HttpStatusCode.OK, Municipios.Select(m => new { id = m.Id, codigoDane = m.CodigoDane, municipio = m.Municipio, departamento = m.Departamento, pais = "Colombia" }).ToList()),
                ("POST", "/api/captura-solicitudes/analizar-excel") => AnalisisFalla is { } fallaAnalisis
                    ? Error(fallaAnalisis, CodigoAnalisisFalla, "No se pudo leer el archivo Excel (prueba).")
                    : Json(HttpStatusCode.OK, Analisis),
                ("POST", "/api/captura-solicitudes") => Alta(cuerpo),
                ("POST", var r) when r.StartsWith("/api/solicitudes/", StringComparison.Ordinal) && r.EndsWith("/rechazar", StringComparison.Ordinal) =>
                    Rechazar(r["/api/solicitudes/".Length..^"/rechazar".Length], cuerpo),
                ("GET", var r) when r.StartsWith("/api/solicitudes/", StringComparison.Ordinal) => Solicitud(r["/api/solicitudes/".Length..]),
                ("POST", "/api/cotizaciones") => CrearORecuperar(cuerpo),
                ("POST", var r) when r.StartsWith("/api/cotizaciones/", StringComparison.Ordinal) && r.EndsWith("/resultado", StringComparison.Ordinal) =>
                    MarcarResultado(r["/api/cotizaciones/".Length..^"/resultado".Length], cuerpo),
                ("POST", var r) when r.StartsWith("/api/cotizaciones/", StringComparison.Ordinal) && r.EndsWith("/conexos", StringComparison.Ordinal) =>
                    AgregarConexos(r["/api/cotizaciones/".Length..^"/conexos".Length], cuerpo),
                ("GET", var r) when r.StartsWith("/api/cotizaciones/por-solicitud/", StringComparison.Ordinal) =>
                    CotizacionPorSolicitud(r["/api/cotizaciones/por-solicitud/".Length..]),
                ("GET", var r) when r.StartsWith("/api/cotizaciones/", StringComparison.Ordinal) => LeerCotizacion(r["/api/cotizaciones/".Length..]),
                ("GET", "/api/servicios-conexos") => CatalogoConexos(request.RequestUri),
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

        if (CatalogosFallidos.Contains(nombre))
        {
            return Error(HttpStatusCode.InternalServerError, "ERROR_INTERNO", "Catálogo caído (prueba).");
        }

        if (nombre == "tipos-vehiculo" && CuerpoTiposVehiculo is { } crudo)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(crudo, Encoding.UTF8, "application/json") };
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

    /// <summary>
    /// HU-023 (#176): <c>GET /api/captura-solicitudes/ciudades?departamento=&amp;q=</c> como Nexo (<c>clsBuscarCiudadesQueryHandler</c>):
    /// sin tildes ni mayúsculas, el texto dentro del municipio y el departamento exacto, ordenado por nombre, máximo 50, sin <c>pais</c>.
    /// </summary>
    private HttpResponseMessage Ciudades(Uri url)
    {
        var consulta = System.Web.HttpUtility.ParseQueryString(url.Query);
        CiudadesConsultadas.Enqueue(url.Query);
        var q = Cotifly.Aplicacion.Municipios.Interfaces.IClsMunicipiosProvider.Normalizar(consulta["q"]);
        var departamento = Cotifly.Aplicacion.Municipios.Interfaces.IClsMunicipiosProvider.Normalizar(consulta["departamento"]);
        var filas = Municipios
            .Where(m => departamento.Length == 0 || Cotifly.Aplicacion.Municipios.Interfaces.IClsMunicipiosProvider.Normalizar(m.Departamento) == departamento)
            .Where(m => q.Length == 0 || Cotifly.Aplicacion.Municipios.Interfaces.IClsMunicipiosProvider.Normalizar(m.Municipio).Contains(q, StringComparison.Ordinal))
            .OrderBy(m => m.Municipio, StringComparer.Ordinal)
            .Take(50)
            .Select(m => new { id = m.Id, codigoDane = m.CodigoDane, municipio = m.Municipio, departamento = m.Departamento, nombreTarifasPlaza = (string?)null, zonaTarificacion = (string?)null })
            .ToList();
        return Json(HttpStatusCode.OK, filas);
    }

    /// <summary>
    /// HU-030.1 (#184): el índice (base 0) de la primera línea del alta cuyo <c>peso</c> no cuadra al 1 % con sus posiciones con peso
    /// (la regla de Nexo); <c>null</c> si todas cuadran o no traen posiciones.
    /// </summary>
    internal static int? LineaConPesoInconsistente(JsonNode? alta)
    {
        if (alta?["lineas"] is not JsonArray lineas)
        {
            return null;
        }

        for (var i = 0; i < lineas.Count; i++)
        {
            if (lineas[i]?["posiciones"] is not JsonArray { Count: > 0 } posiciones || lineas[i]?["peso"] is not JsonValue pesoNodo)
            {
                continue;
            }

            var suma = posiciones.Sum(p => p?["pesoKg"] is JsonValue kg && kg.GetValue<decimal>() > 0
                ? (p["cantidad"]?.GetValue<int>() ?? 1) * kg.GetValue<decimal>()
                : 0m);
            var peso = pesoNodo.GetValue<decimal>();
            if (suma > 0 && peso > 0 && Math.Abs(peso - suma) / peso > 0.01m)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>HU-023 (#176): la query de cada <c>GET /api/captura-solicitudes/ciudades</c> recibida.</summary>
    public ConcurrentQueue<string> CiudadesConsultadas { get; } = new();

    private HttpResponseMessage Alta(string? cuerpo)
    {
        if (AltaFalla is { } falla)
        {
            return Error(falla, CodigoAltaFalla, "Rechazada por el Nexo falso.");
        }

        UltimaAlta = JsonNode.Parse(cuerpo ?? "{}");
        if (LineaConPesoInconsistente(UltimaAlta) is { } inconsistente)
        {
            Interlocked.Increment(ref _altasPesoInconsistente);
            return Error(HttpStatusCode.BadRequest, "PESO_INCONSISTENTE",
                $"Línea {inconsistente + 1}: el peso no coincide con la suma de las posiciones (tolerancia 1 %).");
        }

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
                l?["carroceria"]?.GetValue<string>())
            {
                GrupoRutaId = l?["grupoRutaId"]?.GetValue<string>(),
                IdRecomendacionVehiculo = l?["idRecomendacionVehiculo"]?.GetValue<string>(),
            }));
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
        else if (AutoCostearSegunContrato && UltimaAlta?["lineas"] is JsonArray segunContrato)
        {
            solicitud.Cotizacion = new clsCotizacionFalsa(Guid.NewGuid().ToString());
            for (var i = 0; i < segunContrato.Count; i++)
            {
                var vehiculo = segunContrato[i]?["tipoVehiculo"]?.GetValue<string>();
                var clase = segunContrato[i]?["claseViaje"]?.GetValue<string>();
                if (vehiculo is not null && !TiposVehiculo.Any(t => t.Oficial && t.Codigo is not null && NormalizarComoNexo(t.Codigo) == NormalizarComoNexo(vehiculo)))
                {
                    Cuarentenas++;
                    continue;
                }

                if (vehiculo is null && (clase is null || clase == "Cupo"))
                {
                    continue; // "Línea sin vehículo: requiere consulta completa" (§4): PorCostear
                }

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
            scorePrioridad = ScorePrioridadAlta,
            fletesCalculados = enProceso ? 0 : solicitud.Cotizacion?.Fletes.Count ?? 0,
            lineasPorCostear = enProceso ? numero : numero - (solicitud.Cotizacion?.Fletes.Count ?? 0),
            costeoEnProceso = enProceso,
        });
    }

    private HttpResponseMessage Solicitud(string id)
    {
        if (!_solicitudes.TryGetValue(id, out var s))
        {
            return Error(HttpStatusCode.NotFound, "SOLICITUD_NO_ENCONTRADA", "Solicitud no encontrada.");
        }

        if (ConflictosPasajerosSolicitud > 0)
        {
            ConflictosPasajerosSolicitud--;
            return Error(HttpStatusCode.Conflict, "CONFLICTO_CONCURRENCIA", "La solicitud cambió mientras se leía (prueba).");
        }

        if (FallarConsultaTrasResultado && s.Cotizacion is { Resultado: not "SinDefinir" })
        {
            return Error(HttpStatusCode.ServiceUnavailable, "NEXO_CAIDO", "Consulta no disponible tras el resultado (prueba).");
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
            lineas = Indices(s).Select(i => ConSituacionLinea(s, i, new
            {
                lineaId = s.LineaIds[i],
                ruta = $"{s.Lineas[i].Origen} → {s.Lineas[i].Destino}",
                tipologia = s.Lineas[i].Tipologia,
                peso = s.Lineas[i].Peso,
                tipoVehiculo = s.Lineas[i].TipoVehiculo,
                carroceria = s.Lineas[i].Carroceria,
                // HU-023.4 (#159): clsLineaCosteoDto devuelve lo que recibió (null si no viajó).
                grupoRutaId = s.Lineas[i].GrupoRutaId,
                idRecomendacionVehiculo = s.Lineas[i].IdRecomendacionVehiculo,
                // Nexo #297 (S5-HU-20.5): posición de la línea en el arreglo del alta, desde 1.
                numeroLinea = i + 1,
            })).ToArray(),
        });
    }

    /// <summary>
    /// #181 (Nexo #309): con <see cref="ExponerSituacionLinea"/>, la línea lleva al final <c>estadoLinea</c> (minúscula inicial:
    /// <c>porCostear</c> o <c>autoResuelta</c>) y <c>motivoPendiente</c> (el de <see cref="MarcarMotivoPendiente"/>, solo si sigue por
    /// costear). Sin ella, la línea sale como la de un Nexo anterior a #309: sin esos campos.
    /// </summary>
    private JsonNode ConSituacionLinea(clsSolicitudFalsa s, int indice, object linea)
    {
        var nodo = JsonSerializer.SerializeToNode(linea, _json)!;
        if (ExponerSituacionLinea)
        {
            var costeada = s.Cotizacion?.Fletes.ContainsKey(indice) == true;
            nodo["estadoLinea"] = costeada ? "autoResuelta" : "porCostear";
            nodo["motivoPendiente"] = !costeada && s.Motivos.TryGetValue(indice, out var motivo) ? motivo : null;
        }

        return nodo;
    }

    /// <summary>#181: ¿<c>GET /api/solicitudes/{id}</c> trae <c>estadoLinea</c> y <c>motivoPendiente</c> por línea (Nexo con #309)? Por defecto no.</summary>
    public bool ExponerSituacionLinea { get; set; }

    /// <summary>#181: Nexo deja la línea <paramref name="indice"/> (base 0, orden de captura) pendiente con este motivo.</summary>
    public void MarcarMotivoPendiente(string solicitudId, int indice, string motivo) => Con(solicitudId, s => s.Motivos[indice] = motivo);

    /// <summary>
    /// <c>GET /api/cotizaciones/por-solicitud/{solicitudId}</c> (Nexo S5-HU-20.1, #286): la cotización que ya existe, SIN crear nada; 204 si
    /// todavía no hay (la crean el autocosteo, la Mesa o el "crear o recuperar").
    /// </summary>
    private HttpResponseMessage CotizacionPorSolicitud(string solicitudId)
    {
        if (SinCotizacionPorSolicitud)
        {
            return Error(HttpStatusCode.NotFound, "RUTA_NO_ENCONTRADA", $"GET /api/cotizaciones/por-solicitud/{solicitudId}");
        }

        if (!_solicitudes.TryGetValue(solicitudId, out var s))
        {
            return Error(HttpStatusCode.NotFound, "SOLICITUD_NO_ENCONTRADA", "Solicitud no encontrada.");
        }

        return s.Cotizacion is null ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, Cotizacion(s));
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

    /// <summary>
    /// HU-028 (#186): <c>POST /api/solicitudes/{id}/rechazar</c> como <c>clsRechazarSolicitudCommand</c> de Nexo: motivo de 1 a 100 y
    /// comentario hasta 500 (400 si no), 404 <c>SOLICITUD_NO_ENCONTRADA</c>, 409 <c>SOLICITUD_YA_DESCARTADA</c>; si no, la deja
    /// <c>Descartada</c> y responde 204.
    /// </summary>
    private HttpResponseMessage Rechazar(string solicitudId, string? cuerpo)
    {
        if (RechazoFalla is { } falla)
        {
            return Error(falla, CodigoRechazoFalla, "Rechazo no disponible (prueba).");
        }

        var nodo = JsonNode.Parse(cuerpo ?? "{}");
        var motivo = nodo?["motivo"]?.GetValue<string>();
        var comentario = nodo?["comentario"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 100 || comentario is { Length: > 500 })
        {
            return Error(HttpStatusCode.BadRequest, "VALIDATION_ERROR", "motivo (1-100) o comentario (hasta 500) inválido.");
        }

        if (!_solicitudes.TryGetValue(solicitudId, out var s))
        {
            return Error(HttpStatusCode.NotFound, "SOLICITUD_NO_ENCONTRADA", "Solicitud no encontrada.");
        }

        if (s.Estado == "Descartada")
        {
            return Error(HttpStatusCode.Conflict, "SOLICITUD_YA_DESCARTADA", "La solicitud ya está descartada.");
        }

        s.Estado = "Descartada";
        _rechazos.Add((solicitudId, motivo, comentario));
        return new HttpResponseMessage(HttpStatusCode.NoContent);
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

    /// <summary><c>GET /api/servicios-conexos</c>: filtro por activos, texto (nombre o código), categoría y página, como Nexo.</summary>
    private HttpResponseMessage CatalogoConexos(Uri url)
    {
        if (CatalogoFalla is { } falla)
        {
            return Error(falla, falla == HttpStatusCode.Forbidden ? "SIN_PERMISO" : "ERROR", "Catálogo rechazado por el Nexo falso.");
        }

        var parametros = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.OrdinalIgnoreCase);
        var soloActivos = parametros.TryGetValue("soloActivos", out var activos) && bool.Parse(activos);
        var q = parametros.GetValueOrDefault("q");
        var categoria = parametros.GetValueOrDefault("categoria");
        var page = int.Parse(parametros.GetValueOrDefault("page") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var pageSize = int.Parse(parametros.GetValueOrDefault("pageSize") ?? "50", System.Globalization.CultureInfo.InvariantCulture);
        var filtrados = _catalogo
            .Where(x => !soloActivos || x.Activo)
            .Where(x => string.IsNullOrEmpty(q) || x.Nombre.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Codigo.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrEmpty(categoria) || string.Equals(x.Categoria, categoria, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var items = filtrados.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new
        {
            id = x.Id, codigoServicio = x.Codigo, tipoServicio = x.Nombre, origen = (string?)null, destino = (string?)null, puerto = (string?)null,
            ruta = (string?)null, tipoMaquina = (string?)null, costoUnitario = x.Costo, unidad = x.Unidad, vigencia = "2026-12-31", categoria = x.Categoria,
            activo = x.Activo,
        }).ToList();
        return Json(HttpStatusCode.OK, new { items, total = filtrados.Count });
    }

    /// <summary><c>GET /api/cotizaciones/{id}</c>: la cotización con sus líneas y conexos (o 503 si se pidió fallar la relectura).</summary>
    private HttpResponseMessage LeerCotizacion(string cotizacionId)
    {
        var s = _solicitudes.Values.FirstOrDefault(x => x.Cotizacion?.Id == cotizacionId);
        if (s?.Cotizacion is not { } c)
        {
            return Error(HttpStatusCode.NotFound, "COTIZACION_NO_ENCONTRADA", "Cotización no encontrada.");
        }

        return FallarRelecturaTrasConexos && c.ConexosAgregados > 0
            ? Error(HttpStatusCode.ServiceUnavailable, "NEXO_CAIDO", "Relectura fallida (prueba).")
            : Json(HttpStatusCode.OK, Cotizacion(s));
    }

    /// <summary>
    /// <c>POST /api/cotizaciones/{id}/conexos</c> (<c>clsAgregarConexosRequest</c>): cada item exige una línea de la cotización y un
    /// servicio activo del catálogo; el valor aplicado lo calcula Nexo (costo unitario × cantidad) si no viene. Todo o nada.
    /// </summary>
    private HttpResponseMessage AgregarConexos(string cotizacionId, string? cuerpo)
    {
        if (ConexosFalla is { } falla)
        {
            return Error(falla, falla == HttpStatusCode.Forbidden ? "SIN_PERMISO" : "ERROR", "Conexos rechazados por el Nexo falso.");
        }

        var s = _solicitudes.Values.FirstOrDefault(x => x.Cotizacion?.Id == cotizacionId);
        if (s?.Cotizacion is not { } c)
        {
            return Error(HttpStatusCode.NotFound, "COTIZACION_NO_ENCONTRADA", "Cotización no encontrada.");
        }

        if (JsonNode.Parse(cuerpo ?? "{}")?["items"] is not JsonArray items || items.Count == 0)
        {
            return Error(HttpStatusCode.BadRequest, "VALIDATION_ERROR", "Items es obligatorio.");
        }

        var nuevos = new List<(string Id, string Nombre, string Categoria, decimal Cantidad, decimal Valor, string? ServicioId, string? LineaId)>();
        foreach (var item in items)
        {
            var lineaId = item?["lineaId"]?.GetValue<string>();
            var servicioId = item?["servicioConexoId"]?.GetValue<string>();
            var cantidad = item?["cantidad"]?.GetValue<decimal>() ?? 1m;
            if (!c.Fletes.Keys.Any(i => c.LineaId(i) == lineaId))
            {
                return Error(HttpStatusCode.BadRequest, "LINEA_NO_ENCONTRADA", "La línea no pertenece a la cotización.");
            }

            if (servicioId is not null && RechazosConexo.TryGetValue(servicioId, out var rechazo))
            {
                return Error(rechazo, rechazo == HttpStatusCode.NotFound ? "SERVICIO_CONEXO_NO_ENCONTRADO" : "SERVICIO_CONEXO_INVALIDO", "Servicio rechazado (prueba).");
            }

            var servicio = _catalogo.FirstOrDefault(x => string.Equals(x.Id, servicioId, StringComparison.OrdinalIgnoreCase));
            if (servicio is null)
            {
                return Error(HttpStatusCode.NotFound, "SERVICIO_CONEXO_NO_ENCONTRADO", "Servicio conexo no encontrado.");
            }

            if (!servicio.Activo)
            {
                return Error(HttpStatusCode.BadRequest, "SERVICIO_CONEXO_INACTIVO", "El servicio conexo está inactivo.");
            }

            var valor = item?["valorAplicado"]?.GetValue<decimal>() ?? servicio.Costo * cantidad;
            nuevos.Add((Guid.NewGuid().ToString(), servicio.Nombre, servicio.Categoria, cantidad, valor, servicio.Id, lineaId));
        }

        foreach (var nuevo in nuevos)
        {
            c.Conexos.Add((nuevo.Id, nuevo.Nombre, nuevo.Categoria, nuevo.Cantidad, nuevo.Valor));
            c.ServicioYLinea[nuevo.Id] = (nuevo.ServicioId, nuevo.LineaId);
        }

        c.ConexosAgregados++;
        if (CortarRespuestaConexos)
        {
            // Nexo aplicó el conexo pero la respuesta no llegó (red o tiempo agotado): el efecto es incierto para Cotifly.
            throw new HttpRequestException("Conexión cortada después de agregar los conexos (prueba).");
        }

        // Con la relectura fallida, Nexo responde 2xx sin cuerpo: Cotifly tiene que releer la cotización.
        return FallarRelecturaTrasConexos ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, Cotizacion(s));
    }

    private static object Cotizacion(clsSolicitudFalsa s)
    {
        var c = s.Cotizacion!;
        var lineas = c.Fletes.OrderByDescending(f => f.Key).Where(f => f.Key < s.LineaIds.Count).Select(f => new
        {
            id = c.LineaId(f.Key), lineaSolicitudId = (string?)s.LineaIds[f.Key], valorFlete = f.Value.Flete, fuente = "Sicetac",
            valorAprovisionamiento = f.Value.Aprovisionamiento, valorRetornoContenedor = f.Value.Retorno,
        }).ToArray();
        var conexos = c.Conexos.Select(x => new
        {
            id = x.Id,
            cotizacionLineaId = c.ServicioYLinea.TryGetValue(x.Id, out var sl) ? sl.LineaId : null,
            servicioConexoId = c.ServicioYLinea.TryGetValue(x.Id, out var sl2) ? sl2.ServicioId : null,
            nombre = x.Nombre,
            categoria = x.Categoria,
            cantidad = x.Cantidad,
            valorAplicado = x.Valor,
        }).ToList();
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

    /// <summary>
    /// Índices de las líneas en el orden en que las devuelve el falso: barajados con <see cref="SemillaOrdenLineas"/> o, sin semilla, al
    /// revés del de captura.
    /// </summary>
    private IEnumerable<int> Indices(clsSolicitudFalsa s)
    {
        var indices = Enumerable.Range(0, s.LineaIds.Count).ToArray();
        if (SemillaOrdenLineas is not { } semilla)
        {
            return Enumerable.Reverse(indices);
        }

        new Random(semilla).Shuffle(indices);
        return indices;
    }

    private sealed record clsLineaFalsa(string Origen, string Destino, string? Tipologia, decimal? Peso, string? TipoVehiculo, string? Carroceria)
    {
        public string? GrupoRutaId { get; init; }

        public string? IdRecomendacionVehiculo { get; init; }
    }

    private sealed class clsSolicitudFalsa(string id, int orden, IReadOnlyList<string> lineaIds)
    {
        public string Id { get; } = id;
        public int Orden { get; } = orden;
        public IReadOnlyList<string> LineaIds { get; } = lineaIds;
        public List<clsLineaFalsa> Lineas { get; } = [];
        public string Estado { get; set; } = "Pendiente";
        public clsCotizacionFalsa? Cotizacion { get; set; }

        /// <summary>#181: motivo pendiente por índice de línea (orden de captura).</summary>
        public Dictionary<int, string> Motivos { get; } = [];
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

        /// <summary>Servicio del catálogo y línea de la cotización de cada conexo agregado por <c>POST .../conexos</c>.</summary>
        public Dictionary<string, (string? ServicioId, string? LineaId)> ServicioYLinea { get; } = [];

        /// <summary>Veces que se agregaron conexos por la API (para fallar la relectura después).</summary>
        public int ConexosAgregados { get; set; }

        private readonly Dictionary<int, string> _lineaIds = [];

        /// <summary>Id ESTABLE de la línea de la cotización para el índice de línea de la solicitud (como el GUID de Nexo).</summary>
        public string LineaId(int indice)
        {
            if (!_lineaIds.TryGetValue(indice, out var id))
            {
                id = Guid.NewGuid().ToString();
                _lineaIds[indice] = id;
            }

            return id;
        }
    }

    private sealed record clsServicioCatalogoFalso(string Id, string Codigo, string Nombre, string Categoria, string Unidad, decimal Costo, bool Activo);
}
