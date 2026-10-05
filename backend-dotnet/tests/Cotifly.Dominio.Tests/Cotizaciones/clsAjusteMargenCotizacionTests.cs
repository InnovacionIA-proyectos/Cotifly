using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Invariantes del ajuste de margen sobre el costeo y de la regla del PDF (HU-013.2, ADR-018 §3 y §11): margen de 0 a 0,99,
/// justificación por debajo del sugerido, costo disponible y sin envío ni resultado.
/// </summary>
public sealed class clsAjusteMargenCotizacionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.18)]
    [InlineData(0.99)]
    public void UnMargenEntreCeroYNoventaYNueveEsValido(decimal margen) =>
        Costeada().ValidarAjusteMargen(margen, "Prueba").Should().BeNull();

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.991)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void UnMargenFueraDeRangoEsInvalido(decimal margen) =>
        Costeada().ValidarAjusteMargen(margen, "Prueba")!.Codigo.Should().Be(clsCodigosError.AjusteMargenInvalido);

    [Fact]
    public void UnaJustificacionDeMasDeQuinientosCaracteresEsInvalida() =>
        Costeada().ValidarAjusteMargen(0.10m, new string('x', 501))!.Codigo.Should().Be(clsCodigosError.AjusteMargenInvalido);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PorDebajoDelSugeridoSinJustificacionSeRechaza(string? justificacion)
    {
        var error = Costeada().ValidarAjusteMargen(0.12m, justificacion);

        error!.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        error.Mensaje.Should().Contain("0.12").And.Contain("0.15");
    }

    [Fact]
    public void PorEncimaOIgualAlSugeridoLaJustificacionEsOpcional()
    {
        Costeada().ValidarAjusteMargen(0.18m, null).Should().BeNull();
        Costeada().ValidarAjusteMargen(0.15m, null).Should().BeNull();
    }

    [Fact]
    public void SinCostoDisponibleNoSeAjusta()
    {
        var pendiente = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-2", MargenPolitica = 0.15m, MargenAplicado = 0.15m, MetodoCalculo = "margen", EstadoCosteo = EstadoCosteo.PendienteCosteo,
        }).Valor;

        pendiente.ValidarAjusteMargen(0.18m, null)!.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        pendiente.TieneCostoDisponible.Should().BeFalse();
    }

    [Fact]
    public void EnviadaAlClienteOConResultadoSePideUnaVersionNueva()
    {
        var enviada = Costeada();
        enviada.RegistrarEnvio();
        var ganada = Costeada();
        ganada.MarcarSeguimiento(true, null, DateTimeOffset.UnixEpoch);

        enviada.ValidarAjusteMargen(0.18m, null)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        ganada.ValidarAjusteMargen(0.18m, null)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        ganada.ValidarCambioEtapaFinal()!.Mensaje.Should().Contain("resultado");
        Costeada().ValidarCambioEtapaFinal().Should().BeNull("sin enviar y sin resultado se permite");
    }

    [Fact]
    public void ElAjusteGuardaElSugeridoYPasaANegociado()
    {
        var cotizacion = Costeada(sugeridoSap: 0.20m);

        cotizacion.AplicarMargenNegociado(0.25m).Should().BeTrue();

        cotizacion.MargenAplicado.Should().Be(0.25m);
        cotizacion.OrigenMargen.Should().Be(OrigenMargen.Negociado);
        cotizacion.MargenSugerido.Should().Be(0.20m, "el sugerido (SAP TM) se conserva");
        cotizacion.OrigenMargenSugerido.Should().Be(OrigenMargen.SapTm, "el ajuste no pierde la fuente del sugerido");
        cotizacion.MargenPolitica.Should().Be(0.15m, "el ajuste no sobrescribe margenPolitica");

        cotizacion.AplicarMargenNegociado(0.22m).Should().BeTrue();
        cotizacion.MargenSugerido.Should().Be(0.20m, "un segundo ajuste no pisa el sugerido");
        cotizacion.ValidarAjusteMargen(0.18m, null)!.Codigo.Should().Be(clsCodigosError.JustificacionRequerida, "la regla usa el sugerido de SAP TM");
    }

    [Fact]
    public void AjustarAlMismoMargenNoCambiaNada()
    {
        var cotizacion = Costeada();

        cotizacion.AplicarMargenNegociado(0.15m).Should().BeFalse();

        cotizacion.OrigenMargen.Should().Be(OrigenMargen.PoliticaLinea);
    }

    [Fact]
    public void ElMargenSeRedondeaACuatroDecimales()
    {
        var cotizacion = Costeada();

        cotizacion.AplicarMargenNegociado(0.123456m);

        cotizacion.MargenAplicado.Should().Be(0.1235m);
    }

    [Fact]
    public void InvalidarElPdfLoQuitaYLoMarcaDesactualizadoYGenerarloLoVuelveVigente()
    {
        var cotizacion = Costeada();
        cotizacion.InvalidarPdf().Should().BeNull("sin PDF no hay nada que invalidar");
        cotizacion.PdfDesactualizado.Should().BeFalse();
        cotizacion.AsignarPdf("PCN-1.pdf");

        cotizacion.InvalidarPdf().Should().Be("PCN-1.pdf");

        cotizacion.PdfPath.Should().BeNull();
        cotizacion.PdfDesactualizado.Should().BeTrue();
        cotizacion.AsignarPdf("PCN-1.pdf");
        cotizacion.PdfDesactualizado.Should().BeFalse();
    }

    [Fact]
    public void LaVersionDeNegocioEmpiezaEnCeroYSoloCoincideConLaVigente()
    {
        var cotizacion = Costeada();

        cotizacion.TieneVersion(0).Should().BeTrue();
        cotizacion.IncrementarVersionNegocio();
        cotizacion.TieneVersion(0).Should().BeFalse();
        cotizacion.TieneVersion(1).Should().BeTrue();
        cotizacion.TieneVersion(-1).Should().BeFalse("un If-Match inválido nunca coincide");
    }

    [Fact]
    public void ElSugeridoYSuOrigenSeFijanAlRegistrarElMargenDelCosteo()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-3" }).Valor;

        cotizacion.RegistrarMargenCosteo(0.15m, 0.21m, OrigenMargen.SapTm, "margen");

        (cotizacion.MargenSugerido, cotizacion.OrigenMargenSugerido, cotizacion.MargenPolitica).Should().Be((0.21m, OrigenMargen.SapTm, 0.15m));
    }

    [Fact]
    public void LaJustificacionSeGuardaSinCaracteresDeControl()
    {
        clsMargenAjuste.LimpiarJustificacion(" Cliente\r\nestratégico\t\0 ").Should().Be("Cliente  estratégico");
        clsMargenAjuste.LimpiarJustificacion("\n\t").Should().BeNull();
        clsMargenAjuste.RegistrarNegociacionCosteo(0.15m, 0.12m, "\r\n", Guid.NewGuid(), Guid.NewGuid(), "PCN-1").Error.Codigo
            .Should().Be(clsCodigosError.JustificacionRequerida, "una justificación de solo caracteres de control cuenta como ausente");
    }

    [Fact]
    public void ElHistorialExigeCotizacionYReferencia()
    {
        var historial = clsCotizacionPdfHistorial.Registrar(Guid.NewGuid(), " PCN-1.pdf ", MotivoInvalidacionPdf.AjusteMargen, null);

        historial.PdfPath.Should().Be("PCN-1.pdf");
        clsCodigoEnum.ACodigo(historial.Motivo).Should().Be("ajuste_margen");
        clsCodigoEnum.ACodigo(MotivoInvalidacionPdf.ReSolicitud).Should().Be("re_solicitud");
        FluentActions.Invoking(() => clsCotizacionPdfHistorial.Registrar(Guid.Empty, "x.pdf", MotivoInvalidacionPdf.Textos, null))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => historial.Archivar(new string('x', 401))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ElAjusteDelCosteoGuardaElSugeridoLaDiferenciaYLaJustificacionOpcional()
    {
        var cotizacionId = Guid.NewGuid();
        var usuarioId = Guid.NewGuid();

        var arriba = clsMargenAjuste.RegistrarNegociacionCosteo(0.15m, 0.18m, null, usuarioId, cotizacionId, "PCN-1").Valor;
        var abajo = clsMargenAjuste.RegistrarNegociacionCosteo(0.15m, 0.12m, " Cliente estratégico ", usuarioId, cotizacionId, "PCN-1").Valor;

        (arriba.MargenPolitica, arriba.MargenAplicado, arriba.Diferencia, arriba.Motivo).Should().Be((0.15m, 0.18m, 0.03m, (string?)null));
        (abajo.Diferencia, abajo.Motivo, abajo.UsuarioId, abajo.CotizacionId).Should().Be((-0.03m, "Cliente estratégico", usuarioId, cotizacionId));
        clsMargenAjuste.RegistrarNegociacionCosteo(0.15m, 0.12m, "  ", usuarioId, cotizacionId, "PCN-1").Error.Codigo
            .Should().Be(clsCodigosError.JustificacionRequerida);
    }

    private static clsCotizacion Costeada(decimal? sugeridoSap = null) => clsCotizacion.Crear(new clsDatosCotizacion
    {
        Folio = "PCN-1",
        Costo = 2_550_000m,
        PrecioFinal = 3_000_000m,
        MargenPolitica = 0.15m,
        MargenAplicado = sugeridoSap ?? 0.15m,
        OrigenMargen = sugeridoSap is null ? OrigenMargen.PoliticaLinea : OrigenMargen.SapTm,
        MetodoCalculo = "margen",
        EstadoCosteo = EstadoCosteo.Autocosteo,
    }).Valor;
}
