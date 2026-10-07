using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>POST /web/chat</c> con el asistente por folio (HU-013.6, #102, ADR-018 §2b) por HTTP contra la base real y el modelo guionizado:
/// sin folio 400 <c>FOLIO_REQUERIDO</c> (sin llamar al modelo), folio ajeno o inexistente 404, folio demasiado largo 400, y una
/// cotización creada por el chat antes de HU-013 (<c>estadoCosteo = null</c>) sigue disponible para consultarla, sin montos hacia el
/// modelo y sin cambiar la base.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAsistenteFolioEndpointTests(clsEntornoApiWeb entorno)
{
    private const string RutaChat = "/web/chat";
    private static readonly HttpMethod _post = HttpMethod.Post;

    private static string SesionId() => Guid.NewGuid().ToString("N");

    private clsApiWebPruebas Api(clsChatClientGuionizado modelo, IDictionary<string, string?>? extra = null) =>
        new(entorno.BaseDatos, extra: extra, configurar: b => b.ConfigureTestServices(s => s.AddSingleton<IChatClient>(modelo)));

    [Fact]
    public async Task SinFolioRespondeFolioRequeridoSinLlamarAlModelo()
    {
        var modelo = new clsChatClientGuionizado();
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var sinCampo = await api.EnviarAsync(_post, RutaChat, ana.Access, clsApiWebPruebas.Json(new { mensaje = "Hola", sesion_id = SesionId() }));
        using var vacio = await api.EnviarAsync(_post, RutaChat, ana.Access, clsApiWebPruebas.Json(new { mensaje = "Hola", sesion_id = SesionId(), folio = "  " }));

        foreach (var respuesta in new[] { sinCampo, vacio })
        {
            var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.BadRequest);
            json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("FOLIO_REQUERIDO");
        }

        modelo.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFolioAjenoOInexistenteResponde404()
    {
        var modelo = new clsChatClientGuionizado();
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var beto = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var deAna = await api.CrearCotizacionAsync(ana.UsuarioId, "900555111", conPdf: false);

        using var ajeno = await api.EnviarAsync(_post, RutaChat, beto.Access, Mensaje("¿Cuál es el precio?", deAna.Folio));
        using var inexistente = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Hola", "PCN-NO-EXISTE-001"));

        foreach (var respuesta in new[] { ajeno, inexistente })
        {
            (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.NotFound)).GetProperty("error").GetString()
                .Should().Be("Cotización no encontrada o no es tuya.");
        }

        modelo.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFolioDeMasDeCincuentaCaracteresEs400()
    {
        using var api = Api(new clsChatClientGuionizado());
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Hola", new string('X', 51)));

        (await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.BadRequest)).GetProperty("error").GetString().Should().Be("folio inválido");
    }

    // Las cotizaciones creadas por el chat antes de HU-013 (estadoCosteo = null) siguen siendo del comercial: el asistente las consulta,
    // no recibe sus montos (costo 1.000.000, precio 1.176.471) y una propuesta de margen responde COSTEO_NO_LISTO sin tocar la base.
    [Fact]
    public async Task SobreUnaCotizacionDeChatAnteriorConsultaSinMontosYNoCambiaLaBase()
    {
        var modelo = new clsChatClientGuionizado()
            .Llamar("consultar_cotizacion", new { })
            .Llamar("proponer_ajuste_margen", new { margen = 0.18 })
            .Cerrar("Esta cotización no viene del costeo de Nexo: el margen se ajusta creando una nueva desde el formulario.");
        using var api = Api(modelo);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900555112", conPdf: false);
        var versionAntes = await VersionNegocioAsync(cotizacion.Folio);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Súbele el margen al 18 %", cotizacion.Folio.ToLowerInvariant()));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("herramientas").EnumerateArray().Select(h => h.GetString()).Should().Equal("consultar_cotizacion", "proponer_ajuste_margen");
        json.GetProperty("errores").EnumerateArray().Single().GetProperty("codigo").GetString().Should().Be("COSTEO_NO_LISTO");
        json.GetProperty("accion_propuesta").ValueKind.Should().Be(JsonValueKind.Null);
        foreach (var campo in new[] { "precio", "margen", "pdf" })
        {
            json.GetProperty(campo).ValueKind.Should().Be(JsonValueKind.Null, "obsoleto desde HU-013.6");
        }

        var alModelo = string.Join("\n", modelo.Llamadas.SelectMany(l => l.Mensajes).Select(m => m.Text).Concat(modelo.Llamadas.SelectMany(l => l.ResultadosHerramientas)));
        alModelo.Should().Contain(cotizacion.Folio).And.NotContain("1000000").And.NotContain("1.000.000").And.NotContain("1176471").And.NotContain("1.176.471");
        (await VersionNegocioAsync(cotizacion.Folio)).Should().Be(versionAntes, "el chat nunca escribe datos de negocio");
        (await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = '{cotizacion.Folio}'")).Should().Be(0);
    }

    // Con Chat:CreacionHabilitada=true y folio, responde el asistente (el agente legado solo atiende mensajes sin folio).
    [Fact]
    public async Task ConLaCreacionHabilitadaYFolioRespondeElAsistente()
    {
        var modelo = new clsChatClientGuionizado().Llamar("calcular_precio", new { costo = 1, margen = 0.5 }).Cerrar("Esa acción no está disponible aquí.");
        using var api = Api(modelo, new Dictionary<string, string?> { ["Chat:CreacionHabilitada"] = "true" });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900555113", conPdf: false);

        using var respuesta = await api.EnviarAsync(_post, RutaChat, ana.Access, Mensaje("Calcula el precio", cotizacion.Folio));

        var json = await VerificarContratoAsync(respuesta, RutaChat, _post, HttpStatusCode.OK);
        json.GetProperty("errores").EnumerateArray().Single().GetProperty("codigo").GetString().Should().Be("HERRAMIENTA_DESCONOCIDA");
        modelo.Llamadas[0].Herramientas.Should().NotContain("calcular_precio");
    }

    private static string Mensaje(string texto, string folio) =>
        clsApiWebPruebas.Json(new { mensaje = texto, sesion_id = SesionId(), folio });

    private async Task<int> VersionNegocioAsync(string folio) =>
        await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT versionNegocio FROM dbo_cotifly.Cotizaciones WHERE folio = '{folio}'");
}
