using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Invariantes de "Cotizar costo" (HU-013.4, ADR-018 §5 y §11): estados que permiten re-solicitar en el mismo folio o crear la
/// versión nueva, regla del PDF, limpieza del costeo anterior, copia de la identidad en la versión <c>-vN</c> y cambios de carga.
/// </summary>
public sealed class clsReSolicitudCotizacionTests
{
    [Fact]
    public void ConElCosteoVencidoSePermiteEnElMismoFolioYEnVersionNueva()
    {
        var cotizacion = Enviada();
        cotizacion.VencerPollingCosteo("72 h");

        cotizacion.ValidarReSolicitud(versionNueva: false).Should().BeNull();
        cotizacion.ValidarReSolicitud(versionNueva: true).Should().BeNull();
    }

    [Fact]
    public void EntregadaOConErrorDeEnvioSePermiteEnElMismoFolio()
    {
        var entregada = Enviada();
        entregada.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "cot-1", DateTimeOffset.UtcNow, null);
        var conError = Enviada();
        conError.RegistrarErrorEnvioCosteo("Nexo no respondió (HTTP 503)");

        entregada.ValidarReSolicitud(false).Should().BeNull();
        conError.ValidarReSolicitud(false).Should().BeNull();
    }

    [Theory]
    [InlineData(EstadoCosteo.PendienteCosteo, false)]
    [InlineData(EstadoCosteo.PendienteCosteo, true)]
    [InlineData(EstadoCosteo.Autocosteo, false)]
    [InlineData(EstadoCosteo.Descartada, false)]
    public void EnOtrosEstadosNoSePermite(EstadoCosteo estado, bool versionNueva)
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarConsultaCosteo(estado, null, DateTimeOffset.UtcNow, null);

        cotizacion.ValidarReSolicitud(versionNueva)!.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
    }

    [Theory]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Descartada)]
    public void LaVersionNuevaSePermiteSinUnCosteoEnCurso(EstadoCosteo estado)
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarConsultaCosteo(estado, null, DateTimeOffset.UtcNow, null);

        cotizacion.ValidarReSolicitud(versionNueva: true).Should().BeNull();
    }

    [Fact]
    public void UnaCotizacionSinFormularioDeCosteoNoSeReSolicita()
    {
        var chat = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-05102026-001" }).Valor;

        chat.ValidarReSolicitud(true)!.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
    }

    [Fact]
    public void UnRechazoDefinitivoDeNexoSoloSeCorrigeEnUnaVersionNueva()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarErrorEnvioCosteo("VALIDATION_ERROR", definitivo: true);

        cotizacion.ValidarReSolicitud(false)!.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        cotizacion.ValidarReSolicitud(true).Should().BeNull();
    }

    [Fact]
    public void YaEnviadaOConResultadoElMismoFolioEsPdfYaEnviadoPeroLaVersionNuevaSePermite()
    {
        var enviada = Enviada();
        enviada.RegistrarConsultaCosteo(EstadoCosteo.Entregada, null, DateTimeOffset.UtcNow, null);
        enviada.RegistrarEnvio();
        var conResultado = Enviada();
        conResultado.VencerPollingCosteo("72 h");
        conResultado.MarcarSeguimiento(false, "Precio alto", DateTimeOffset.UtcNow);

        enviada.ValidarReSolicitud(false)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        conResultado.ValidarReSolicitud(false)!.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        enviada.ValidarReSolicitud(true).Should().BeNull();
        conResultado.ValidarReSolicitud(true).Should().BeNull();
    }

    [Fact]
    public void PrepararLaReSolicitudOlvidaElCosteoAnteriorYLaDejaPendienteDeEnvio()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "cot-1", DateTimeOffset.UtcNow, null);
        cotizacion.RegistrarPrecioCosteo(2_550_000m, 3_000_000m, "[]", DateTimeOffset.UtcNow);

        cotizacion.PrepararReSolicitud();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        cotizacion.CosteoId.Should().BeNull();
        cotizacion.CotizacionNexoId.Should().BeNull();
        cotizacion.Costo.Should().BeNull();
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.ServiciosConexos.Should().BeNull();
        cotizacion.CosteoListoAt.Should().BeNull();
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        cotizacion.Folio.Should().Be("PCN-900555111-AP-05102026-001");
        cotizacion.Version.Should().Be(1);
    }

    [Fact]
    public void LaVersionNuevaCopiaLaIdentidadYEmpiezaSinCosteo()
    {
        var original = Enviada();
        original.RegistrarConsultaCosteo(EstadoCosteo.Entregada, "cot-1", DateTimeOffset.UtcNow, null);
        original.RegistrarPrecioCosteo(2_550_000m, 3_000_000m, null, DateTimeOffset.UtcNow);
        original.AsignarPdf("PCN.pdf");
        original.RegistrarEnvio();

        var nueva = original.CrearVersion(clsFolio.Desde(original.Folio).Valor.ConVersion(2).Valor).Valor;

        nueva.Folio.Should().Be($"{original.Folio}-v2");
        nueva.Version.Should().Be(2);
        nueva.NitCliente.Should().Be(original.NitCliente);
        nueva.ClienteNombre.Should().Be(original.ClienteNombre);
        nueva.LineaNegocioId.Should().Be(original.LineaNegocioId);
        nueva.UsuarioId.Should().Be(original.UsuarioId);
        nueva.Moneda.Should().Be("COP");
        nueva.Costo.Should().BeNull();
        nueva.PdfPath.Should().BeNull();
        nueva.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        nueva.Id.Should().NotBe(original.Id);
    }

    [Fact]
    public void LosCambiosDeCargaSoloTocanLosCamposPresentesYUnNuloBorra()
    {
        var linea = Solicitud().ALinea();
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg, clsCambiosCarga.CampoClaseContenedor },
            PesoKg = 28_400m,
            ClaseContenedor = null,
            TipoVehiculo = "ignorado: no está presente",
        };

        var nueva = cambios.Aplicar(linea);

        nueva.PesoKg.Should().Be(28_400m);
        nueva.ClaseContenedor.Should().BeNull("un campo presente en null se borra");
        nueva.TipoVehiculo.Should().Be("3S3", "un campo ausente se conserva");
        nueva.Origen.Should().Be(linea.Origen);
        nueva.Posiciones.Should().BeEquivalentTo(linea.Posiciones);
    }

    [Fact]
    public void LasPosicionesNuevasReemplazanTodasYLasHorasVanAlEncabezado()
    {
        var solicitud = Solicitud();
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPosiciones, clsCambiosCarga.CampoHorasCargue },
            Posiciones = [new clsPosicionCarga("Caja", 2, 100m, 100m, 100m, 50m)],
            HorasCargue = 8m,
        };

        cambios.Aplicar(solicitud.ALinea()).Posiciones.Should().ContainSingle().Which.Descripcion.Should().Be("Caja");
        var encabezado = cambios.Aplicar(solicitud.Encabezado);
        encabezado.HorasCargue.Should().Be(8m);
        encabezado.HorasDescargue.Should().Be(solicitud.HorasDescargue);
    }

    private static clsSolicitudCosteo Solicitud() => new()
    {
        Tipo = TipoSolicitud.Spot,
        Origen = "Buenaventura",
        Destino = "Bogotá D.C.",
        TipoVehiculo = "3S3",
        ClaseViaje = "Contenedor",
        ClaseContenedor = "CNT40.HC",
        HorasCargue = 4m,
        HorasDescargue = 6m,
        PesoKg = 28_500m,
        Posiciones = [new clsPosicionCarga("Estiba", 20, 120m, 100m, 150m, 1425m)],
    };

    private static clsCotizacion Enviada()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-05102026-001",
            NitCliente = "900555111",
            ClienteNombre = "CLIENTE S.A.S.",
            LineaNegocioId = 3,
            UsuarioId = Guid.NewGuid(),
        }).Valor;
        cotizacion.RegistrarSolicitudCosteo(Solicitud());
        cotizacion.RegistrarEnvioCosteo("sol-1", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return cotizacion;
    }
}
