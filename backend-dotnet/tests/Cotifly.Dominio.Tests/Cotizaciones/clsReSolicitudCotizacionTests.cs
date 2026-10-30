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
        Consultar(entregada, EstadoCosteo.Entregada, "cot-1");
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
        Consultar(cotizacion, estado, null);

        cotizacion.ValidarReSolicitud(versionNueva)!.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
    }

    [Theory]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Descartada)]
    public void LaVersionNuevaSePermiteSinUnCosteoEnCurso(EstadoCosteo estado)
    {
        var cotizacion = Enviada();
        Consultar(cotizacion, estado, null);

        cotizacion.ValidarReSolicitud(versionNueva: true).Should().BeNull();
    }

    [Fact]
    public void UnaCotizacionSinFormularioDeCosteoNoSeReSolicita()
    {
        var chat = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-05102026-001" }).Valor;

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
        Consultar(enviada, EstadoCosteo.Entregada, null);
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
        Consultar(cotizacion, EstadoCosteo.Entregada, "cot-1");
        Costear(cotizacion, 2_550_000m);
        cotizacion.PrecioFinal.Should().Be(3_000_000m);

        cotizacion.PrepararReSolicitud();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        cotizacion.SolicitudNexoId.Should().BeNull();
        cotizacion.CotizacionNexoId.Should().BeNull();
        cotizacion.CostoFinal.Should().BeNull();
        cotizacion.PrecioFinal.Should().BeNull();
        var ruta = cotizacion.PrimeraRuta!;
        (ruta.PrecioFinalLinea, ruta.CostoFletesTotal, ruta.EstadoCosteo).Should().Be(((decimal?)null, (decimal?)null, EstadoCosteo.PendienteEnvio));
        ruta.Vehiculos.Should().OnlyContain(v => v.LineaSolicitudNexoId == null && v.CostoFlete == null && v.EstadoCosteo == EstadoCosteo.PendienteEnvio);
        cotizacion.CosteoListoAt.Should().BeNull();
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
        cotizacion.Folio.Should().Be("PCN-900555111-AP-05102026-001");
        cotizacion.Version.Should().Be(1);
    }

    [Fact]
    public void LaVersionNuevaCopiaLaIdentidadYEmpiezaSinCosteo()
    {
        var original = Enviada();
        Consultar(original, EstadoCosteo.Entregada, "cot-1");
        Costear(original, 2_550_000m);
        original.AsignarPdf("PCN.pdf");
        original.RegistrarEnvio();

        var nueva = original.CrearVersion(clsFolio.Desde(original.Folio).Valor.ConVersion(2).Valor, 0m).Valor;

        nueva.Folio.Should().Be($"{original.Folio}-v2");
        nueva.Version.Should().Be(2);
        nueva.NitCliente.Should().Be(original.NitCliente);
        nueva.ClienteNombre.Should().Be(original.ClienteNombre);
        nueva.UsuarioId.Should().Be(original.UsuarioId);
        nueva.Moneda.Should().Be("COP");
        nueva.Rutas.Should().BeEmpty("las rutas las registra el caso de uso (HU-021)");
        nueva.PrecioFinal.Should().BeNull();
        nueva.PdfPath.Should().BeNull();
        nueva.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        nueva.Id.Should().NotBe(original.Id);
    }

    [Fact]
    public void LaVersionNuevaCopiaElMargenNegociadoYRefrescarSoloCambiaElSugerido()
    {
        var original = Enviada();
        Costear(original, 2_550_000m);
        original.AplicarMargenNegociado(1, 0.22m, DateTimeOffset.UnixEpoch);

        var nueva = original.CrearVersion(clsFolio.Desde(original.Folio).Valor.ConVersion(2).Valor, 0m).Valor;
        nueva.RegistrarSolicitudCosteo(Solicitud(), 3);
        nueva.CopiarMargenesNegociadosDe(original);
        var ruta = nueva.PrimeraRuta!;
        ruta.RefrescarMargenSugerido(0.15m, 0.18m, Cotifly.Dominio.Margen.OrigenMargen.SapTm);

        nueva.TieneMargenNegociado.Should().BeTrue();
        ruta.MargenAplicado.Should().Be(0.22m);
        ruta.OrigenMargen.Should().Be(Cotifly.Dominio.Margen.OrigenMargen.Negociado);
        ruta.SugeridoVigente.Should().Be(0.18m);
        ruta.OrigenMargenSugerido.Should().Be(Cotifly.Dominio.Margen.OrigenMargen.SapTm);
        var sinNegociar = Enviada().CrearVersion(clsFolio.Desde("PCN-900555111-AP-05102026-001").Valor.ConVersion(2).Valor, 0m).Valor;
        sinNegociar.RegistrarSolicitudCosteo(Solicitud(), 3);
        sinNegociar.CopiarMargenesNegociadosDe(Enviada());
        sinNegociar.PrimeraRuta!.MargenAplicado.Should().BeNull("sin margen negociado, la versión resuelve el suyo al enviar");
    }

    [Fact]
    public void LaVersionNuevaEnUsdCopiaLaMonedaYTomaLaTrmDelDiaSinTocarLaOriginal()
    {
        var original = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-05102026-001",
            Moneda = "USD",
            TrmAplicada = 3_900m,
        }).Valor;

        var nueva = original.CrearVersion(clsFolio.Desde(original.Folio).Valor.ConVersion(2).Valor, 4_012.35m).Valor;

        nueva.Moneda.Should().Be("USD");
        nueva.TrmAplicada.Should().Be(4_012.35m);
        original.TrmAplicada.Should().Be(3_900m);
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
    public void LasCiudadesDelContenedorLlevanSuIdYUnNombreNuevoSinIdBorraElAnterior()
    {
        // HU-028 (#186), defecto B: datos_carga llevaba las ciudades de devolución y de levante solo como texto.
        var linea = Solicitud().ALinea() with { CiudadDevolucionContenedor = "Buenaventura", CiudadDevolucionMunicipioId = 76109 };
        var conId = new clsCambiosCarga
        {
            Presentes = new HashSet<string>
            {
                clsCambiosCarga.CampoCiudadLevanteContenedor, clsCambiosCarga.CampoCiudadLevanteMunicipioId, clsCambiosCarga.CampoLevanteContenedorDiferente,
            },
            LevanteContenedorDiferente = true,
            CiudadLevanteContenedor = "Cali",
            CiudadLevanteMunicipioId = 76001,
        };
        var soloNombre = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoCiudadDevolucionContenedor },
            CiudadDevolucionContenedor = "Cartagena",
        };

        var nueva = conId.Aplicar(linea);
        var sinId = soloNombre.Aplicar(linea);

        (nueva.CiudadLevanteContenedor, nueva.CiudadLevanteMunicipioId).Should().Be(("Cali", 76001));
        nueva.CiudadDevolucionMunicipioId.Should().Be(76109, "lo que no viene se conserva");
        (sinId.CiudadDevolucionContenedor, sinId.CiudadDevolucionMunicipioId).Should().Be(("Cartagena", (int?)null),
            "el id de Buenaventura ya no corresponde al nombre nuevo");
        clsCambiosCarga.Campos.Should().Contain([clsCambiosCarga.CampoCiudadDevolucionMunicipioId, clsCambiosCarga.CampoCiudadLevanteMunicipioId]);
    }

    [Fact]
    public void LasPosicionesNuevasReemplazanTodasYLasHorasVanALaLineaYAlEncabezado()
    {
        var solicitud = Solicitud();
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPosiciones, clsCambiosCarga.CampoHorasCargue },
            Posiciones = [new clsPosicionCarga("Caja", 2, 100m, 100m, 100m, 50m)],
            HorasCargue = 8m,
        };

        var linea = cambios.Aplicar(solicitud.ALinea());
        linea.Posiciones.Should().ContainSingle().Which.Descripcion.Should().Be("Caja");
        linea.HorasCargue.Should().Be(8m, "HU-021: las horas son de la ruta");
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

    private static clsCotizacionSolicitud Enviada()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-05102026-001",
            NitCliente = "900555111",
            ClienteNombre = "CLIENTE S.A.S.",
            UsuarioId = Guid.NewGuid(),
        }).Valor;
        cotizacion.RegistrarSolicitudCosteo(Solicitud(), 3);
        cotizacion.RegistrarEnvioCosteo("sol-1", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return cotizacion;
    }

    /// <summary>Como el polling (HU-021): el estado llega a cada camión y la solicitud resume a sus rutas.</summary>
    private static void Consultar(clsCotizacionSolicitud cotizacion, EstadoCosteo estado, string? cotizacionNexoId)
    {
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarEstadoCamiones(estado);
        }

        cotizacion.RegistrarConsultaCosteo(estado, cotizacionNexoId, null);
    }

    /// <summary>Margen de la política (15 %) y el flete de Nexo en el camión; la ruta y la solicitud quedan con precio.</summary>
    private static void Costear(clsCotizacionSolicitud cotizacion, decimal flete)
    {
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        ruta.Vehiculos[0].RegistrarCosteo(cotizacion.EstadoCosteo ?? EstadoCosteo.Entregada, flete, null);
        cotizacion.RecalcularTotales(DateTimeOffset.UnixEpoch);
    }
}
