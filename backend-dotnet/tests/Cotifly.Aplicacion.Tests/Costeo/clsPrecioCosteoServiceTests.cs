using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Margen sobre el costeo de Nexo (HU-005.7, ADR-015 §5): el costo de Nexo pasa por el módulo de Precio (HU-003.4) con el margen
/// guardado al enviar; nunca se muestra como precio final.
/// </summary>
public sealed class clsPrecioCosteoServiceTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new(new clsPrecioService());

    [Fact]
    public void ConCosteoCompletoElPrecioSaleDelMargenYNoEsElCostoDeNexo()
    {
        var cotizacion = Cotizacion();

        var aplicado = _servicio.Aplicar(cotizacion, Foto(2_000_000m, retorno: 550_000m), _ahora);

        aplicado.Should().BeTrue();
        cotizacion.Costo.Should().Be(2_550_000m, "flete + retorno del contenedor de la línea");
        cotizacion.PrecioFinal.Should().Be(3_000_000m, "2.550.000 / (1 - 0,15)");
        cotizacion.PrecioFinal.Should().NotBe(cotizacion.Costo);
        cotizacion.CosteoListoAt.Should().Be(_ahora);
    }

    [Fact]
    public void LosServiciosConexosLlevanSuPrecioConMargenIncluidosPorDefectoYConservanLaSeleccion()
    {
        var cotizacion = Cotizacion();
        _servicio.Aplicar(cotizacion, Foto(2_000_000m, conexos: [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 170_000m), new clsConexoNexo("CX-2", "Seguro", "adicional", 1m, 85_000m)]), _ahora);

        var servicios = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos);
        servicios.Select(s => (s.Id, s.Precio, s.Incluido)).Should().Equal(("CX-1", 200_000m, true), ("CX-2", 100_000m, true));

        cotizacion.SeleccionarServiciosConexos(clsServiciosConexosJson.Escribir([servicios[0], servicios[1] with { Incluido = false }]));
        _servicio.Aplicar(cotizacion, Foto(2_100_000m, conexos: [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 170_000m), new clsConexoNexo("CX-2", "Seguro", "adicional", 1m, 85_000m)]), _ahora.AddHours(1));

        clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Select(s => s.Incluido).Should().Equal(true, false);
        cotizacion.CosteoListoAt.Should().Be(_ahora, "costeoListoAt es la primera vez que hubo costeo");
    }

    [Fact]
    public void SinCosteoCompletoNoSePonePrecio()
    {
        var cotizacion = Cotizacion(EstadoCosteo.PendienteCosteo);

        _servicio.Aplicar(cotizacion, Foto(2_000_000m), _ahora).Should().BeFalse();

        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.Costo.Should().BeNull();
    }

    [Fact]
    public void SinMargenResueltoNoSeMuestraElCostoComoPrecioYQuedaLaAdvertencia()
    {
        var cotizacion = Cotizacion(conMargen: false);

        _servicio.Aplicar(cotizacion, Foto(2_000_000m), _ahora).Should().BeFalse();

        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinMargen);
    }

    [Fact]
    public void ConElPdfYaGeneradoElPrecioNoCambiaYSeAdviertePorElCosteoNuevo()
    {
        var cotizacion = Cotizacion();
        _servicio.Aplicar(cotizacion, Foto(2_000_000m), _ahora);
        cotizacion.AsignarPdf("PCN-X.pdf");
        var precio = cotizacion.PrecioFinal;

        _servicio.Aplicar(cotizacion, Foto(2_500_000m), _ahora).Should().BeFalse();

        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.MotivoEstadoCosteo.Should().Contain("versión nueva");
    }

    [Fact]
    public void LaMismaFotoNoCambiaNada()
    {
        var cotizacion = Cotizacion();
        _servicio.Aplicar(cotizacion, Foto(2_000_000m), _ahora).Should().BeTrue();

        _servicio.Aplicar(cotizacion, Foto(2_000_000m), _ahora.AddMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void LasLineasQueNoSonDeLaSolicitudNoSuman()
    {
        var cotizacion = Cotizacion();
        var foto = new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
            [new clsLineaCosteoNexo("L1", 1_700_000m, null, null), new clsLineaCosteoNexo(null, 999_999m, null, null)], []));

        _servicio.Aplicar(cotizacion, foto, _ahora);

        cotizacion.Costo.Should().Be(1_700_000m);
        cotizacion.PrecioFinal.Should().Be(2_000_000m);
    }

    private static clsCotizacion Cotizacion(EstadoCosteo estado = EstadoCosteo.Autocosteo, bool conMargen = true)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-28092026-009" }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        if (conMargen)
        {
            cotizacion.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        cotizacion.RegistrarConsultaCosteo(estado, "COT-1", _ahora, _ahora);
        return cotizacion;
    }

    private static clsCosteoConsultadoNexo Foto(decimal flete, decimal? retorno = null, IReadOnlyList<clsConexoNexo>? conexos = null) =>
        new("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir", [new clsLineaCosteoNexo("L1", flete, null, retorno)], conexos ?? []));
}
