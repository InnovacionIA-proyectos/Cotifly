using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// <see cref="IClsMargenResolverService"/> y <see cref="IClsAjusteMargenService"/> con el registro real
/// (<c>AddAplicacion</c> + <c>AddInfraestructura</c>) sobre SQL Server 2019 con la semilla del 0001
/// (8 líneas al 15 %, TrqType OPTN…OPSC) y la muestra SAP TM del Python. Paridad con los tests
/// <c>requiere_pg</c> de <c>tests/test_margen.py</c>.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenResolverIntegracionTests : clsPruebaIntegracion
{
    private static readonly clsFuenteMargenMuestra _fuenteCompartida = new();

    public clsMargenResolverIntegracionTests(clsBaseDatosPruebas baseDatos)
        : base(baseDatos, s => s.AddSingleton<IClsFuenteMargen>(_fuenteCompartida))
    {
    }

    public static TheoryData<string, string> LineasSembradas => new()
    {
        { "OPTN", "Nacional" },
        { "OPDS", "Distribucion" },
        { "OPIM", "Importacion" },
        { "OPEX", "Exportacion" },
        { "OPTI", "Internacional" },
    };

    private Task<clsResultado<clsMargenResuelto>> ResolverAsync(string linea, string? bp = null, string? salesOrg = null) =>
        ConsultarAsync(s => s.GetRequiredService<IClsMargenResolverService>().ResolverAsync(new clsEntradaMargen(linea, bp, salesOrg), Ct));

    // test_resolver_sap_tm
    [Fact]
    public async Task ConBpYRegistroEnSapElOrigenEsSapTm()
    {
        var r = (await ResolverAsync("Nacional", "15927")).Valor.Salida;

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m);
        r.TrqType.Should().Be("OPTN");
        r.Anomalo.Should().BeFalse();
        r.Detalle.Should().Contain("15927");
    }

    // test_resolver_fallback_cliente_nuevo
    [Fact]
    public async Task SinBpSeUsaLaPoliticaSembrada()
    {
        var resuelto = (await ResolverAsync("Nacional")).Valor;

        resuelto.Salida.Origen.Should().Be("politica_linea");
        resuelto.Salida.Margen.Should().Be(0.15m);
        resuelto.Salida.TrqType.Should().Be("OPTN");
        resuelto.LineaNegocioId.Should().NotBeNull();
        resuelto.MetodoCalculo.Should().Be("margen");
    }

    // test_resolver_fallback_cliente_sin_registro
    [Fact]
    public async Task ConBpSinRegistroEnSapSeUsaLaPolitica() =>
        (await ResolverAsync("Nacional", "99999")).Valor.Salida.Origen.Should().Be("politica_linea");

    // test_resolver_marca_anomalo_negativo
    [Fact]
    public async Task UnMargenSapNegativoSeMarcaAnomalo()
    {
        var r = (await ResolverAsync("Nacional", "5000")).Valor.Salida;

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(-0.192m);
        r.Anomalo.Should().BeTrue();
        r.Detalle.Should().Contain("ANÓMALO");
    }

    // test_resolver_marca_anomalo_atipico
    [Fact]
    public async Task UnMargenSapAtipicoSeMarcaAnomalo()
    {
        var r = (await ResolverAsync("Importacion", "5001")).Valor.Salida;

        r.Margen.Should().Be(0.999m);
        r.Anomalo.Should().BeTrue();
    }

    // test_resolver_respeta_sales_org
    [Fact]
    public async Task SeRespetaLaOrganizacionDeVentasPedida()
    {
        // Almacenamiento (OHUB, organización 50000010) no está en el catálogo desde el 0009; se prueba con Nacional, cuyo registro
        // de SAP TM es de la organización 50000005: pedida esa, hay margen de SAP; pedida otra, no lo hay.
        var r = (await ResolverAsync("Nacional", "15927", "50000005")).Valor.Salida;
        var otra = (await ResolverAsync("Nacional", "15927", "50000010")).Valor.Salida;

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m);
        r.TrqType.Should().Be("OPTN");
        otra.Origen.Should().Be("politica_linea", "en la organización 50000010 el cliente no tiene registro");
    }

    // test_resolver_linea_desconocida
    [Fact]
    public async Task UnaLineaDesconocidaDevuelveMargenLineaDesconocida() =>
        (await ResolverAsync("NoExiste")).Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);

    // test_mapeo_todas_las_lineas
    [Theory]
    [MemberData(nameof(LineasSembradas))]
    public async Task CadaLineaSembradaTieneSuTrqTypeYSuPolitica(string trqType, string linea)
    {
        var (lineaDeTrq, resuelto) = await ConsultarAsync(async s =>
            (await s.GetRequiredService<IClsSapMapeoRepository>().LineaDeAsync(trqType, cancellationToken: Ct),
             await s.GetRequiredService<IClsMargenResolverService>().ResolverAsync(new clsEntradaMargen(linea), Ct)));

        lineaDeTrq.Should().Be(linea);
        resuelto.Valor.Salida.TrqType.Should().Be(trqType);
        resuelto.Valor.Salida.Margen.Should().Be(0.15m);
        resuelto.Valor.LineaNegocio.Should().Be(linea);
    }

    // Deuda de ADR-008: la collation CI_AS distingue tildes; el resolver compara sin ellas.
    [Theory]
    [InlineData("Distribución", "Distribucion", "OPDS")]
    [InlineData("importación", "Importacion", "OPIM")]
    [InlineData("  EXPORTACIÓN ", "Exportacion", "OPEX")]
    [InlineData("  NACIONAL ", "Nacional", "OPTN")]
    public async Task UnaLineaConTildesSeResuelveALaLineaSembrada(string recibida, string canonica, string trqType)
    {
        var resuelto = (await ResolverAsync(recibida)).Valor;

        resuelto.LineaNegocio.Should().Be(canonica);
        resuelto.Salida.TrqType.Should().Be(trqType);
        resuelto.Salida.Origen.Should().Be("politica_linea");
    }

    [Fact]
    public async Task ElAjusteNegociadoSePersisteConElComercialYLaCotizacion()
    {
        var usuario = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(usuario);
        await GuardarAsync(cotizacion);

        var resultado = await ConsultarAsync(s => s.GetRequiredService<IClsAjusteMargenService>().RegistrarAsync(
            new clsEntradaAjusteMargen(0.15m, 0.12m, usuario, Motivo: "Cliente estratégico", Costo: 2_850_000m, CotizacionId: cotizacion.Id), Ct));
        var ajustes = await ConsultarAsync(s => s.GetRequiredService<IClsMargenAjusteRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        resultado.Valor.Margen.Origen.Should().Be("negociado");
        resultado.Valor.Precio!.Precio.Should().Be(3_238_636m);
        var ajuste = ajustes.Should().ContainSingle().Subject;
        ajuste.Id.Should().Be(resultado.Valor.AjusteId);
        ajuste.UsuarioId.Should().Be(usuario);
        ajuste.Folio.Should().Be(cotizacion.Folio);
        ajuste.Diferencia.Should().Be(-0.03m);
        ajuste.CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task UnAjusteMarkupMayorQueUnoSePersiste()
    {
        var usuario = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(usuario);
        await GuardarAsync(cotizacion);

        var resultado = await ConsultarAsync(s => s.GetRequiredService<IClsAjusteMargenService>().RegistrarAsync(
            new clsEntradaAjusteMargen(0.8m, 1.25m, usuario, MetodoCalculo: "markup", CotizacionId: cotizacion.Id), Ct));
        var ajustes = await ConsultarAsync(s => s.GetRequiredService<IClsMargenAjusteRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        resultado.EsExitoso.Should().BeTrue();
        ajustes.Should().ContainSingle().Which.MargenAplicado.Should().Be(1.25m);
    }

    [Fact]
    public async Task UnAjusteSobreLaCotizacionDeOtroComercialSeRechaza()
    {
        var duena = await CrearUsuarioAsync();
        var otro = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(duena);
        await GuardarAsync(cotizacion);

        var resultado = await ConsultarAsync(s => s.GetRequiredService<IClsAjusteMargenService>().RegistrarAsync(
            new clsEntradaAjusteMargen(0.15m, 0.12m, otro, CotizacionId: cotizacion.Id), Ct));
        var ajustes = await ConsultarAsync(s => s.GetRequiredService<IClsMargenAjusteRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        resultado.Error.Codigo.Should().Be(clsCodigosError.AjusteCotizacionInvalida);
        ajustes.Should().BeEmpty();
    }
}
