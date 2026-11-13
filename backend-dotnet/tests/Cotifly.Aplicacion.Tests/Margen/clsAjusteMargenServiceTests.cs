using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Margen;

/// <summary>
/// Margen negociado: valida por método, persiste el ajuste con el comercial, devuelve origen
/// <c>negociado</c> y calcula el precio con el margen manual. Paridad con <c>registrar_ajuste_margen</c>
/// (<c>diferencia = aplicado − política</c>, <c>AJUSTE_COTIZACION_INVALIDA</c>).
/// </summary>
public sealed class clsAjusteMargenServiceTests
{
    private readonly IClsMargenAjusteRepository _ajustes = Substitute.For<IClsMargenAjusteRepository>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly Guid _usuario = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsAjusteMargenService Servicio() => new(_ajustes, _cotizaciones, _unidadTrabajo);

    [Fact]
    public async Task ElMargenNegociadoSeRegistraConElComercialYSeDevuelveConOrigenNegociado()
    {
        clsMargenAjuste? agregado = null;
        _ajustes.When(a => a.Agregar(Arg.Any<clsMargenAjuste>())).Do(ci => agregado = ci.Arg<clsMargenAjuste>());

        var resultado = await Servicio().RegistrarAsync(
            new clsEntradaAjusteMargen(0.15m, 0.12m, _usuario, Motivo: "Cliente estratégico", TrqType: "OPTN"), Ct);

        var salida = resultado.Valor;
        salida.Registrado.Should().BeTrue();
        salida.Diferencia.Should().Be(-0.03m);
        salida.Margen.Should().Be(new clsSalidaMargen(0.12m, "negociado", "OPTN", false,
            "Margen negociado por el comercial: 0.12 (política 0.15, diferencia -0.03). Motivo: Cliente estratégico."));
        salida.Precio.Should().BeNull();
        agregado.Should().NotBeNull();
        agregado!.UsuarioId.Should().Be(_usuario);
        agregado.MargenPolitica.Should().Be(0.15m);
        agregado.MargenAplicado.Should().Be(0.12m);
        agregado.Motivo.Should().Be("Cliente estratégico");
        salida.AjusteId.Should().Be(agregado.Id);
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElPrecioSeCalculaConElMargenManualSinRecalcularLaPolitica()
    {
        var resultado = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.12m, _usuario, Costo: 2_850_000m), Ct);

        // 2 850 000 / (1 − 0.12) = 3 238 636.36 → 3 238 636 (con la política 0.15 serían 3 352 941).
        resultado.Valor.Precio!.Precio.Should().Be(3_238_636m);
        resultado.Valor.Precio.Margen.Should().Be(0.12m);
        resultado.Valor.Precio.Utilidad.Should().Be(388_636m);
    }

    // Deuda de ADR-008: con markup el margen negociado puede ser ≥ 1.
    [Fact]
    public async Task ConMarkupSeAceptaUnMargenNegociadoMayorOIgualAUno()
    {
        var resultado = await Servicio().RegistrarAsync(
            new clsEntradaAjusteMargen(0.8m, 1.2m, _usuario, MetodoCalculo: "markup", Costo: 1_000_000m), Ct);

        resultado.Valor.Precio!.Precio.Should().Be(2_200_000m);
        resultado.Valor.Precio.Metodo.Should().Be("markup");
        resultado.Valor.Margen.Anomalo.Should().BeTrue("1.2 está fuera de [0, 60 %]");
        resultado.Valor.Diferencia.Should().Be(0.4m);
    }

    [Theory]
    [InlineData(0.15, 1.0, "margen")]
    [InlineData(0.15, 1.5, "margen")]
    [InlineData(0.15, -0.01, "margen")]
    [InlineData(0.15, -0.01, "markup")]
    [InlineData(1.0, 0.2, "margen")]
    [InlineData(0.15, 0.12, "promedio")]
    public async Task UnMargenFueraDeRangoParaSuMetodoSeRechazaSinGuardar(double politica, double aplicado, string metodo)
    {
        var resultado = await Servicio().RegistrarAsync(
            new clsEntradaAjusteMargen((decimal)politica, (decimal)aplicado, _usuario, MetodoCalculo: metodo), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.AjusteMargenInvalido, "PRECIO_INVALIDO queda solo para el cálculo de precio");
        await NoSeGuardoNada();
    }

    [Fact]
    public async Task LaSalidaYElPrecioUsanLosMargenesConLaEscalaQueSeGuarda()
    {
        clsMargenAjuste? agregado = null;
        _ajustes.When(a => a.Agregar(Arg.Any<clsMargenAjuste>())).Do(ci => agregado = ci.Arg<clsMargenAjuste>());

        var resultado = await Servicio().RegistrarAsync(
            new clsEntradaAjusteMargen(0.150049m, 0.123456m, _usuario, Costo: 2_850_000m), Ct);

        agregado!.MargenAplicado.Should().Be(0.1235m);
        agregado.MargenPolitica.Should().Be(0.15m);
        resultado.Valor.Margen.Margen.Should().Be(0.1235m);
        resultado.Valor.Diferencia.Should().Be(-0.0265m);
        resultado.Valor.Precio!.Margen.Should().Be(0.1235m);
        resultado.Valor.Precio.Precio.Should().Be(3_251_569m); // 2 850 000 / 0.8765 = 3 251 568.74 (con 0.123456 sin redondear daría 3 251 406)
        resultado.Valor.Margen.Detalle.Should().Contain("0.1235 (política 0.15, diferencia -0.0265)");
    }

    [Fact]
    public async Task UnCostoInvalidoSeRechazaSinGuardar()
    {
        var resultado = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.12m, _usuario, Costo: 0m), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        resultado.Error.Mensaje.Should().Contain("positivo");
        await NoSeGuardoNada();
    }

    [Fact]
    public async Task UnaCotizacionInexistenteOAjenaDevuelveAjusteCotizacionInvalida()
    {
        var cotizacionId = Guid.NewGuid();
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacionId, _usuario, Arg.Any<CancellationToken>()).Returns(Task.FromResult<clsCotizacionSolicitud?>(null));

        var resultado = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.12m, _usuario, CotizacionId: cotizacionId), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.AjusteCotizacionInvalida);
        resultado.Error.Mensaje.Should().Contain(cotizacionId.ToString());
        await NoSeGuardoNada();
    }

    [Fact]
    public async Task ConCotizacionDelComercialElAjusteTomaSuFolio()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-24092026", UsuarioId = _usuario }).Valor;
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacion.Id, _usuario, Arg.Any<CancellationToken>()).Returns(Task.FromResult<clsCotizacionSolicitud?>(cotizacion));
        clsMargenAjuste? agregado = null;
        _ajustes.When(a => a.Agregar(Arg.Any<clsMargenAjuste>())).Do(ci => agregado = ci.Arg<clsMargenAjuste>());

        var resultado = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.18m, _usuario, CotizacionId: cotizacion.Id), Ct);

        resultado.EsExitoso.Should().BeTrue();
        agregado!.CotizacionId.Should().Be(cotizacion.Id);
        agregado.Folio.Should().Be("PCN-24092026");
    }

    [Fact]
    public async Task ConUnaCotizacionDeUnaRutaElAjusteGuardaEsaRuta()
    {
        // MargenAjustes.lineaSolicitudId apunta a la ruta real (la única de la cotización, o la indicada).
        var cotizacion = Cotifly.Pruebas.Comun.clsCotizacionMuestra.Costeada(folio: "PCN-RUTA-1", usuarioId: _usuario);
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacion.Id, _usuario, Arg.Any<CancellationToken>()).Returns(Task.FromResult<clsCotizacionSolicitud?>(cotizacion));
        clsMargenAjuste? agregado = null;
        _ajustes.When(a => a.Agregar(Arg.Any<clsMargenAjuste>())).Do(ci => agregado = ci.Arg<clsMargenAjuste>());

        var sinRuta = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.18m, _usuario, CotizacionId: cotizacion.Id), Ct);
        var ajena = await Servicio().RegistrarAsync(
            new clsEntradaAjusteMargen(0.15m, 0.18m, _usuario, CotizacionId: cotizacion.Id, LineaSolicitudId: Guid.NewGuid()), Ct);
        var sinCotizacion = await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.18m, _usuario, LineaSolicitudId: Guid.NewGuid()), Ct);

        sinRuta.EsExitoso.Should().BeTrue();
        agregado!.LineaSolicitudId.Should().Be(cotizacion.PrimeraRuta!.Id);
        ajena.Error.Codigo.Should().Be(clsCodigosError.RutaInexistente);
        sinCotizacion.Error.Codigo.Should().Be(clsCodigosError.AjusteCotizacionInvalida);
    }

    [Fact]
    public async Task SinMotivoSeRegistraElMotivoPorDefecto()
    {
        clsMargenAjuste? agregado = null;
        _ajustes.When(a => a.Agregar(Arg.Any<clsMargenAjuste>())).Do(ci => agregado = ci.Arg<clsMargenAjuste>());

        await Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.12m, _usuario, Motivo: "  "), Ct);

        agregado!.Motivo.Should().Be(clsAjusteMargenService.MotivoPorDefecto);
    }

    [Fact]
    public async Task SinComercialLanzaArgumentException()
    {
        var registrar = () => Servicio().RegistrarAsync(new clsEntradaAjusteMargen(0.15m, 0.12m, Guid.Empty), Ct);

        await registrar.Should().ThrowAsync<ArgumentException>();
        await NoSeGuardoNada();
    }

    private async Task NoSeGuardoNada()
    {
        _ajustes.DidNotReceive().Agregar(Arg.Any<clsMargenAjuste>());
        await _unidadTrabajo.DidNotReceive().GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }
}
