using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Ajuste de margen sobre el costeo (HU-013.2, ADR-018 §3, §8 y §11): precio recalculado con el módulo de Precio (el mismo cálculo
/// que <see cref="clsPrecioService"/>), ajuste en <c>MargenAjustes</c>, PDF invalidado con historial, control de versión y auditoría.
/// </summary>
public sealed class clsAjusteMargenCosteoServiceTests
{
    private const int Version = 7;
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenAjusteRepository _ajustes = Substitute.For<IClsMargenAjusteRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsPrecioService _precios = new(Substitute.For<IClsProveedorTrm>(), Substitute.For<IClsCalendarioNegocio>());
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsAjusteMargenCosteoServiceTests()
    {
        _cotizacion = Costeada(_ana, "PCN-900555111-AP-05102026-001");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns([]);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SubirElMargenRecalculaElPrecioYLosConexosComoElModuloDePrecioYRegistraElAjuste()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.18m, null, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var vista = resultado.Valor;
        vista.PrecioFinal.Should().Be(_precios.Calcular(2_550_000m, 0.18m).Valor.Precio, "equivalencia con clsPrecioService");
        vista.PrecioFinal.Should().Be(3_109_756m);
        vista.MargenAplicado.Should().Be(0.18m);
        vista.MargenSugerido.Should().Be(0.15m);
        vista.OrigenMargen.Should().Be("negociado");
        vista.ServiciosConexos.Single().Precio.Should().Be(103_659m, "85.000 / (1 - 0,18), a pesos enteros");
        vista.ServiciosConexos.Single().Incluido.Should().BeFalse("la selección del comercial se conserva");
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a =>
            a.MargenPolitica == 0.15m && a.MargenAplicado == 0.18m && a.Diferencia == 0.03m && a.Motivo == null
            && a.UsuarioId == _ana && a.CotizacionId == _cotizacion.Id && a.Folio == _cotizacion.Folio));
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
        vista.RowVersion.Should().Be("8", "el ajuste sube la versión de negocio");
        await _auditoria.Received(1).RegistrarAsync(clsAjusteMargenCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task BajarDelSugeridoConJustificacionLaGuardaComoMotivo()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.12m, " Cliente estratégico ", Version, Ct);

        resultado.Valor.PrecioFinal.Should().Be(2_897_727m);
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a => a.Diferencia == -0.03m && a.Motivo == "Cliente estratégico"));
    }

    [Fact]
    public async Task BajarDelSugeridoSinJustificacionNoCambiaNadaYTraeElSugeridoEnElDetalle()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.10m, "  ", Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        resultado.Error.Datos.Should().ContainKey("margen_sugerido").WhoseValue.Should().Be(0.15m);
        _cotizacion.PrecioFinal.Should().Be(3_000_000m);
        _cotizacion.MargenAplicado.Should().Be(0.15m);
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsAjusteMargenCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ElMismoMargenVigenteNoRegistraNadaNiInvalidaElPdf()
    {
        _cotizacion.AsignarPdf("PCN-1.pdf");

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.15m, null, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.PdfPath.Should().Be("PCN-1.pdf");
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConPdfSinEnviarElAjusteLoInvalidaYLoDejaEnElHistorial()
    {
        _cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.18m, null, Version, Ct);

        resultado.Valor.PdfUrl.Should().BeNull();
        resultado.Valor.PdfDesactualizado.Should().BeTrue();
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h =>
            h.PdfPath == "/data/pdfs/PCN-1.pdf" && h.Motivo == MotivoInvalidacionPdf.AjusteMargen && h.UsuarioId == _ana));
    }

    [Fact]
    public async Task YaEnviadaRespondePdfYaEnviadoConLaLlamadaParaCrearLaVersion()
    {
        _cotizacion.AsignarPdf("PCN-1.pdf");
        _cotizacion.RegistrarEnvio();

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        var datos = resultado.Error.Datos!;
        datos["accion"].Should().Be("nueva_version");
        datos["metodo"].Should().Be("POST");
        datos["endpoint"].Should().Be($"/web/cotizaciones/{_cotizacion.Folio}/costeo/re-solicitar");
        ((IReadOnlyDictionary<string, object?>)datos["cuerpo"]!)["nueva_version"].Should().Be(true);
        _cotizacion.PdfPath.Should().Be("PCN-1.pdf");
    }

    [Fact]
    public async Task ConOtraVersionRespondeConflictoSinTocarNada()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.18m, null, 6, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _cotizacion.MargenAplicado.Should().Be(0.15m);
    }

    [Fact]
    public async Task SiLaVersionCambiaAlGuardarRespondeConflicto()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    [Fact]
    public async Task UnaAjenaOInexistenteEsNoEncontradaYNoSeAudita()
    {
        var resultado = await Servicio().AjustarAsync(Guid.NewGuid(), _cotizacion.Folio, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task EnUnaLicitacionCadaLineaSeRecalculaYElTotalEsLaSuma()
    {
        var lineas = clsLineaSolicitud.Desde(_cotizacion.Id, [Linea("YUMBO"), Linea("CALI")]);
        lineas[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m);
        lineas[1].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_550_000m);
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(lineas);

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, 0.20m, null, Version, Ct);

        lineas.Select(l => l.PrecioFinal).Should().Equal(1_250_000m, 1_937_500m);
        resultado.Valor.PrecioFinal.Should().Be(3_187_500m, "la suma de las líneas");
    }

    [Fact]
    public void EnDolaresConvierteConLaTrmFijadaDeLaCotizacion()
    {
        var usd = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-U", Moneda = "USD", TrmAplicada = 4000m, Costo = 2_550_000m, PrecioFinal = 750m, MargenPolitica = 0.15m,
            MargenAplicado = 0.15m, MetodoCalculo = "margen", EstadoCosteo = EstadoCosteo.Autocosteo,
        }).Valor;

        var recalculo = Servicio().Recalcular(usd, [], 0.18m);

        recalculo.Valor.PrecioFinal.Should().Be(777.44m, "3.109.756 / 4.000, con la TRM de la cotización y no la del día");
    }

    private clsAjusteMargenCosteoService Servicio() =>
        new(_cotizaciones, _ajustes, _precios, _unidad, _auditoria, Options.Create(new clsOpcionesPdf()));

    private static clsLineaSolicitudCosteo Linea(string origen) => new() { Origen = origen, Destino = "BOGOTA", PesoKg = 1m };

    /// <summary>Cotización costeada por Nexo: flete 2.550.000 con margen de la política (15 %) y un conexo de 85.000 excluido.</summary>
    public static clsCotizacionSolicitud Costeada(Guid usuarioId, string folio)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            UsuarioId = usuarioId,
            Costo = 2_550_000m,
            PrecioFinal = 3_000_000m,
            MargenPolitica = 0.15m,
            MargenAplicado = 0.15m,
            OrigenMargen = OrigenMargen.PoliticaLinea,
            MetodoCalculo = "margen",
            EstadoCosteo = EstadoCosteo.Autocosteo,
        }).Valor;
        cotizacion.SeleccionarServiciosConexos(clsServiciosConexosJson.Escribir([new clsServicioConexoCosteo("CX-1", "Cargue", "logistico", 1m, 85_000m, 100_000m, false)]));
        typeof(clsCotizacionSolicitud).GetProperty(nameof(clsCotizacionSolicitud.VersionNegocio))!.SetValue(cotizacion, Version);
        return cotizacion;
    }
}
