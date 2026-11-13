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
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Ajuste de margen sobre el costeo (ADR-018 §3, §8 y §11): precio recalculado con el módulo de Precio (el mismo cálculo
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
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SubirElMargenRecalculaElPrecioYLosConexosComoElModuloDePrecioYRegistraElAjuste()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var vista = resultado.Valor;
        // El precio de la ruta (y el total) es el flete con el margen de la ruta + la venta del conexo (85.000), sin margen.
        vista.PrecioFinal.Should().Be(_precios.Calcular(2_550_000m, 0.18m).Valor.Precio + 85_000m, "equivalencia con clsPrecioService");
        vista.PrecioFinal.Should().Be(3_194_756m);
        vista.MargenAplicado.Should().Be(0.18m);
        vista.MargenSugerido.Should().Be(0.15m);
        vista.OrigenMargen.Should().Be("negociado");
        vista.ServiciosConexos.Single().Precio.Should().Be(85_000m, "HU-043.2: la venta del conexo no cambia con el margen");
        vista.ServiciosConexos.Single().Incluido.Should().BeTrue("HU-021: un conexo de la ruta siempre va en el precio y en el PDF");
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a =>
            a.MargenPolitica == 0.15m && a.MargenAplicado == 0.18m && a.Diferencia == 0.03m && a.Motivo == null
            && a.UsuarioId == _ana && a.CotizacionId == _cotizacion.Id && a.Folio == _cotizacion.Folio
            && a.LineaSolicitudId == _cotizacion.PrimeraRuta!.Id));
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
        vista.RowVersion.Should().Be("8", "el ajuste sube la versión de negocio");
        await _auditoria.Received(1).RegistrarAsync(clsAjusteMargenCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task BajarDelSugeridoConJustificacionLaGuardaComoMotivo()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.12m, " Cliente estratégico ", Version, Ct);

        resultado.Valor.PrecioFinal.Should().Be(2_982_727m, "HU-043.2: 2.550.000 / 0,88 + 85.000 del conexo, sin margen");
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a => a.Diferencia == -0.03m && a.Motivo == "Cliente estratégico"));
    }

    [Fact]
    public async Task BajarDelSugeridoSinJustificacionNoCambiaNadaYTraeElSugeridoEnElDetalle()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.10m, "  ", Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        resultado.Error.Datos.Should().ContainKey("margen_sugerido").WhoseValue.Should().Be(0.15m);
        _cotizacion.PrecioFinal.Should().Be(3_085_000m);
        _cotizacion.PrimeraRuta!.MargenAplicado.Should().Be(0.15m);
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsAjusteMargenCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ElMismoMargenVigenteNoRegistraNadaNiInvalidaElPdf()
    {
        _cotizacion.AsignarPdf("PCN-1.pdf");

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.15m, null, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.PdfPath.Should().Be("PCN-1.pdf");
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConPdfSinEnviarElAjusteLoInvalidaYLoDejaEnElHistorial()
    {
        _cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

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

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        var datos = resultado.Error.Datos!;
        datos["accion"].Should().Be("nueva_version");
        datos["metodo"].Should().Be("POST");
        datos["endpoint"].Should().Be($"/web/cotizaciones/{_cotizacion.Folio}/costeo/re-solicitar");
        ((IReadOnlyDictionary<string, object?>)datos["cuerpo"]!)["nueva_version"].Should().Be(true);
        _cotizacion.PdfPath.Should().Be("PCN-1.pdf");
    }

    [Fact]
    public async Task FinalizadaRespondePdfYaEnviadoConLaLlamadaParaCrearLaVersionSinTocarElPrecio()
    {
        _cotizacion.Finalizar(_ana, clsCotizacionMuestra.Ahora);
        var precio = _cotizacion.PrecioFinal;

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        resultado.Error.Mensaje.Should().Contain("finalizada");
        resultado.Error.Datos!["accion"].Should().Be("nueva_version");
        _cotizacion.PrecioFinal.Should().Be(precio);
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConOtraVersionRespondeConflictoSinTocarNada()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, 6, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _cotizacion.PrimeraRuta!.MargenAplicado.Should().Be(0.15m);
    }

    [Fact]
    public async Task SiLaVersionCambiaAlGuardarRespondeConflicto()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    [Fact]
    public async Task UnaAjenaOInexistenteEsNoEncontradaYNoSeAudita()
    {
        var resultado = await Servicio().AjustarAsync(Guid.NewGuid(), _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    // ------------------------------------------------------------------ Margen por ruta

    [Fact]
    public async Task ConUnaSolaRutaElAjusteSinRutaLaTomaYElAjusteGuardaEsaRuta()
    {
        var resultado = await Servicio().AjustarAsync(_ana, _cotizacion.Folio, clsRutaIndicada.Ninguna, 0.18m, null, Version, Ct);

        resultado.Valor.Rutas.Should().ContainSingle().Which.MargenAplicado.Should().Be(0.18m);
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a => a.LineaSolicitudId == _cotizacion.PrimeraRuta!.Id));
    }

    [Fact]
    public async Task ConTresRutasSinIndicarLaRutaRespondeRutaRequeridaConLasRutasYNoCambiaNada()
    {
        var licitacion = Licitacion();

        var resultado = await Servicio().AjustarAsync(_ana, "PCN-L1", clsRutaIndicada.Ninguna, 0.20m, null, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RutaRequerida);
        ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)resultado.Error.Datos!["rutas"]!).Select(r => r["linea_orden"]).Should().Equal(1, 2, 3);
        licitacion.RutasOrdenadas.Select(r => r.MargenAplicado).Should().Equal(0.15m, 0.15m, 0.15m);
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsAjusteMargenCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ConTresRutasElAjusteDeLaRutaDosSoloRecalculaEsaRutaYRegistraLaRutaReal()
    {
        var licitacion = Licitacion();
        var segunda = licitacion.RutasOrdenadas[1];

        var resultado = await Servicio().AjustarAsync(_ana, "PCN-L1", clsRutaIndicada.DeOrden(2), 0.20m, null, Version, Ct);

        licitacion.RutasOrdenadas.Select(r => r.MargenAplicado).Should().Equal(0.15m, 0.20m, 0.15m);
        licitacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_000_000m, 2_125_000m, 500_000m);
        resultado.Valor.PrecioFinal.Should().Be(3_625_000m, "la suma de las rutas: solo cambió la 2");
        resultado.Valor.Rutas.Select(r => r.MargenAplicado).Should().Equal(0.15m, 0.20m, 0.15m);
        resultado.Valor.Rutas[1].OrigenMargen.Should().Be("negociado");
        _ajustes.Received(1).Agregar(Arg.Is<clsMargenAjuste>(a =>
            a.LineaSolicitudId == segunda.Id && a.MargenPolitica == 0.15m && a.MargenAplicado == 0.20m && a.CotizacionId == licitacion.Id));
    }

    [Fact]
    public async Task LaRutaTambienSeIndicaPorSuIdYSeValidaContraSuPropioSugerido()
    {
        var licitacion = Licitacion();
        var tercera = licitacion.RutasOrdenadas[2];
        tercera.RegistrarMargenCosteo(0.15m, 0.25m, OrigenMargen.SapTm, "margen");
        licitacion.RecalcularTotales(clsCotizacionMuestra.Ahora);

        var sinJustificar = await Servicio().AjustarAsync(_ana, "PCN-L1", new clsRutaIndicada(null, tercera.Id), 0.20m, null, Version, Ct);
        var otraRuta = await Servicio().AjustarAsync(_ana, "PCN-L1", new clsRutaIndicada(2, tercera.Id), 0.20m, "x", Version, Ct);

        sinJustificar.Error.Codigo.Should().Be(clsCodigosError.JustificacionRequerida, "la ruta 3 sugiere 25 % (SAP TM)");
        sinJustificar.Error.Datos!["margen_sugerido"].Should().Be(0.25m);
        sinJustificar.Error.Datos["linea_orden"].Should().Be(3);
        otraRuta.Error.Codigo.Should().Be(clsCodigosError.RutaInexistente);
    }

    [Fact]
    public async Task ConOtraVersionElConflictoVaAntesQueLaRuta()
    {
        Licitacion();

        var resultado = await Servicio().AjustarAsync(_ana, "PCN-L1", clsRutaIndicada.Ninguna, 0.20m, null, Version - 1, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
    }

    [Fact]
    public void LaVistaPreviaDeUnaRutaDaSuPrecioYElTotalSinCambiarNada()
    {
        var licitacion = Licitacion();

        var recalculo = clsAjusteMargenCosteoService.Recalcular(licitacion, licitacion.RutasOrdenadas[1], 0.20m).Valor;

        recalculo.PrecioRutaAnterior.Should().Be(2_000_000m);
        recalculo.PrecioRuta.Should().Be(2_125_000m);
        recalculo.PrecioFinal.Should().Be(3_625_000m);
        licitacion.PrecioFinal.Should().Be(3_500_000m, "la vista previa no cambia la cotización");
    }

    /// <summary>Pliego de 3 rutas al 15 %: fletes 850.000, 1.700.000 y 425.000 → precios 1.000.000, 2.000.000 y 500.000.</summary>
    private clsCotizacionSolicitud Licitacion()
    {
        var licitacion = clsCotizacionMuestra.ConVersionNegocio(
            clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], margen: 0.15m, folio: "PCN-L1", usuarioId: _ana), Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-L1", _ana, Arg.Any<CancellationToken>()).Returns(licitacion);
        _unidad.GuardarCambiosSiVersionAsync(licitacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        return licitacion;
    }

    [Fact]
    public void EnDolaresConvierteConLaTrmFijadaDeLaCotizacion()
    {
        var usd = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-U", moneda: "USD", trm: 4000m);
        usd.PrecioFinal.Should().Be(750m);

        var recalculo = clsAjusteMargenCosteoService.Recalcular(usd, usd.PrimeraRuta!, 0.18m);

        recalculo.Valor.PrecioFinal.Should().Be(777.44m, "3.109.756 / 4.000, con la TRM de la cotización y no la del día");
    }

    private clsAjusteMargenCosteoService Servicio() =>
        new(_cotizaciones, _ajustes, _unidad, _auditoria, Options.Create(new clsOpcionesPdf()));

    /// <summary>
    /// Cotización costeada por Nexo: flete 2.550.000 con margen de la política (15 %) y el conexo Cargue de 85.000 en la ruta. El
    /// conexo es una fila de la ruta y su precio va en el de la ruta.
    /// </summary>
    public static clsCotizacionSolicitud Costeada(Guid usuarioId, string folio)
    {
        var cotizacion = clsCotizacionMuestra.Formulario(folio, usuarioId);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");

        // Sin ids de Nexo (de la línea ni de la cotización): las pruebas que los necesitan los ponen aparte.
        ruta.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 2_550_000m, null);
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, null, null);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        return clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
    }

    /// <summary>
    /// <see cref="Costeada"/> ya finalizada por <paramref name="usuarioId"/> (el comercial aceptó el precio): la base de
    /// las pruebas del PDF y de los textos, que exigen la cotización finalizada.
    /// </summary>
    public static clsCotizacionSolicitud Finalizada(Guid usuarioId, string folio)
    {
        var cotizacion = Costeada(usuarioId, folio);
        cotizacion.Finalizar(usuarioId, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        return cotizacion;
    }
}
