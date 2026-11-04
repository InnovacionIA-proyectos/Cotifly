using System.Net;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-042.2 (#243, decisión del 2026-11-04): de punta a punta por la API (<c>POST /web/solicitudes-costeo</c> → <c>GET .../costeo</c>) con la
/// copia local de SAP TM real y su última corrida <c>ok</c> de hace 72 h: el aviso de la sincronización vieja (antes solo en el detalle del
/// resolver, que el envío descartaba) sale en <c>aviso_margen</c> de la ruta, solo y junto al del margen anómalo (#139). Base AISLADA: la
/// compartida no debe tener una corrida <c>ok</c> vieja.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAvisoSincronizacionMargenEndpointsTests(clsEntornoApiWeb entorno)
{
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;

    [Theory]
    [InlineData("20", "sap_tm", false)]
    [InlineData("75", "politica_linea", true)]
    public async Task ConLaUltimaSincronizacionDeHace72HorasElCosteoLoAvisaEnAvisoMargen(string valorSap, string origen, bool anomalo)
    {
        var bp = $"66{Random.Shared.Next(100_000, 999_999)}";
        await using var aislada = await entorno.BaseDatos.CrearBaseConScriptsAsync("dbSapAviso", clsBaseDatosPruebas.Scripts.Length);
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, $"""
            UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoComercial} WHERE codigo = 'comercial';
            INSERT INTO dbo_cotifly.SapMargenCliente (salesOrgId, trqType, bpSap, nit, margenRecomendado)
            VALUES (N'50000005', N'OPIM', N'{bp}', N'{clsCosteoEndpointsTests.Nit}', {valorSap});
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, status)
            VALUES (DATEADD(HOUR, -72, SYSDATETIMEOFFSET()), DATEADD(HOUR, -72, SYSDATETIMEOFFSET()), 1, 0, 0, N'ok');
            """);
        using var nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.", bp: bp);
        using var api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, nexo, new Dictionary<string, string?> { ["ConnectionStrings:Cotifly"] = aislada.Cadena });
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, clsCosteoEndpointsTests.Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", ana.Access);
        var ruta = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas").EnumerateArray().Should().ContainSingle().Subject;

        ruta.GetProperty("origen_margen").GetString().Should().Be(origen);
        ruta.GetProperty("margen_sap_anomalo").GetBoolean().Should().Be(anomalo);
        var aviso = ruta.GetProperty("aviso_margen").GetString();
        aviso.Should().Contain("más de 48 h sin sincronizarse").And.Contain("puede estar desactualizado");
        if (anomalo)
        {
            aviso.Should().StartWith("SAP TM propuso un margen de 75 %", "el aviso del anómalo va primero y el de la sincronización después");
        }
    }
}
