using System.Net;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// <see cref="IClsMargenResolverService"/> con el registro real y los adaptadores reales de SAP TM (#13) elegidos por
/// <c>SapTm:Modo</c>, sobre SQL Server 2019 con la semilla del 0001: <c>export_json</c> leyendo la muestra del Python y
/// <c>odata</c> contra el SAP falso (<see cref="clsSapTmFalso"/>, sin red) que responde con esa misma muestra.
/// Incluye <c>test_conmutacion_export_a_odata_misma_salida</c> para todas las filas de la muestra.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenFuentesSapIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    public static TheoryData<string, string?, string?> Consultas => new()
    {
        { "Nacional", "15927", null },
        { "Importacion", "580", null },
        { "Exportacion", "704", null },
        { "Internacional", "1123", null },
        { "Distribucion", "2044", null },
        { "Nacional", "15927", "50000010" },
        { "Nacional", "5000", null },
        { "Importacion", "5001", null },
        { "Nacional", "99999", null },
        { "Nacional", null, null },
    };

    // test_resolver_sap_tm con el adaptador export_json real (no la muestra en memoria)
    [Fact]
    public async Task ConExportJsonUnClienteConRegistroObtieneElMargenDeSap()
    {
        await using var proveedor = CrearProveedor(ConfiguracionExport(clsSapTmFalso.RutaMuestra));

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional", "15927"))).Valor.Salida;

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m, "el Value 13.5 se divide por 100 una sola vez");
        r.TrqType.Should().Be("OPTN");
        r.Anomalo.Should().BeFalse();
        r.Detalle.Should().Contain("15927").And.Contain("13.5%");
    }

    [Fact]
    public async Task ConExportJsonUnClienteSinRegistroUsaLaPolitica()
    {
        await using var proveedor = CrearProveedor(ConfiguracionExport(clsSapTmFalso.RutaMuestra));

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional", "99999"))).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoSinRegistro);
    }

    [Fact]
    public async Task ConExportJsonAusenteElResolverUsaLaPoliticaConElMotivo()
    {
        await using var proveedor = CrearProveedor(ConfiguracionExport(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")));

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional", "15927"))).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoNoDisponible);
    }

    [Fact]
    public async Task ConODataElResolverConsultaSapConElBpYObtieneSuMargen()
    {
        var sap = new clsSapTmFalso();
        await using var proveedor = CrearProveedorOData(sap);

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional", "15927"))).Valor.Salida;

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m);
        r.TrqType.Should().Be("OPTN");
        clsSapTmFalso.FiltroDe(sap.Peticiones.Should().ContainSingle().Subject.Url)
            .Should().Be("SalesOrgId eq '50000005' and TrqType eq 'OPTN' and OrderPartyId eq '15927'");
    }

    [Fact]
    public async Task ConODataCaidoElResolverUsaLaPoliticaSinRomperLaCotizacion()
    {
        var sap = new clsSapTmFalso();
        for (var i = 0; i <= clsFuenteMargenOData.Reintentos; i++)
        {
            sap.Responder(HttpStatusCode.ServiceUnavailable);
        }

        await using var proveedor = CrearProveedorOData(sap);

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional", "15927"))).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoNoDisponible);
        sap.Peticiones.Should().HaveCount(1 + clsFuenteMargenOData.Reintentos);
    }

    [Fact]
    public async Task ConODataSinBpNoSeLlamaASap()
    {
        var sap = new clsSapTmFalso();
        await using var proveedor = CrearProveedorOData(sap);

        var r = (await ResolverAsync(proveedor, new clsEntradaMargen("Nacional"))).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        sap.Peticiones.Should().BeEmpty();
    }

    // test_conmutacion_export_a_odata_misma_salida: cambiar SapTm:Modo no cambia la salida de obtener_margen.
    [Theory]
    [MemberData(nameof(Consultas))]
    public async Task ConmutarDeExportJsonAODataDaLaMismaSalida(string linea, string? bp, string? salesOrg)
    {
        var entrada = new clsEntradaMargen(linea, bp, salesOrg);
        await using var export = CrearProveedor(ConfiguracionExport(clsSapTmFalso.RutaMuestra));
        await using var odata = CrearProveedorOData(new clsSapTmFalso());

        var desdeExport = (await ResolverAsync(export, entrada)).Valor;
        var desdeOData = (await ResolverAsync(odata, entrada)).Valor;

        desdeOData.Salida.Should().Be(desdeExport.Salida);
        desdeOData.Should().BeEquivalentTo(desdeExport);
    }

    private static Dictionary<string, string?> ConfiguracionExport(string ruta) => new()
    {
        ["SapTm:Modo"] = "export_json",
        ["SapTm:ExportPath"] = ruta,
    };

    private ServiceProvider CrearProveedorOData(clsSapTmFalso sap) =>
        CrearProveedor(
            new Dictionary<string, string?>
            {
                ["SapTm:Modo"] = "odata",
                ["SapTm:Url"] = clsSapTmFalso.UrlBase,
                ["SapTm:User"] = "svc_cotifly",
                ["SapTm:Pass"] = "Kq7-Vr2m-Xz9p",
            },
            s => s.AddHttpClient(clsFuenteMargenOData.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap));

    private ServiceProvider CrearProveedor(Dictionary<string, string?> configuracion, Action<IServiceCollection>? configurar = null) =>
        clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad, configuracion, configurar: configurar);

    private static async Task<clsResultado<clsMargenResuelto>> ResolverAsync(ServiceProvider proveedor, clsEntradaMargen entrada)
    {
        await using var ambito = proveedor.CreateAsyncScope();
        return await ambito.ServiceProvider.GetRequiredService<IClsMargenResolverService>().ResolverAsync(entrada, Ct);
    }
}
