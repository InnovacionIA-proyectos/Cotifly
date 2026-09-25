using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Api.Administracion.DTOs;
using Cotifly.Api.Comun;
using Cotifly.Api.Correo.DTOs;
using Cotifly.Api.Cotizaciones.DTOs;
using Cotifly.Api.Identidad.DTOs;
using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// La API expone exactamente lo que declara <c>specs/openapi.yaml</c> (SDD, ADR-010): cada ruta y método del contrato tiene un
/// endpoint (salvo los pendientes de la parte C de #12, marcados aquí), cada endpoint de <c>/web/*</c> está en el contrato,
/// todos exigen sesión y los DTOs de <c>/web/*</c> se serializan en snake_case con exactamente las propiedades de su esquema.
/// </summary>
public sealed partial class clsContratoRutasTests(clsCotiflyApiFactory factory) : IClassFixture<clsCotiflyApiFactory>
{
    /// <summary>
    /// Operaciones del contrato que todavía no existen: <c>/web/chat</c> (agente) y <c>/chat/events</c> (Google Chat) llegan con la
    /// parte C de #12. Cuando se implementen, esta lista debe quedar vacía (la prueba lo exige: un pendiente implementado falla).
    /// </summary>
    public static readonly IReadOnlySet<string> PendientesParteC = new HashSet<string>(StringComparer.Ordinal)
    {
        "POST /web/chat",
        "POST /chat/events",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CadaOperacionDelContratoTieneSuEndpointSalvoLasPendientesDeLaParteC()
    {
        var contrato = await OperacionesDelContratoAsync();
        var expuestas = OperacionesExpuestas();

        var faltantes = contrato.Where(o => !expuestas.Contains(o) && !PendientesParteC.Contains(o)).ToList();
        var pendientesYaImplementadas = PendientesParteC.Where(expuestas.Contains).ToList();

        faltantes.Should().BeEmpty("cada operación de specs/openapi.yaml debe estar implementada");
        pendientesYaImplementadas.Should().BeEmpty("si ya existe, quítala de PendientesParteC");
        PendientesParteC.Should().OnlyContain(p => contrato.Contains(p), "los pendientes deben ser operaciones del contrato");
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
                                 && !PendientesParteC.Contains(o)))
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
            "{archivo}" => "PCN-01011999-001.pdf",
            _ => "PCN-01011999-001",
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
        { typeof(clsTelefonoComercial), "TelefonoComercial" },
        { typeof(clsDestinatarios), "Destinatarios" },
        { typeof(clsVistaPreviaCorreo), "VistaPreviaCorreo" },
        { typeof(clsRespuestaEnvio), "RespuestaEnvio" },
        { typeof(clsSeguimientoWebRequest), "SeguimientoWeb" },
        { typeof(clsEnvioWebRequest), "EnvioWeb" },
        { typeof(clsActualizarLineaNegocioRequest), "ActualizarLineaNegocio" },
        { typeof(clsTelefonoRequest), "TelefonoComercial" },
    };

    /// <summary>
    /// Con <see cref="clsJsonWeb.Opciones"/> cada DTO de <c>/web/*</c> produce (y lee) exactamente los nombres snake_case de su
    /// esquema: ni uno de más ni uno de menos. Así un campo renombrado en C# rompe esta prueba antes que al frontend.
    /// </summary>
    [Theory]
    [MemberData(nameof(DtosWeb))]
    public async Task LosDtosDeWebTienenExactamenteLasPropiedadesSnakeCaseDeSuEsquema(Type dto, string esquema)
    {
        var propiedades = (await clsValidadorEsquemaOpenApi.EsquemaAsync(esquema)).Properties!.Keys;
        var info = clsJsonWeb.Opciones.GetTypeInfo(dto);

        info.Properties.Select(p => p.Name).Should().BeEquivalentTo(propiedades);
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
