using System.Net;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-042.2 (#243, ADR-027): <c>GET /web/admin/sap-margen/estado</c> y <c>POST /web/admin/sap-margen/sincronizar</c> por HTTP. Solo gerencia
/// (403 para el comercial), 503 <c>SAP_MARGEN_NO_CONFIGURADO</c> sin <c>SapTm:Url/User/Pass</c> y, configurada, una corrida forzada real con
/// el SAP falso sobre una base AISLADA (con la collation de la base real): la compartida nunca debe tener una corrida <c>ok</c>. El worker no
/// se registra en la API de estas pruebas: las corridas las fuerza la prueba.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSapMargenAdminEndpointsTests(clsEntornoApiWeb entorno)
{
    private const string Estado = "/web/admin/sap-margen/estado";
    private const string Sincronizar = "/web/admin/sap-margen/sincronizar";
    private const string SinPermiso = """{"error":"No tienes permiso para esta operación."}""";

    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeshabilitadaElEstadoRespondeConHabilitadaFalseYLaCorridaForzadaDa503()
    {
        // SapTm__SincronizacionHabilitada es false por defecto (decisión del 2026-11-04), aunque hubiera credenciales de SAP.
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set("40", 3));
        await using var aislada = await BaseAsync();
        using var api = Api(aislada.Cadena, sap, habilitada: false);
        var gloria = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var estado = await api.EnviarAsync(_get, Estado, gloria.Access);
        using var sincronizar = await api.EnviarAsync(_post, Sincronizar, gloria.Access);

        (await VerificarContratoAsync(estado, Estado, _get, HttpStatusCode.OK)).GetProperty("habilitada").GetBoolean().Should().BeFalse();
        var error = await VerificarContratoAsync(sincronizar, Sincronizar, _post, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(sincronizar, "SAP_MARGEN_NO_CONFIGURADO");
        error.GetProperty("error").GetProperty("mensaje").GetString().Should().Contain("deshabilitada");
        sap.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConLaUrlDeSapMalEscritaLaApiArrancaYResponde503SapMargenNoConfigurado()
    {
        var sap = new clsSapRentabFalso();
        using var api = new clsApiWebPruebas(entorno.BaseDatos, extra: new Dictionary<string, string?>
        {
            ["SapTm:SincronizacionHabilitada"] = "true",
            ["SapTm:Url"] = "sap-tm-prd:8000/sin-esquema",
            ["SapTm:User"] = clsSapRentabFalso.Usuario,
            ["SapTm:Pass"] = clsSapRentabFalso.Clave,
        });
        var gloria = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var estado = await api.EnviarAsync(_get, Estado, gloria.Access);
        using var sincronizar = await api.EnviarAsync(_post, Sincronizar, gloria.Access);

        var error = await VerificarContratoAsync(estado, Estado, _get, HttpStatusCode.ServiceUnavailable);
        await VerificarErrorNegocioAsync(estado, "SAP_MARGEN_NO_CONFIGURADO");
        error.GetProperty("error").GetProperty("detalle").GetProperty("motivo").GetString().Should().Contain("'SapTm:Url'").And.NotContain("sin-esquema");
        await VerificarErrorNegocioAsync(sincronizar, "SAP_MARGEN_NO_CONFIGURADO");
        sap.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task UnComercialRecibe403YNoSeLlamaASap()
    {
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set("41", 3));
        await using var aislada = await BaseAsync();
        using var api = Api(aislada.Cadena, sap);
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var estado = await api.EnviarAsync(_get, Estado, ana.Access);
        using var sincronizar = await api.EnviarAsync(_post, Sincronizar, ana.Access);

        await VerificarContratoAsync(estado, Estado, _get, HttpStatusCode.Forbidden);
        (await estado.Content.ReadAsStringAsync(Ct)).Should().Be(SinPermiso);
        await VerificarContratoAsync(sincronizar, Sincronizar, _post, HttpStatusCode.Forbidden);
        sap.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task LaGerenciaFuerzaUnaCorridaYElEstadoLaMuestra()
    {
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set("42", 4));
        await using var aislada = await BaseAsync();
        using var api = Api(aislada.Cadena, sap);
        var gloria = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var antes = await api.EnviarAsync(_get, Estado, gloria.Access);
        var jsonAntes = await VerificarContratoAsync(antes, Estado, _get, HttpStatusCode.OK);
        using var sincronizar = await api.EnviarAsync(_post, Sincronizar, gloria.Access);
        var resultado = await VerificarContratoAsync(sincronizar, Sincronizar, _post, HttpStatusCode.OK);
        using var despues = await api.EnviarAsync(_get, Estado, gloria.Access);
        var estado = await VerificarContratoAsync(despues, Estado, _get, HttpStatusCode.OK);

        jsonAntes.GetProperty("ultima_corrida").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        jsonAntes.GetProperty("filas_activas").GetInt32().Should().Be(0);
        resultado.GetProperty("estado").GetString().Should().Be("ok");
        resultado.GetProperty("filas_recibidas").GetInt32().Should().Be(4);
        resultado.GetProperty("corrida").GetProperty("filas_insertadas").GetInt32().Should().Be(4);
        estado.GetProperty("ultima_corrida_ok").GetProperty("estado").GetString().Should().Be("ok");
        estado.GetProperty("filas_activas").GetInt32().Should().Be(4);
        (estado.GetProperty("intervalo_minutos").GetInt32(), estado.GetProperty("chequeo_minutos").GetInt32()).Should().Be((1440, 60));
        estado.GetProperty("en_curso").GetBoolean().Should().BeFalse();
        estado.GetProperty("habilitada").GetBoolean().Should().BeTrue();
        sap.Peticiones.Should().ContainSingle().Which.Url.Query.Should().NotContain("filter");
        (await clsBaseDatosPruebas.EscalarAsync<int>(aislada.Cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'admin_sincronizar_margen_sap' AND exito = 1")).Should().Be(1);
    }

    [Fact]
    public async Task UnaCorridaForzadaQueFallaResponde200ConEstadoErrorYElMotivo()
    {
        var sap = new clsSapRentabFalso().Responder(HttpStatusCode.Unauthorized);
        await using var aislada = await BaseAsync();
        using var api = Api(aislada.Cadena, sap);
        var gloria = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var sincronizar = await api.EnviarAsync(_post, Sincronizar, gloria.Access);
        var resultado = await VerificarContratoAsync(sincronizar, Sincronizar, _post, HttpStatusCode.OK);

        resultado.GetProperty("estado").GetString().Should().Be("error");
        resultado.GetProperty("filas_recibidas").GetInt32().Should().Be(0);
        resultado.GetProperty("corrida").GetProperty("mensaje_error").GetString().Should().Contain("rechazó las credenciales")
            .And.NotContain(clsSapRentabFalso.Clave);
        (await clsBaseDatosPruebas.EscalarAsync<int>(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.SapMargenCliente")).Should().Be(0);
    }

    /// <summary>Base aislada con todos los scripts, la collation de la base real y los roles externos de prueba mapeados.</summary>
    private async Task<clsBaseAuxiliarPruebas> BaseAsync()
    {
        var aislada = await entorno.BaseDatos.CrearBaseConScriptsAsync("dbSapAdmin", clsBaseDatosPruebas.Scripts.Length, clsBaseDatosPruebas.CollationCotifly);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, $"""
            UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoComercial} WHERE codigo = 'comercial';
            UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoGerente} WHERE codigo = 'gerente_comercial';
            UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoDirector} WHERE codigo = 'director';
            """);
        return aislada;
    }

    private clsApiWebPruebas Api(string cadena, clsSapRentabFalso sap, bool habilitada = true) => new(
        entorno.BaseDatos,
        extra: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cotifly"] = cadena,
            ["SapTm:SincronizacionHabilitada"] = habilitada ? "true" : "false",
            ["SapTm:Url"] = clsSapRentabFalso.Url,
            ["SapTm:User"] = clsSapRentabFalso.Usuario,
            ["SapTm:Pass"] = clsSapRentabFalso.Clave,
            ["SapTm:MinimoFilas"] = "1",
        },
        configurar: b => b.ConfigureTestServices(s =>
        {
            s.AddHttpClient(clsClienteRentabSap.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap);
            foreach (var worker in s.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(clsWorkerSincronizacionMargenSap)).ToList())
            {
                s.Remove(worker);
            }
        }));
}
