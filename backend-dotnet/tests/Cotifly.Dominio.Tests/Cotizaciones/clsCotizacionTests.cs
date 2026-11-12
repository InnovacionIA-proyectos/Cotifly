using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Tests.Cotizaciones;

public sealed class clsCotizacionTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnaCotizacionNuevaNaceEnVersionUnoEnPesosSinEnviar()
    {
        var resultado = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = " PCN-DG-24092026-001 " });

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
        clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-DG-24092026-001-v2", Version = 2 }).Valor.Version.Should().Be(2);

    [Fact]
    public void LaTrmDebeSerCeroCuandoLaMonedaEsCop() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "COP", TrmAplicada = 4000m }).Mensaje.Should().Contain("TRM");

    [Fact]
    public void LaTrmDebeSerPositivaEnOtraMoneda() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "usd", TrmAplicada = 0m }).Mensaje.Should().Contain("USD");

    [Fact]
    public void EnDolaresConTrmLaMonedaSeNormaliza()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "F", Moneda = "usd", TrmAplicada = 4100.5m }).Valor;

        cotizacion.Moneda.Should().Be("USD");
        cotizacion.TrmAplicada.Should().Be(4100.5m);
    }

    [Fact]
    public void LaMonedaDebeSerUnCodigoIsoDeTresLetras() =>
        Crear(new clsDatosCotizacion { Folio = "F", Moneda = "PESOS" }).Mensaje.Should().Contain("ISO");

    // El margen, el costo y el precio son de la ruta; en la cotización del chat se validan al registrar su ruta.
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void LosMargenesDebenEstarEntreCeroYUno(double margen)
    {
        clsCotizacionSolicitud.ValidarCotizacionChat(null, null, (decimal)margen, null, null)!.Mensaje.Should().Contain("margenAplicado");
        clsCotizacionSolicitud.ValidarCotizacionChat(null, (decimal)margen, null, null, null)!.Mensaje.Should().Contain("margenPolitica");
    }

    [Theory]
    [InlineData("margen")]
    [InlineData(null)]
    public void ConMetodoMargenLosMargenesDebenSerMenoresQueUno(string? metodo) =>
        clsCotizacionSolicitud.ValidarCotizacionChat(null, null, 1m, metodo, null)!.Mensaje.Should().Contain("margen");

    [Fact]
    public void ConMetodoMarkupLosMargenesPuedenSerMayoresOIgualesAUno()
    {
        clsCotizacionSolicitud.ValidarCotizacionChat(1_000m, 1m, 1.35m, "markup", 2_350m).Should().BeNull();

        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "F" }).Valor;
        cotizacion.RegistrarCotizacionChat(3, 1_000m, 1m, 1.35m, OrigenMargen.PoliticaLinea, "markup", 2_350m);

        cotizacion.PrimeraRuta!.MargenAplicado.Should().Be(1.35m);
    }

    [Fact]
    public void ConMetodoMarkupLosMargenesNoPuedenSerNegativos() =>
        clsCotizacionSolicitud.ValidarCotizacionChat(null, -0.01m, null, "markup", null)!.Mensaje.Should().Contain("negativo");

    [Fact]
    public void ElCostoYElPrecioNoPuedenSerNegativos()
    {
        clsCotizacionSolicitud.ValidarCotizacionChat(-1m, null, null, null, null)!.Mensaje.Should().Contain("costo");
        clsCotizacionSolicitud.ValidarCotizacionChat(null, null, null, null, -1m)!.Mensaje.Should().Contain("precioFinal");
    }

    [Fact]
    public void LosErroresUsanElCodigoCotizacionInvalida() =>
        clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "" }).Error.Codigo.Should().Be(clsCotizacionSolicitud.CodigoError);

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mejor precio")]
    public void MarcarGanadaDejaElMotivoDeRechazoEnNoAplica(string? motivo)
    {
        // Una ganada no deja motivoRechazo en null.
        var cotizacion = Nueva();

        cotizacion.MarcarSeguimiento(true, motivo, _ahora);

        cotizacion.Ganada.Should().BeTrue();
        cotizacion.MotivoRechazo.Should().Be(clsCotizacionSolicitud.MotivoRechazoNoAplica).And.Be("No aplica");
        cotizacion.MarcadoAt.Should().Be(_ahora);
    }

    [Fact]
    public void ElMotivoDeRechazoSoloQuedaVacioSinResultado()
    {
        var cotizacion = Nueva();
        cotizacion.MotivoRechazo.Should().BeNull("una cotización nueva no tiene resultado");

        cotizacion.MarcarSeguimiento(true, null, _ahora);
        cotizacion.MarcarSeguimiento(null, "Precio alto", _ahora);

        cotizacion.Ganada.Should().BeNull();
        cotizacion.MotivoRechazo.Should().BeNull();
    }

    [Fact]
    public void PasarDeGanadaAPerdidaGuardaElMotivoDelComercial()
    {
        var cotizacion = Nueva();
        cotizacion.MarcarSeguimiento(true, null, _ahora);

        cotizacion.MarcarSeguimiento(false, "Precio alto", _ahora);

        cotizacion.MotivoRechazo.Should().Be("Precio alto");
    }

    [Fact]
    public void EsDeSoloEsVerdaderoParaSuDueno()
    {
        var dueno = Guid.NewGuid();
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "F", UsuarioId = dueno }).Valor;

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
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "F", NitCliente = "  ", ClienteNombre = "" }).Valor;

        cotizacion.NitCliente.Should().BeNull();
        cotizacion.ClienteNombre.Should().BeNull();
    }

    private static clsCotizacionSolicitud Nueva() => clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-1" }).Valor;

    private static Dominio.Comun.clsErrorDominio Crear(clsDatosCotizacion datos)
    {
        var resultado = clsCotizacionSolicitud.Crear(datos);
        resultado.EsFallo.Should().BeTrue();
        return resultado.Error;
    }
}
