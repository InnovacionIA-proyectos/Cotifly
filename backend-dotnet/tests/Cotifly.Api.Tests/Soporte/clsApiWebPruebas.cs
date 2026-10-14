using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotifly.Pruebas.Web;

/// <summary>Sesión de prueba emitida por <c>POST /api/auth/login-clientepro</c>.</summary>
public sealed record clsSesionPrueba(string Access, Guid UsuarioId, string NombreUsuario);

/// <summary>Cotización creada para una prueba (folio generado por el servidor, ADR-011).</summary>
public sealed record clsCotizacionPrueba(Guid Id, string Folio, string? ArchivoPdf, string? NitCliente);

/// <summary>
/// API real en memoria (<see cref="WebApplicationFactory{TEntryPoint}"/>) sobre la base de Testcontainers, con ayudas para las
/// pruebas de <c>/web/*</c>: sesiones reales por el login SSO (persona en la <c>dbsolucionesti</c> falsa + regional en
/// <c>UsuariosWeb</c>), cotizaciones creadas con <see cref="IClsGeneradorFolioService"/> y PDF en el almacén temporal.
/// Se comparte entre <c>Cotifly.Api.Tests</c> y <c>Cotifly.Aceptacion.Tests</c> (archivo enlazado).
/// </summary>
public sealed class clsApiWebPruebas : IDisposable
{
    public const string ClaveJwt = "clave-jwt-de-pruebas-web-de-al-menos-32-bytes";

    /// <summary>Contenido mínimo de un PDF (el almacén no lo interpreta).</summary>
    public static readonly byte[] PdfMinimo = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");

    private readonly clsBaseDatosPruebas _baseDatos;

    public clsApiWebPruebas(
        clsBaseDatosPruebas baseDatos,
        bool correoHabilitado = false,
        IDictionary<string, string?>? extra = null,
        Action<IWebHostBuilder>? configurar = null,
        string entorno = "Pruebas")
    {
        ArgumentNullException.ThrowIfNull(baseDatos);
        _baseDatos = baseDatos;
        var valores = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cotifly"] = baseDatos.CadenaCotifly,
            ["ConnectionStrings:Identidad"] = baseDatos.CadenaIdentidad,
            ["Jwt:SigningKey"] = ClaveJwt,
            ["LimiteAuth:PeticionesPorVentana"] = "100000",
            ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal(),

            // La TRM (HU-012.1) nunca sale a la red: sin un manejador falso, las dos fuentes apuntan a hosts de prueba inexistentes.
            ["Trm:Url"] = clsTrmFalso.Url,
            ["Trm:UrlDolarApi"] = clsDolarApiFalso.Url,
        };
        if (correoHabilitado)
        {
            // Relay inalcanzable: un envío propio termina en CORREO_ENVIO_FALLIDO; uno ajeno se rechaza antes (404).
            valores["Correo:Habilitado"] = "true";
            valores["Correo:Modo"] = "smtp_relay";
            valores["Correo:SmtpRelay:Host"] = "127.0.0.1";
            valores["Correo:SmtpRelay:Port"] = "1";
            valores["Correo:SmtpRelay:TimeoutSeg"] = "2";
        }

        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        Factory = new clsFabricaApiWeb().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(entorno);
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores));

            // Reloj de la API con horas explícitas por flujo (CrearCotizacionAsync con creadaEn, #117); sin fijar, el del sistema.
            b.ConfigureTestServices(s => s.AddSingleton<IClsReloj>(sp => new clsRelojFijable(sp.GetRequiredService<TimeProvider>())));
            configurar?.Invoke(b);
        });
        Cliente = Factory.CreateClient();
    }

    public WebApplicationFactory<Program> Factory { get; }

    /// <summary>
    /// API con <c>Nexo:UrlBase</c> apuntando al Nexo falso (ADR-015): los clientes HTTP <c>Nexo</c> (búsquedas) y <c>NexoCosteo</c>
    /// (costeo) usan <paramref name="nexo"/> como manejador primario, sin salir a la red. Con <paramref name="dolarApi"/> y
    /// <paramref name="superfinanciera"/>, las fuentes de la TRM (ADR-014) también son falsas. Con <paramref name="modelo"/>, el
    /// <c>IChatClient</c> (agente y redactor de textos, HU-013.5) es ese doble: nunca un proveedor real. Con <paramref name="cubicador"/>
    /// (HU-021.5, #133), <c>Cubicador:UrlBase</c> y <c>Cubicador:ApiKey</c> apuntan al cubicador falso y su cliente HTTP lo usa como manejador.
    /// Con <paramref name="servicios"/>, reemplazos adicionales de DI (por ejemplo, una fuente de margen SAP TM fija, #139).
    /// </summary>
    public static clsApiWebPruebas ConNexo(
        clsBaseDatosPruebas baseDatos,
        clsNexoFalso nexo,
        IDictionary<string, string?>? extra = null,
        HttpMessageHandler? dolarApi = null,
        HttpMessageHandler? superfinanciera = null,
        Microsoft.Extensions.AI.IChatClient? modelo = null,
        HttpMessageHandler? cubicador = null,
        Action<IServiceCollection>? servicios = null)
    {
        ArgumentNullException.ThrowIfNull(nexo);
        // El worker del polling queda apagado por defecto: las pruebas procesan la agenda cuando quieren (ProcesarPollingAsync).
        var valores = new Dictionary<string, string?> { ["Nexo:UrlBase"] = clsNexoFalso.UrlBase, ["CosteoNexo:Habilitado"] = "false" };
        if (cubicador is not null)
        {
            valores["Cubicador:UrlBase"] = clsCubicadorFalso.UrlBase;
            valores["Cubicador:ApiKey"] = clsCubicadorFalso.ApiKey;

            // HU-023.5 (#160): el procesador de la cubicación en segundo plano queda apagado; las pruebas lo corren cuando quieren
            // (ProcesarCubicacionAsync), solo con las rutas de su licitación.
            valores["Cubicador:ProcesadorHabilitado"] = "false";
            valores["Cubicador:LoteRutas"] = "1000";
        }

        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        return new clsApiWebPruebas(baseDatos, extra: valores, configurar: b => b.ConfigureTestServices(s =>
        {
            s.AddHttpClient("Nexo").ConfigurePrimaryHttpMessageHandler(() => nexo);
            s.AddHttpClient("NexoCosteo").ConfigurePrimaryHttpMessageHandler(() => nexo);
            if (cubicador is not null)
            {
                s.AddHttpClient(Cotifly.Infraestructura.Cubicacion.Services.clsCubicadorHttp.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => cubicador);
            }

            if (dolarApi is not null)
            {
                s.AddHttpClient(Cotifly.Infraestructura.Precio.Services.clsProveedorTrmDolarApi.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => dolarApi);
            }

            if (modelo is not null)
            {
                s.AddSingleton(modelo);
            }

            if (superfinanciera is not null)
            {
                s.AddHttpClient(Cotifly.Infraestructura.Precio.Services.clsProveedorTrmSuperfinanciera.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => superfinanciera);
            }

            servicios?.Invoke(s);
        }));
    }

    public HttpClient Cliente { get; }

    /// <summary>
    /// Persona nueva en el almacén con el rol externo pedido, login por la API y, si viene, su regional en <c>UsuariosWeb</c>.
    /// </summary>
    public async Task<clsSesionPrueba> IniciarSesionAsync(int rolExterno, string? regional = null, string? nombreCompleto = null)
    {
        var nombre = $"web{Guid.NewGuid():N}"[..20];
        await _baseDatos.CrearPersonaExternaAsync(nombre, nombreCompleto ?? $"Persona {nombre}", $"{nombre}@boterosoto.com.co", rolExterno);
        using var respuesta = await Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre });
        if (respuesta.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"El login de prueba respondió {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");
        }

        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        var usuario = json.RootElement.GetProperty("user");
        var id = Guid.Parse(usuario.GetProperty("id").GetString()!);
        if (regional is not null)
        {
            await clsBaseDatosPruebas.EjecutarAsync(_baseDatos.CadenaCotifly,
                $"UPDATE dbo_cotifly.UsuariosWeb SET regional = N'{regional.Replace("'", "''", StringComparison.Ordinal)}' WHERE id = '{id}';");
        }

        return new clsSesionPrueba(json.RootElement.GetProperty("access").GetString()!, id, nombre);
    }

    /// <summary>
    /// Cotización del comercial con folio del servidor (<see cref="IClsGeneradorFolioService"/>), línea, precio y, si se pide,
    /// su PDF en el almacén (<c>pdfPath</c>). Con <paramref name="creadaEn"/>, ese es su <c>createdAt</c> (reloj fijado solo para esta
    /// creación, <see cref="clsRelojFijable"/>): las pruebas de orden no dependen del reloj real (#117).
    /// </summary>
    public async Task<clsCotizacionPrueba> CrearCotizacionAsync(
        Guid usuarioId, string? nit = null, bool conPdf = true, string linea = "Nacional", DateTimeOffset? creadaEn = null)
    {
        using var hora = creadaEn is { } fijada ? clsRelojFijable.Fijar(fijada) : null;
        await using var ambito = Factory.Services.CreateAsyncScope();
        var servicios = ambito.ServiceProvider;
        var politica = await servicios.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync(linea)
            ?? throw new InvalidOperationException($"La línea {linea} no existe.");
        var almacen = servicios.GetRequiredService<IClsAlmacenPdf>();
        var repositorio = servicios.GetRequiredService<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        var unidad = servicios.GetRequiredService<IClsUnitOfWork>();

        var resultado = await servicios.GetRequiredService<IClsGeneradorFolioService>().CrearConFolioNuevoAsync<clsCotizacionSolicitud>(usuarioId, nit, async (folio, ct) =>
        {
            string? referencia = null;
            if (conPdf)
            {
                referencia = await almacen.GuardarNuevoAsync($"{folio.Valor}.pdf", PdfMinimo, ct);
            }

            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
            {
                Folio = folio.Valor,
                Version = folio.Version,
                NitCliente = nit,
                ClienteNombre = nit is null ? null : $"Cliente {nit}",
                UsuarioId = usuarioId,
                PdfPath = referencia,
            });
            if (cotizacion.EsFallo)
            {
                return cotizacion.Error;
            }

            // HU-021: como la del chat, una ruta con la línea de negocio, el margen y el precio, y un camión con el flete.
            cotizacion.Valor.RegistrarCotizacionChat(politica.Id, 1_000_000m, 0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen", 1_176_471m);

            repositorio.Agregar(cotizacion.Valor);
            await unidad.GuardarCambiosAsync(ct);
            return clsResultado<clsCotizacionSolicitud>.Exito(cotizacion.Valor);
        });

        if (resultado.EsFallo)
        {
            throw new InvalidOperationException($"No se pudo crear la cotización de prueba: {resultado.Error.Codigo} {resultado.Error.Mensaje}");
        }

        var creada = resultado.Valor;
        return new clsCotizacionPrueba(creada.Id, creada.Folio, conPdf ? $"{creada.Folio}.pdf" : null, nit);
    }

    /// <summary>
    /// Una vuelta del polling de Nexo (HU-005.6) como la haría el worker, después de adelantar la agenda de la cotización
    /// <paramref name="folio"/> al año 2000 (queda primera en la agenda compartida) y, si se pide, su envío a Nexo
    /// <paramref name="enviadaHace"/> atrás. Devuelve cuántas procesó (puede incluir cotizaciones vencidas de otras pruebas).
    /// </summary>
    public async Task<int> ProcesarPollingAsync(string folio, TimeSpan? enviadaHace = null)
    {
        var folioSql = folio.Replace("'", "''", StringComparison.Ordinal);
        var envio = enviadaHace is { } hace
            ? $", costeoEnviadoAt = DATEADD(SECOND, -{(long)hace.TotalSeconds}, SYSDATETIMEOFFSET())"
            : string.Empty;
        await clsBaseDatosPruebas.EjecutarAsync(_baseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET costeoSiguienteConsultaAt = '2000-01-01T00:00:00+00:00'{envio} WHERE folio = N'{folioSql}' AND costeoSiguienteConsultaAt IS NOT NULL;");

        // Solo la cotización de ESTA prueba (como lo haría el worker con ella): la agenda es compartida entre pruebas que corren en
        // paralelo, y procesar el lote completo consultaba cotizaciones de otra prueba contra este Nexo falso (que no las conoce:
        // 404 → costeo_vencido en la otra prueba).
        var id = await clsBaseDatosPruebas.EscalarAsync<Guid?>(_baseDatos.CadenaCotifly,
            $"SELECT id FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = N'{folioSql}' AND costeoSiguienteConsultaAt IS NOT NULL;");
        if (id is not { } cotizacionId)
        {
            return 0;
        }

        await using var ambito = Factory.Services.CreateAsyncScope();
        var resultado = await ambito.ServiceProvider.GetRequiredService<Aplicacion.Costeo.Services.IClsPollingCosteoService>().ProcesarAsync(
            new Aplicacion.Cotizaciones.DTOs.clsConsultaCosteoPendiente(cotizacionId, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)), CancellationToken.None);
        return resultado == Aplicacion.Costeo.Services.ResultadoConsultaCosteo.NoReclamada ? 0 : 1;
    }

    /// <summary>
    /// HU-023.5 (#160): vueltas del procesador de la cubicación en segundo plano (<c>clsWorkerCubicacionLicitacion.ProcesarLoteAsync</c>)
    /// solo con las rutas de la licitación <paramref name="folio"/>, hasta que no quede ninguna pendiente con el turno vencido ni reclamada
    /// por un instante (otra réplica de la prueba la está cubicando), o hasta <paramref name="limite"/> (30 s por defecto). Se espera por
    /// tiempo y no por número de vueltas: una vuelta sin cupo libre no procesa nada y se repite tras una pausa corta. Devuelve cuántas rutas
    /// procesó esta réplica.
    /// </summary>
    public async Task<int> ProcesarCubicacionAsync(string folio, TimeSpan? limite = null)
    {
        var id = await IdCotizacionAsync(folio);
        var ambitos = Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var logger = Factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ProcesarCubicacion");
        var total = 0;
        // Reloj monotónico: con DateTime.UtcNow un salto del reloj del contenedor (WSL2) cortaba la espera antes de tiempo.
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var plazo = limite ?? TimeSpan.FromSeconds(30);
        while (reloj.Elapsed < plazo)
        {
            var procesadas = await Cotifly.Infraestructura.Cubicacion.Services.clsWorkerCubicacionLicitacion.ProcesarLoteAsync(ambitos, logger, id, CancellationToken.None);
            total += procesadas;
            // Pendientes con el turno vencido, o reclamadas hace poco (en curso en otra réplica: menos de 2 min de reclamo restante).
            var porTerminar = await clsBaseDatosPruebas.EscalarAsync<int>(_baseDatos.CadenaCotifly,
                "SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE estadoCubicacion = N'pendiente' "
                + "AND (cubicacionDisponibleAt IS NULL OR cubicacionDisponibleAt <= DATEADD(SECOND, 125, SYSDATETIMEOFFSET()) AND cubicacionIntentos = 0 "
                + $"OR cubicacionDisponibleAt <= SYSDATETIMEOFFSET()) AND cotizacionSolicitudId = '{id}';");
            if (porTerminar == 0)
            {
                break;
            }

            if (procesadas == 0)
            {
                await Task.Delay(50);
            }
        }

        return total;
    }

    /// <summary>Id de la cotización <paramref name="folio"/>.</summary>
    public async Task<Guid> IdCotizacionAsync(string folio) =>
        await clsBaseDatosPruebas.EscalarAsync<Guid>(_baseDatos.CadenaCotifly,
            $"SELECT id FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = N'{folio.Replace("'", "''", StringComparison.Ordinal)}';");

    /// <summary>
    /// Dos (o más) réplicas del worker procesan a la vez la misma cotización vencida en la agenda, cada una en su propio ámbito de DI
    /// (HU-021.9: el reclamo de la agenda deja pasar a una sola). Devuelve el resultado de cada una.
    /// </summary>
    public async Task<IReadOnlyList<Aplicacion.Costeo.Services.ResultadoConsultaCosteo>> ProcesarPollingEnParaleloAsync(string folio, int replicas)
    {
        var folioSql = folio.Replace("'", "''", StringComparison.Ordinal);
        await clsBaseDatosPruebas.EjecutarAsync(_baseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET costeoSiguienteConsultaAt = '2000-01-01T00:00:00+00:00' WHERE folio = N'{folioSql}' AND costeoSiguienteConsultaAt IS NOT NULL;");
        var id = await clsBaseDatosPruebas.EscalarAsync<Guid>(_baseDatos.CadenaCotifly,
            $"SELECT id FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = N'{folioSql}';");
        var pendiente = new Aplicacion.Cotizaciones.DTOs.clsConsultaCosteoPendiente(id, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return await Task.WhenAll(Enumerable.Range(0, replicas).Select(async _ =>
        {
            await using var ambito = Factory.Services.CreateAsyncScope();
            return await ambito.ServiceProvider.GetRequiredService<Aplicacion.Costeo.Services.IClsPollingCosteoService>()
                .ProcesarAsync(pendiente, CancellationToken.None);
        }));
    }

    /// <summary>Registra en <c>Correos</c> un envío exitoso de la cotización (para los destinatarios previos).</summary>
    public async Task RegistrarEnvioAsync(Guid cotizacionId, Guid usuarioId, string para)
    {
        await using var ambito = Factory.Services.CreateAsyncScope();
        var correo = clsCorreo.Crear(cotizacionId, [para], EstadoCorreo.Enviado, "Cotización", [], usuarioId, $"<{Guid.NewGuid():N}@pruebas>").Valor;
        ambito.ServiceProvider.GetRequiredService<IClsCorreoRepository>().Agregar(correo);
        await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync();
    }

    /// <summary>
    /// HU-021 (decisión 3): agrega en la ruta 1 de <paramref name="folio"/> un conexo de costo <paramref name="costo"/> como lo hace el
    /// comercial (<c>POST .../servicios-conexos</c>, que lo crea en Nexo y guarda el valor que Nexo calcula). El polling ya no importa
    /// los conexos de Nexo. Lanza si la API no responde 200.
    /// </summary>
    public async Task AgregarConexoAsync(clsNexoFalso nexo, string token, string folio, string nombre, decimal costo)
    {
        ArgumentNullException.ThrowIfNull(nexo);
        var id = Guid.NewGuid().ToString();
        nexo.ConServicioConexo(id, $"SC-{id[..8]}", nombre, "logistico", "servicio", costo);
        using var costeo = await EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{folio}/costeo", token);
        using var respuesta = await EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", token,
            Json(new { items = new[] { new { linea_orden = 1, servicio_id = id, cantidad = 1 } } }), costeo.Headers.ETag?.Tag);
        if (respuesta.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"No se pudo agregar el conexo {nombre}: {(int)respuesta.StatusCode} {await respuesta.Content.ReadAsStringAsync()}");
        }
    }

    /// <summary>
    /// Petición con <c>Authorization: Bearer</c> (si hay token), cuerpo JSON crudo (si viene) e <c>If-Match</c> (si viene, tal cual:
    /// la concurrencia optimista de HU-013, ADR-018 §8).
    /// </summary>
    public async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string ruta, string? token, string? cuerpoJson = null, string? ifMatch = null)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        if (token is not null)
        {
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (cuerpoJson is not null)
        {
            peticion.Content = new StringContent(cuerpoJson, Encoding.UTF8, "application/json");
        }

        return await Cliente.SendAsync(peticion);
    }

    /// <summary>Serializa un objeto anónimo tal cual (sin política de nombres): las pruebas escriben el snake_case a mano.</summary>
    public static string Json(object valor) => JsonSerializer.Serialize(valor);

    /// <summary>Petición con un cuerpo cualquiera (multipart de licitaciones, HU-011).</summary>
    public async Task<HttpResponseMessage> EnviarContenidoAsync(HttpMethod metodo, string ruta, string? token, HttpContent contenido)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta) { Content = contenido };
        if (token is not null)
        {
            peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await Cliente.SendAsync(peticion);
    }

    /// <summary>
    /// Multipart de <c>POST /web/licitaciones</c> (HU-011.2): la parte <c>licitacion</c> con el JSON y, si viene, la parte <c>archivo</c>.
    /// </summary>
    public static MultipartFormDataContent Licitacion(string json, byte[]? archivo, string nombreArchivo = "pliego.xlsx")
    {
        var multipart = new MultipartFormDataContent { { new StringContent(json, Encoding.UTF8), "licitacion" } };
        if (archivo is not null)
        {
            multipart.Add(new ByteArrayContent(archivo), "archivo", nombreArchivo);
        }

        return multipart;
    }

    /// <summary>Multipart con solo la parte <c>archivo</c> (<c>POST /web/licitaciones/analizar-excel</c>).</summary>
    public static MultipartFormDataContent Archivo(byte[] archivo, string nombreArchivo = "pliego.xlsx") =>
        new() { { new ByteArrayContent(archivo), "archivo", nombreArchivo } };

    /// <summary>Número con punto decimal (independiente de la cultura).</summary>
    public static string Numero(decimal valor) => valor.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        Cliente.Dispose();
        Factory.Dispose();
    }

    /// <summary>Fábrica con el entorno de pruebas (no Production: sin CORS obligatorio ni secretos).</summary>
    private sealed class clsFabricaApiWeb : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Pruebas");
        }
    }
}
