using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Margen;

/// <summary>
/// <see cref="clsMargenResolverService"/> con repositorios simulados (NSubstitute) sembrados como el script 0001
/// y la muestra SAP TM del Python. Paridad con los tests <c>test_resolver_*</c> de <c>tests/test_margen.py</c>
/// (allí requerían Postgres; aquí son unitarios y se repiten contra SQL Server en Cotifly.Infraestructura.Tests).
/// </summary>
public sealed class clsMargenResolverServiceTests
{
    private static readonly Dictionary<string, string> _trqTypeSemilla = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nacional"] = "OPTN",
        ["Distribucion"] = "OPDS",
        ["Importacion"] = "OPIM",
        ["Exportacion"] = "OPEX",
        ["Internacional"] = "OPTI",
        ["Almacenamiento"] = "OHUB",
        ["Almacenamiento3PL"] = "OH3P",
        ["ITR"] = "OPSC",
    };

    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsSapMapeoRepository _mapeo = Substitute.For<IClsSapMapeoRepository>();
    private readonly clsFuenteMargenMuestra _fuente = new();
    private readonly clsLoggerFalso<clsMargenResolverService> _logger = new();
    private readonly clsRelojFalso _reloj = new();

    // HU-042.4 (#247): cada política tiene su id (1, 2, ...) y el mapeo responde el TrqType por ese id, como la FK de SapMapeoTrqType.
    private readonly List<clsMargenPolitica> _lineas = [.. _trqTypeSemilla.Keys.Select((l, i) => ConId(clsMargenPolitica.Crear(l, 0.15m).Valor, i + 1))];

    public clsMargenResolverServiceTests()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<IReadOnlyList<clsMargenPolitica>>(_lineas));
        _mapeo.TrqTypeDeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(_lineas.FirstOrDefault(l => l.Id == ci.ArgAt<int>(0)) is { } linea
                ? _trqTypeSemilla.GetValueOrDefault(linea.LineaNegocio)
                : null));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int IdDe(string linea) => _lineas.Single(l => l.LineaNegocio == linea).Id;

    private clsMargenResolverService Resolver(IClsFuenteMargen? fuente = null, string salesOrgDefault = "50000005", int avisoHoras = 48) =>
        new(_politicas, _mapeo, fuente ?? _fuente,
            Options.Create(new clsOpcionesMargen { SalesOrgDefault = salesOrgDefault, AvisoSincronizacionHoras = avisoHoras }), _reloj, _logger);

    private static clsMargenPolitica ConId(clsMargenPolitica politica, int id)
    {
        typeof(clsMargenPolitica).GetProperty(nameof(clsMargenPolitica.Id))!.SetValue(politica, id);
        return politica;
    }

    private async Task<clsSalidaMargen> ResolverOk(string linea, string? bp = null, string? salesOrg = null)
    {
        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen(linea, bp, salesOrg), Ct);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        return resultado.Valor.Salida;
    }

    // test_resolver_sap_tm
    [Fact]
    public async Task ConBpYRegistroEnSapElOrigenEsSapTm()
    {
        var r = await ResolverOk("Nacional", "15927");

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m);
        r.TrqType.Should().Be("OPTN");
        r.Anomalo.Should().BeFalse();
        r.Detalle.Should().Be("Margen propuesto SAP TM (tabla local, actualizado 2026-09-23) para cliente 15927 en OPTN: 13.5%");
        _fuente.Consultas.Should().Equal(("50000005", "OPTN", "15927"));
    }

    // test_resolver_fallback_cliente_nuevo + ADR-004 (decisión B): sin BP no se consulta SAP.
    [Fact]
    public async Task SinBpNoSeConsultaSapYSeUsaLaPoliticaDeLaLinea()
    {
        var r = await ResolverOk("Nacional");

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.TrqType.Should().Be("OPTN");
        r.Anomalo.Should().BeFalse();
        r.Detalle.Should().Be("Política de línea Nacional (cliente nuevo: sin BP de SAP, no se consulta SAP TM): 0.15.");
        _fuente.Consultas.Should().BeEmpty();
    }

    // test_resolver_fallback_cliente_sin_registro
    [Fact]
    public async Task ConBpSinRegistroEnSapSeUsaLaPolitica()
    {
        var r = await ResolverOk("Nacional", "99999");

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.Detalle.Should().Be("Política de línea Nacional (sin registro en SAP TM): 0.15.");
        _fuente.Consultas.Should().ContainSingle();
    }

    // test_resolver_marca_anomalo_negativo
    [Fact]
    public async Task UnMargenSapNegativoSeMarcaAnomalo()
    {
        var r = await ResolverOk("Nacional", "5000");

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(-0.192m);
        r.Anomalo.Should().BeTrue();
        r.Detalle.Should().Be(
            "Margen propuesto SAP TM (tabla local, actualizado 2026-09-23) para cliente 5000 en OPTN: -19.2% — ANÓMALO (fuera de [0, 60%]): validar antes de aplicar.");
    }

    // test_resolver_marca_anomalo_atipico
    [Fact]
    public async Task UnMargenSapAtipicoSeMarcaAnomalo()
    {
        var r = await ResolverOk("Importacion", "5001");

        r.Margen.Should().Be(0.999m);
        r.Anomalo.Should().BeTrue();
    }

    // test_resolver_respeta_sales_org
    [Fact]
    public async Task SeRespetaLaOrganizacionDeVentasPedida()
    {
        var r = await ResolverOk("Almacenamiento", "3301", "50000010");

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.15m);
        r.TrqType.Should().Be("OHUB");
        await _mapeo.Received(1).TrqTypeDeAsync(IdDe("Almacenamiento"), "50000010", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConLaOrganizacionPorDefectoUnRegistroDeOtraOrganizacionNoAplica()
    {
        var r = await ResolverOk("Almacenamiento", "3301");

        r.Origen.Should().Be("politica_linea");
        _fuente.Consultas.Should().Equal(("50000005", "OHUB", "3301"));
    }

    [Fact]
    public async Task LaOrganizacionPorDefectoSaleDeLasOpciones()
    {
        var resultado = await Resolver(salesOrgDefault: "50000010").ResolverAsync(new clsEntradaMargen("Almacenamiento", "3301"), Ct);

        resultado.Valor.Salida.Origen.Should().Be("sap_tm");
        _fuente.Consultas.Should().Equal(("50000010", "OHUB", "3301"));
    }

    // test_resolver_linea_desconocida
    [Theory]
    [InlineData("NoExiste")]
    [InlineData("Aérea")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnaLineaDesconocidaDevuelveMargenLineaDesconocida(string linea)
    {
        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen(linea, "15927"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
    }

    // #13: el Value se muestra tal como llega de SAP, como el f"{value}%" del Python (no con 2 decimales fijos).
    [Theory]
    [InlineData("Importacion", "580", "OPIM: 5%")]
    [InlineData("Distribucion", "2044", "OPDS: 9.75%")]
    [InlineData("Nacional", "5000", "OPTN: -19.2% — ANÓMALO")]
    public async Task ElDetalleMuestraElValueTalComoLlegaDeSap(string linea, string bp, string esperado) =>
        (await ResolverOk(linea, bp)).Detalle.Should().Contain(esperado);

    // Decisión de #13 (Gherkin "Resiliencia si SAP no responde" de HU-009): SAP caído → política, con warning.
    [Fact]
    public async Task SiLaFuenteSapFallaSeUsaLaPoliticaYSeRegistraUnWarning()
    {
        _fuente.NoDisponible = true;

        var r = await ResolverOk("Nacional", "15927");

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.TrqType.Should().Be("OPTN");
        r.Detalle.Should().Be($"Política de línea Nacional ({clsMargenResolverService.MotivoNoDisponible}): 0.15.");
        _fuente.Consultas.Should().ContainSingle();
        _logger.Registros.Should().ContainSingle(l => l.Nivel == LogLevel.Warning)
            .Which.Mensaje.Should().Contain("SAP TM").And.Contain("OPTN").And.Contain("timeout simulado");
    }

    // HU-042.4 (#247): sin política no hay TrqType (el mapeo se busca por el id de la política), así que no se consulta SAP y una fuente
    // caída ya no cambia el error: es MARGEN_LINEA_DESCONOCIDA (antes, MARGEN_FUENTE_NO_DISPONIBLE).
    [Fact]
    public async Task SiLaLineaNoTienePoliticaNoSeConsultaSapAunqueLaFuenteEsteCaida()
    {
        _fuente.NoDisponible = true;
        _lineas.RemoveAll(l => l.LineaNegocio == "Nacional");

        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        _fuente.Consultas.Should().BeEmpty();
        await _mapeo.DidNotReceive().TrqTypeDeAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConSapDisponibleYSinPoliticaSigueSiendoLineaDesconocida()
    {
        _lineas.RemoveAll(l => l.LineaNegocio == "Nacional");

        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen("Nacional", "99999"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        _logger.Registros.Should().BeEmpty();
    }

    [Fact]
    public async Task SinBpUnaFuenteCaidaNoAfecta()
    {
        _fuente.NoDisponible = true;

        (await ResolverOk("Nacional")).Origen.Should().Be("politica_linea");
    }

    [Fact]
    public async Task ConBpPeroSinTrqTypeMapeadoSeUsaLaPoliticaSinConsultarSap()
    {
        _mapeo.TrqTypeDeAsync(IdDe("ITR"), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));

        var r = await ResolverOk("ITR", "4110");

        r.Origen.Should().Be("politica_linea");
        r.TrqType.Should().BeNull();
        r.Detalle.Should().Be("Política de línea ITR (sin registro en SAP TM): 0.15.");
        _fuente.Consultas.Should().BeEmpty();
    }

    // HU-042.2 (#243): una copia local que nunca se sincronizó (ambiente sin el job de Datos) es "no configurado", no "caído".
    [Fact]
    public async Task ConLaFuenteNoConfiguradaSeUsaLaPoliticaSinError()
    {
        _fuente.EstaConfigurada = false;

        var r = await ResolverOk("Nacional", "15927");

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.TrqType.Should().Be("OPTN");
        r.Detalle.Should().Be("Política de línea Nacional (SAP TM no configurado: se usa la política de la línea): 0.15.");
        _fuente.Consultas.Should().BeEmpty("una copia nunca sincronizada no se lee");
        _logger.Registros.Should().BeEmpty("no configurado no es una caída");
    }

    // Deuda de ADR-008: la línea se resuelve sin tildes, mayúsculas ni espacios.
    [Theory]
    [InlineData("distribución", "Distribucion", "OPDS")]
    [InlineData("ALMACENAMIENTO 3PL", "Almacenamiento3PL", "OH3P")]
    [InlineData("IMPORTACIÓN", "Importacion", "OPIM")]
    [InlineData("  exportación ", "Exportacion", "OPEX")]
    [InlineData("almacenamiento3pl", "Almacenamiento3PL", "OH3P")]
    public async Task LaLineaSeResuelveSinTildesNiMayusculas(string recibida, string canonica, string trqType)
    {
        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen(recibida), Ct);

        resultado.Valor.LineaNegocio.Should().Be(canonica);
        resultado.Valor.Salida.TrqType.Should().Be(trqType);
        resultado.Valor.Salida.Detalle.Should().StartWith($"Política de línea {canonica} ");
        await _mapeo.Received(1).TrqTypeDeAsync(IdDe(canonica), "50000005", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElResultadoIncluyeElContextoDeLaPoliticaParaElEstado()
    {
        _lineas.Add(clsMargenPolitica.Crear("Especial", 1.2m, clsReglaMargen.MetodoMarkup).Valor);

        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen("especial"), Ct);

        resultado.Valor.MetodoCalculo.Should().Be("markup");
        resultado.Valor.MargenPolitica.Should().Be(1.2m);
        resultado.Valor.LineaNegocioId.Should().NotBeNull();
        resultado.Valor.Salida.Anomalo.Should().BeTrue("1.2 está fuera de [0, 60 %]");
        resultado.Valor.Salida.TrqType.Should().BeNull();
    }

    // HU-042.4 (#247): antes un TrqType mapeado por texto daba un margen de SAP sin política; con la FK, sin política no hay TrqType.
    [Fact]
    public async Task SinPoliticaNoHayTrqTypeNiSeConsultaSap()
    {
        var fuente = Substitute.For<IClsFuenteMargen>();

        var resultado = await Resolver(fuente).ResolverAsync(new clsEntradaMargen("Aérea", "777"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        await fuente.DidNotReceive().ConsultarAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElTrqTypeSeBuscaPorElIdDeLaPoliticaYLaOrganizacion()
    {
        await ResolverOk("Exportacion", "704", "50000005");

        await _mapeo.Received(1).TrqTypeDeAsync(IdDe("Exportacion"), "50000005", Arg.Any<CancellationToken>());
    }

    // HU-042.2 (#243): la copia local no caduca; con la última sincronización exitosa más vieja que SapTm:AvisoSincronizacionHoras el
    // detalle lo avisa y el margen de SAP se usa igual.
    [Fact]
    public async Task ConLaSincronizacionViejaSeUsaElMargenDeSapYElDetalleLoAvisa()
    {
        _fuente.UltimaSincronizacion = _reloj.AhoraUtc.AddHours(-49);

        var r = await ResolverOk("Nacional", "15927");

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m);
        r.Detalle.Should().StartWith("Margen propuesto SAP TM (tabla local, actualizado 2026-09-23) para cliente 15927 en OPTN: 13.5% — ")
            .And.Contain("sincronización con más de 48 h")
            .And.EndWith("el dato puede estar desactualizado.");
    }

    [Theory]
    [InlineData(48, false)]
    [InlineData(47, true)]
    public async Task ElAvisoDeSincronizacionUsaElUmbralDeLasOpciones(int umbralHoras, bool avisa)
    {
        _fuente.UltimaSincronizacion = _reloj.AhoraUtc.AddHours(-48);

        var resultado = await Resolver(avisoHoras: umbralHoras).ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct);

        resultado.Valor.Salida.Detalle.Contains($"sincronización con más de {umbralHoras} h", StringComparison.Ordinal).Should().Be(avisa);
    }

    [Fact]
    public async Task UnMargenAnomaloConSincronizacionViejaLlevaLosDosAvisos()
    {
        _fuente.UltimaSincronizacion = _reloj.AhoraUtc.AddDays(-5);

        var r = await ResolverOk("Nacional", "5000");

        r.Anomalo.Should().BeTrue();
        r.Detalle.Should().Contain("sincronización con más de 48 h").And.EndWith("— ANÓMALO (fuera de [0, 60%]): validar antes de aplicar.");
    }

    [Fact]
    public async Task SinFechasDeLaFuenteElDetalleSoloDiceTablaLocal()
    {
        var fuente = Substitute.For<IClsFuenteMargen>();
        fuente.ConsultarAsync("50000005", "OPTN", "15927", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(clsConsultaMargenSap.ConValor(13.5m)));

        var resultado = await Resolver(fuente).ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct);

        resultado.Valor.Salida.Detalle.Should().Be("Margen propuesto SAP TM (tabla local) para cliente 15927 en OPTN: 13.5%");
    }

    [Fact]
    public async Task ElPorcentajeConLaEscalaDeLaColumnaSeMuestraSinCerosALaDerecha()
    {
        var fuente = Substitute.For<IClsFuenteMargen>();
        fuente.ConsultarAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(clsConsultaMargenSap.ConValor(13.5000m, clsFuenteMargenMuestra.ActualizadoMuestra)));

        var r = (await Resolver(fuente).ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct)).Valor.Salida;

        r.Margen.Should().Be(0.135m);
        r.Detalle.Should().EndWith("OPTN: 13.5%");
    }

    [Fact]
    public async Task ConLaCopiaCaidaElMotivoEsNoDisponibleYSeRegistraUnWarning()
    {
        var fuente = Substitute.For<IClsFuenteMargen>();
        fuente.Modo.Returns("tabla_local");
        fuente.ConsultarAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<clsConsultaMargenSap>>(_ => throw new clsFuenteMargenNoDisponibleException("No se pudo leer la copia local del margen de SAP TM."));

        var r = (await Resolver(fuente).ResolverAsync(new clsEntradaMargen("Nacional", "15927"), Ct)).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoNoDisponible);
        _logger.Registros.Should().ContainSingle(l => l.Nivel == LogLevel.Warning).Which.Mensaje.Should().Contain("tabla_local");
    }

    [Fact]
    public async Task ElBpYLaOrganizacionSeRecortan()
    {
        var r = await ResolverOk("Nacional", " 15927 ", "  ");

        r.Origen.Should().Be("sap_tm");
        _fuente.Consultas.Should().Equal(("50000005", "OPTN", "15927"));
    }

    // #41 (auditoría de #12, F4): el comercial no cotiza con una línea inactiva (la administración sí la ve).
    [Fact]
    public async Task UnaLineaInactivaNoSeCotiza()
    {
        _lineas.Single(l => l.LineaNegocio == "Nacional").Desactivar();

        var resultado = await Resolver().ResolverAsync(new clsEntradaMargen("nacional", "15927"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        resultado.Error.Detalle.Should().Be(clsMargenResolverService.DetalleLineaInactiva);
        resultado.Error.Mensaje.Should().Contain("inactiva");
    }

    // HU-013.15 (#124): "Licitaciones" es una línea del catálogo de Nexo, no de Cotifly; el error dice qué líneas usar.
    [Fact]
    public async Task LasLineasActivasSonLasDeCotiflyOrdenadasSinLasInactivas()
    {
        _lineas.Single(l => l.LineaNegocio == "ITR").Desactivar();

        var activas = await Resolver().LineasActivasAsync(Ct);

        activas.Should().Equal("Almacenamiento", "Almacenamiento3PL", "Distribucion", "Exportacion", "Importacion", "Internacional", "Nacional");
    }

    [Theory]
    [InlineData("Licitaciones")]
    [InlineData(" licitaciónes ")]
    public void LaLineaLicitacionesDeNexoEnUnaLicitacionExplicaQueLineaUsar(string linea)
    {
        var error = clsMargenResolverService.LineaDesconocida(linea, ["Importacion", "Nacional"], esLicitacion: true);

        error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        error.Detalle.Should().Be(clsMargenResolverService.DetalleLineaDeNexo);
        error.Mensaje.Should().StartWith("'Licitaciones' es una línea de negocio del catálogo de Nexo, no de Cotifly")
            .And.EndWith("Usa una de estas: Importacion, Nacional.");
        error.Datos!["lineas_validas"].Should().BeEquivalentTo(new[] { "Importacion", "Nacional" });
    }

    [Fact]
    public void OtraLineaDesconocidaDiceQueNoExisteYQueLineasUsar()
    {
        var error = clsMargenResolverService.LineaDesconocida(" Marítima ", ["Nacional"]);

        error.Detalle.Should().BeNull();
        error.Mensaje.Should().Be("La línea de negocio 'Marítima' no existe en Cotifly. Usa una de estas: Nacional.");
        clsMargenResolverService.LineaDesconocida("Licitaciones", ["Nacional"]).Detalle.Should().BeNull("fuera de una licitación no se explica la línea de Nexo");
    }
}
