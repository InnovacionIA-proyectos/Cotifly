using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

public sealed class clsCotizacionTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnaCotizacionNuevaNaceEnVersionUnoEnPesosSinEnviar()
    {
        var resultado = clsCotizacion.Crear(new clsDatosCotizacion { Folio = " PCN-DG-24092026-001 " });

        resultado.EsExitoso.Should().BeTrue();
        var cotizacion = resultado.Valor;
        cotizacion.Id.Should().NotBeEmpty();
        cotizacion.Folio.Should().Be("PCN-DG-24092026-001");
        cotizacion.Version.Should().Be(1);
        cotizacion.Moneda.Should().Be("COP");
        cotizacion.TrmAplicada.Should().Be(0m);
        cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ElFolioEsObligatorio(string folio) =>
        Crear(new clsDatosCotizacion { Folio = folio }).Mensaje.Should().Contain("folio");

    [Fact]
    public void ElFolioNoSuperaCincuentaCaracteres() =>
        Crear(new clsDatosCotizacion { Folio = new string('F', 51) }).Mensaje.Should().Contain("50");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LaVersionDebeSerAlMenosUno(int version) =>
        Crear(new clsDatosCotizacion { Folio = "F", Version = version }).Mensaje.Should().Contain("versión");

    [Fact]
    public void UnaNuevaVersionConservaSuNumero() =>
        clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-DG-24092026-001-v2", Version = 2 }).Valor.Version.Should().Be(2);

    [Fact]
    public void LaTrmDebeSerCeroCuandoLaMonedaEsCop() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "COP", TrmAplicada = 4000m }).Mensaje.Should().Contain("TRM");

    [Fact]
    public void LaTrmDebeSerPositivaEnOtraMoneda() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "usd", TrmAplicada = 0m }).Mensaje.Should().Contain("USD");

    [Fact]
    public void EnDolaresConTrmLaMonedaSeNormaliza()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "F", Moneda = "usd", TrmAplicada = 4100.5m }).Valor;

        cotizacion.Moneda.Should().Be("USD");
        cotizacion.TrmAplicada.Should().Be(4100.5m);
    }

    [Fact]
    public void LaMonedaDebeSerUnCodigoIsoDeTresLetras() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "PESOS" }).Mensaje.Should().Contain("ISO");

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void LosMargenesDebenEstarEntreCeroYUno(double margen)
    {
        Crear(new clsDatosCotizacion { Folio = "F", MargenAplicado = (decimal)margen }).Mensaje.Should().Contain("margenAplicado");
        Crear(new clsDatosCotizacion { Folio = "F", MargenPolitica = (decimal)margen }).Mensaje.Should().Contain("margenPolitica");
    }

    [Theory]
    [InlineData("margen")]
    [InlineData(null)]
    public void ConMetodoMargenLosMargenesDebenSerMenoresQueUno(string? metodo) =>
        Crear(new clsDatosCotizacion { Folio = "F", MetodoCalculo = metodo, MargenAplicado = 1m }).Mensaje.Should().Contain("margen");

    [Fact]
    public void ConMetodoMarkupLosMargenesPuedenSerMayoresOIgualesAUno()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "F",
            MetodoCalculo = "markup",
            MargenPolitica = 1m,
            MargenAplicado = 1.35m,
        });

        cotizacion.EsExitoso.Should().BeTrue();
        cotizacion.Valor.MargenAplicado.Should().Be(1.35m);
    }

    [Fact]
    public void ConMetodoMarkupLosMargenesNoPuedenSerNegativos() =>
        Crear(new clsDatosCotizacion { Folio = "F", MetodoCalculo = "markup", MargenPolitica = -0.01m }).Mensaje.Should().Contain("negativo");

    [Fact]
    public void ElCostoYElPrecioNoPuedenSerNegativos()
    {
        Crear(new clsDatosCotizacion { Folio = "F", Costo = -1m }).Mensaje.Should().Contain("costo");
        Crear(new clsDatosCotizacion { Folio = "F", PrecioFinal = -1m }).Mensaje.Should().Contain("precioFinal");
    }

    [Fact]
    public void LosErroresUsanElCodigoCotizacionInvalida() =>
        clsCotizacion.Crear(new clsDatosCotizacion { Folio = "" }).Error.Codigo.Should().Be(clsCotizacion.CodigoError);

    [Fact]
    public void ElPrimerEnvioLaDejaEnviadaYLosSiguientesReenviada()
    {
        var cotizacion = Nueva();

        cotizacion.RegistrarEnvio();
        cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Enviada);

        cotizacion.RegistrarEnvio();
        cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada);

        cotizacion.RegistrarEnvio();
        cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada);
    }

    [Fact]
    public void MarcarSeguimientoGuardaResultadoMotivoYMomento()
    {
        var cotizacion = Nueva();

        cotizacion.MarcarSeguimiento(false, "  Precio alto  ", _ahora);

        cotizacion.Ganada.Should().BeFalse();
        cotizacion.MotivoRechazo.Should().Be("Precio alto");
        cotizacion.MarcadoAt.Should().Be(_ahora);
    }

    [Fact]
    public void EsDeSoloEsVerdaderoParaSuDueno()
    {
        var dueno = Guid.NewGuid();
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "F", UsuarioId = dueno }).Valor;

        cotizacion.EsDe(dueno).Should().BeTrue();
        cotizacion.EsDe(Guid.NewGuid()).Should().BeFalse();
        Nueva().EsDe(dueno).Should().BeFalse("una cotización sin dueño no es de nadie");
    }

    [Fact]
    public void AsignarPdfExigeRuta()
    {
        var cotizacion = Nueva();

        cotizacion.AsignarPdf(" pdfs/PCN-1.pdf ");
        cotizacion.PdfPath.Should().Be("pdfs/PCN-1.pdf");
        FluentActions.Invoking(() => cotizacion.AsignarPdf(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void LosTextosOpcionalesVaciosQuedanNulos()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "F", NitCliente = "  ", ClienteNombre = "" }).Valor;

        cotizacion.NitCliente.Should().BeNull();
        cotizacion.ClienteNombre.Should().BeNull();
    }

    private static clsCotizacion Nueva() => clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-1" }).Valor;

    private static Dominio.Comun.clsErrorDominio Crear(clsDatosCotizacion datos)
    {
        var resultado = clsCotizacion.Crear(datos);
        resultado.EsFallo.Should().BeTrue();
        return resultado.Error;
    }
}
