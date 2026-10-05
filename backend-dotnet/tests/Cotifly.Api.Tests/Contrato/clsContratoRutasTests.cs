using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Api.Administracion.DTOs;
using Cotifly.Api.Agente.DTOs;
using Cotifly.Api.Comun;
using Cotifly.Api.Correo.DTOs;
using Cotifly.Api.Cotizaciones.DTOs;
using Cotifly.Api.Identidad.DTOs;
using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// La API expone exactamente lo que declara <c>specs/openapi.yaml</c> (SDD, ADR-010): cada ruta y método del contrato tiene un
/// endpoint (salvo los pendientes de implementar, listados aquí con su issue), cada endpoint de <c>/web/*</c> está en el contrato,
/// todos exigen sesión y los DTOs de <c>/web/*</c> se serializan en snake_case con exactamente las propiedades de su esquema
/// (salvo las propiedades pendientes de implementar, también listadas con su issue).
/// </summary>
public sealed partial class clsContratoRutasTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    /// <summary>
    /// Operaciones del contrato que todavía no existen, con la issue que las implementa (SDD: el contrato llega antes que el
    /// endpoint). Son las marcadas con <c>x-estado: pendiente</c> en el spec. Cuando una issue implementa su endpoint, quita su
    /// entrada: la prueba falla si un pendiente ya existe o si deja de estar en el contrato. HU-013.1 (#97, ADR-018).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PendientesDeImplementar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PUT /web/cotizaciones/{folio}/costeo/margen"] = "#98 (HU-013.2)",
        ["GET /web/servicios-conexos"] = "#99 (HU-013.3)",
        ["POST /web/cotizaciones/{folio}/costeo/servicios-conexos"] = "#99 (HU-013.3)",
        ["POST /web/cotizaciones/{folio}/costeo/re-solicitar"] = "#100 (HU-013.4)",
        ["POST /web/cotizaciones/{folio}/costeo/textos/redactar"] = "#101 (HU-013.5)",
        ["PUT /web/cotizaciones/{folio}/costeo/textos"] = "#101 (HU-013.5)",
    };

    /// <summary>
    /// Propiedades que el contrato ya declara en el esquema de un DTO de <c>/web/*</c> y que el DTO todavía no tiene, con la issue
    /// que las agrega (HU-013.1, #97). Se excluyen de la comparación exacta de propiedades; la prueba falla si el DTO ya las tiene
    /// (quítalas de aquí) o si el esquema ya no las declara. En los esquemas de respuesta son opcionales, así que las respuestas
    /// actuales siguen cumpliendo el contrato.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PropiedadesPendientes =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["SolicitudCosteoWeb"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["moneda"] = "#103 (HU-013.7)" },
            ["MensajeWeb"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["folio"] = "#102 (HU-013.6)" },
            ["RespuestaChat"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["accion_propuesta"] = "#102 (HU-013.6)" },
            ["CosteoCotizacion"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["margen_sugerido"] = "#98 (HU-013.2)",
                ["origen_margen"] = "#98 (HU-013.2)",
                ["trm_aplicada"] = "#103 (HU-013.7)",
                ["textos"] = "#101 (HU-013.5)",
                ["row_version"] = "#101 (HU-013.5)",
            },
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CadaOperacionDelContratoTieneSuEndpointSalvoLasPendientesDeImplementar()
    {
        var contrato = await OperacionesDelContratoAsync();
        var expuestas = OperacionesExpuestas();

        var faltantes = contrato.Where(o => !expuestas.Contains(o) && !PendientesDeImplementar.ContainsKey(o)).ToList();
        var pendientesYaImplementadas = PendientesDeImplementar.Keys.Where(expuestas.Contains).ToList();

        faltantes.Should().BeEmpty("cada operación de specs/openapi.yaml debe estar implementada");
        pendientesYaImplementadas.Should().BeEmpty("si ya existe, quítala de PendientesDeImplementar");
        PendientesDeImplementar.Keys.Should().OnlyContain(p => contrato.Contains(p), "los pendientes deben ser operaciones del contrato");
    }

    [Fact]
    public async Task LasOperacionesPendientesSonExactamenteLasMarcadasConXEstadoPendiente()
    {
        var contrato = await clsValidadorEsquemaOpenApi.ContratoAsync();
        var marcadas = contrato.Paths
            .SelectMany(p => p.Value.Operations!
                .Where(o => EstadoExtension(o.Value) == "pendiente")
                .Select(o => $"{o.Key.Method.ToUpperInvariant()} {p.Key}"))
            .ToList();

        marcadas.Should().BeEquivalentTo(PendientesDeImplementar.Keys, "x-estado: pendiente del spec y la lista de la prueba van juntos");
    }

    [Fact]
    public async Task NingunEndpointDeWebOPdfsQuedaFueraDelContrato()
    {
        var contrato = await OperacionesDelContratoAsync();

        var fuera = OperacionesExpuestas()
            .Where(o => o.Contains(" /web/", StringComparison.Ordinal) || o.Contains(" /pdfs/", StringComparison.Ordinal))
            .Where(o => !contrato.Contains(o))
            .ToList();

        fuera.Should().BeEmpty("no se exponen endpoints de /web/* ni /pdfs sin contrato");
    }

    public static TheoryData<string> OperacionesProtegidas()
    {
        var datos = new TheoryData<string>();
        foreach (var operacion in OperacionesDelContratoAsync().GetAwaiter().GetResult()
                     .Where(o => (o.Contains(" /web/", StringComparison.Ordinal) || o.Contains(" /pdfs/", StringComparison.Ordinal))
                                 && !PendientesDeImplementar.ContainsKey(o)))
        {
            datos.Add(operacion);
        }

        return datos;
    }

    [Theory]
    [MemberData(nameof(OperacionesProtegidas))]
    public async Task CadaEndpointDeWebYPdfsExigeSesionConErrorWeb(string operacion)
    {
        var partes = operacion.Split(' ');
        var ruta = ParametroRuta().Replace(partes[1], m => m.Value switch
        {
            "{id}" when partes[1].Contains("admin", StringComparison.Ordinal) => "1",
            "{id}" => Guid.NewGuid().ToString(),
            "{archivo}" => "PCN-XX-01011999-001.pdf",
            _ => "PCN-XX-01011999-001",
        });
        using var cliente = factory.CreateClient();
        using var peticion = new HttpRequestMessage(new HttpMethod(partes[0]), ruta);
        if (partes[0] is "POST" or "PUT")
        {
            peticion.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        using var respuesta = await cliente.SendAsync(peticion, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized, operacion);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
    }

    [Fact]
    public void CadaControllerDeWebUsaLaConvencionSnakeCaseYLosDemasNo()
    {
        var controllers = typeof(clsJsonWeb).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Tipo: t, Ruta: t.GetCustomAttribute<RouteAttribute>(inherit: true)?.Template ?? string.Empty))
            .ToList();

        var web = controllers.Where(c => c.Ruta.StartsWith("web", StringComparison.Ordinal)).ToList();
        web.Should().NotBeEmpty();
        web.Should().OnlyContain(c => c.Tipo.GetCustomAttribute<clsJsonSnakeCaseAttribute>(true) != null, "todo /web/* va en snake_case (ADR-002)");
        controllers.Where(c => !c.Ruta.StartsWith("web", StringComparison.Ordinal))
            .Should().OnlyContain(c => c.Tipo.GetCustomAttribute<clsJsonSnakeCaseAttribute>(true) == null, "/api/auth/*, /health y /pdfs conservan su convención");
    }

    public static TheoryData<Type, string> DtosWeb() => new()
    {
        { typeof(clsCotizacionResumen), "CotizacionResumen" },
        { typeof(clsMisCotizaciones), "MisCotizaciones" },
        { typeof(clsDatosNuevaVersion), "DatosNuevaVersion" },
        { typeof(clsLineaNegocioAdmin), "LineaNegocio" },
        { typeof(clsActividadComercial), "ActividadComercial" },
        { typeof(clsAjustesMargenComercial), "AjustesMargenComercial" },
        { typeof(clsMetricasComercial), "MetricasComercial" },
        { typeof(clsMetricasRegional), "MetricasRegional" },
        { typeof(clsComercialRegional), "ComercialRegional" },
        { typeof(clsPeriodoMetricas), "PeriodoMetricas" },
        { typeof(Cotifly.Dominio.Metricas.clsIndicadoresMetricas), "IndicadoresMetricas" },
        { typeof(Cotifly.Dominio.Metricas.clsPuntoSerieMensual), "PuntoSerieMensual" },
        { typeof(Cotifly.Dominio.Metricas.clsMetricaCiudad), "MetricaCiudad" },
        { typeof(Cotifly.Dominio.Metricas.clsConcentracionCiudades), "ConcentracionCiudades" },
        { typeof(clsTelefonoComercial), "TelefonoComercial" },
        { typeof(clsDestinatarios), "Destinatarios" },
        { typeof(clsVistaPreviaCorreo), "VistaPreviaCorreo" },
        { typeof(clsRespuestaEnvio), "RespuestaEnvio" },
        { typeof(clsSeguimientoWebRequest), "SeguimientoWeb" },
        { typeof(clsEnvioWebRequest), "EnvioWeb" },
        { typeof(clsActualizarLineaNegocioRequest), "ActualizarLineaNegocio" },
        { typeof(clsTelefonoRequest), "TelefonoComercial" },
        { typeof(clsRespuestaChat), "RespuestaChat" },
        { typeof(clsRespuestaChatError), "RespuestaChatError" },
        { typeof(clsMensajeWebRequest), "MensajeWeb" },
        { typeof(Cotifly.Api.Costeo.DTOs.clsSolicitudCosteoWebRequest), "SolicitudCosteoWeb" },
        { typeof(Cotifly.Api.Costeo.DTOs.clsPosicionCargaWebRequest), "PosicionCargaWeb" },
        { typeof(Cotifly.Aplicacion.Costeo.Services.clsCosteoCotizacion), "CosteoCotizacion" },
        { typeof(Cotifly.Aplicacion.Costeo.Services.clsServicioConexoPrecio), "ServicioConexoCosteo" },
        { typeof(Cotifly.Api.Costeo.DTOs.clsSeleccionServiciosConexosWebRequest), "SeleccionServiciosConexos" },
        { typeof(Cotifly.Api.Costeo.DTOs.clsResultadoWebRequest), "ResultadoWeb" },
        { typeof(Cotifly.Api.Licitaciones.DTOs.clsLicitacionWebRequest), "LicitacionWeb" },
        { typeof(Cotifly.Api.Licitaciones.DTOs.clsLineaLicitacionWebRequest), "LineaLicitacionWeb" },
        { typeof(Cotifly.Api.Licitaciones.DTOs.clsReporteLicitacionWeb), "ReporteLicitacionInvalida" },
        { typeof(Cotifly.Aplicacion.Licitaciones.DTOs.clsErrorFilaLicitacion), "ErrorFilaLicitacion" },
        { typeof(Cotifly.Aplicacion.Licitaciones.DTOs.clsAnalisisExcel), "AnalisisExcel" },
        { typeof(Cotifly.Aplicacion.Licitaciones.DTOs.clsHojaExcel), "HojaExcel" },
        { typeof(Cotifly.Aplicacion.Licitaciones.DTOs.clsLicitacion), "Licitacion" },
        { typeof(Cotifly.Aplicacion.Licitaciones.DTOs.clsLineaLicitacion), "LineaLicitacion" },
    };

    /// <summary>
    /// Con <see cref="clsJsonWeb.Opciones"/> cada DTO de <c>/web/*</c> produce (y lee) exactamente los nombres snake_case de su
    /// esquema: ni uno de más ni uno de menos. Así un campo renombrado en C# rompe esta prueba antes que al frontend.
    /// </summary>
    [Theory]
    [MemberData(nameof(DtosWeb))]
    public async Task LosDtosDeWebTienenExactamenteLasPropiedadesSnakeCaseDeSuEsquema(Type dto, string esquema)
    {
        var propiedades = (await clsValidadorEsquemaOpenApi.EsquemaAsync(esquema)).Properties!.Keys.ToList();
        var pendientes = PropiedadesPendientes.TryGetValue(esquema, out var deEsquema) ? deEsquema.Keys.ToList() : [];
        var delDto = clsJsonWeb.Opciones.GetTypeInfo(dto).Properties.Select(p => p.Name).ToList();

        pendientes.Should().OnlyContain(p => propiedades.Contains(p), "una propiedad pendiente debe estar en el esquema");
        delDto.Intersect(pendientes).Should().BeEmpty("si el DTO ya la tiene, quítala de PropiedadesPendientes");
        delDto.Should().BeEquivalentTo(propiedades.Except(pendientes));
    }

    [Fact]
    public async Task ElErrorNegocioDeWebCumpleElEsquemaConYSinDetalle()
    {
        var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync("ErrorNegocio");

        foreach (var error in new[]
                 {
                     new clsErrorDominio(clsCodigosError.CorreoDuplicado, "Duplicado"),
                     new clsErrorDominio(clsCodigosError.CorreoEnvioFallido, "Falló", "relay caído"),
                 })
        {
            var json = JsonSerializer.Serialize(clsErrorNegocioWeb.Desde(error), clsJsonWeb.Opciones);
            clsValidadorEsquemaOpenApi.Validar(json, esquema).Should().BeEmpty(json);
        }
    }

    /// <summary>Valor de la extensión <c>x-estado</c> de una operación, o <c>null</c>.</summary>
    private static string? EstadoExtension(OpenApiOperation operacion) =>
        operacion.Extensions is { } extensiones && extensiones.TryGetValue("x-estado", out var estado)
            && estado is JsonNodeExtension { Node: { } nodo }
            ? nodo.GetValue<string>()
            : null;

    /// <summary><c>"METODO /ruta/{}"</c> de cada operación del contrato, con los parámetros normalizados a su nombre.</summary>
    private static async Task<HashSet<string>> OperacionesDelContratoAsync()
    {
        var contrato = await clsValidadorEsquemaOpenApi.ContratoAsync();
        return contrato.Paths
            .SelectMany(p => p.Value.Operations!.Keys.Select(m => $"{m.Method.ToUpperInvariant()} {p.Key}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Operaciones que expone la API (endpoints de MVC), en el mismo formato, sin restricciones de ruta (<c>{id:int}</c>).</summary>
    private HashSet<string> OperacionesExpuestas()
    {
        var fuente = factory.Services.GetRequiredService<EndpointDataSource>();
        return fuente.Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m =>
                $"{m} /{RestriccionRuta().Replace(e.RoutePattern.RawText!.TrimStart('/'), "}")}"))
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{[a-z_]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex ParametroRuta();

    [GeneratedRegex(@":[^}]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex RestriccionRuta();
}
