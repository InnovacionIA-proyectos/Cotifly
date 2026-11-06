using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Margen sobre el costeo de Nexo (HU-005.7, ADR-015 §5): el costo de Nexo pasa por la fórmula del módulo de Precio (HU-003.4) con el
/// margen guardado al enviar; nunca se muestra como precio final. HU-021: el costo es del camión y el precio de la ruta; la lectura
/// completa es <see cref="clsLecturaCosteo.AplicarFoto"/> (camiones, estado de la solicitud y precio).
/// </summary>
public sealed class clsPrecioCosteoServiceTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 9, 28, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new();

    [Fact]
    public void ConCosteoCompletoElPrecioSaleDelMargenYNoEsElCostoDeNexo()
    {
        var cotizacion = Cotizacion();

        var aplicado = Aplicar(cotizacion, Foto(2_000_000m, retorno: 550_000m), _ahora);

        aplicado.Should().BeTrue();
        cotizacion.CostoFinal.Should().Be(2_550_000m, "flete + retorno del contenedor de la línea");
        cotizacion.Vehiculos[0].CostoFlete.Should().Be(2_000_000m);
        cotizacion.Vehiculos[0].CostoRetornoContenedor.Should().Be(550_000m);
        cotizacion.PrecioFinal.Should().Be(3_000_000m, "2.550.000 / (1 - 0,15)");
        cotizacion.PrecioFinal.Should().NotBe(cotizacion.CostoFinal);
        cotizacion.CosteoListoAt.Should().Be(_ahora);
    }

    // HU-021 (decisión 3): Cotifly es la fuente de los conexos de la ruta; la lectura del costeo NO importa los conexos de Nexo, salvo
    // los que Cotifly agregó y no pudo leer (marca ConexosPendientesDeLeer). Ya no hay "incluido/excluido": el conexo de la ruta va en
    // su precio. HU-043.2 (#251): a su venta (valorVenta de Nexo), sin margen; sin venta (Nexo anterior), al costo.
    [Fact]
    public void LosConexosDeNexoSoloSeTraenConLaMarcaYVanEnElPrecioASuVentaSinMargen()
    {
        var cotizacion = Cotizacion();
        IReadOnlyList<clsConexoNexo> conexos =
        [
            new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 170_000m) { PrecioVentaUnitario = 250_000m, ValorVenta = 250_000m },
            new clsConexoNexo("CX-2", "Seguro", "adicional", 1m, 85_000m),
        ];
        Aplicar(cotizacion, Foto(2_000_000m, conexos: conexos), _ahora);

        cotizacion.PrimeraRuta!.Conexos.Should().BeEmpty("sin la marca la lectura no trae conexos");
        cotizacion.PrecioFinal.Should().Be(2_352_941m);

        cotizacion.MarcarConexosPendientesDeLeer();
        var pendientes = new clsConexosPendientesNexo(cotizacion.Folio, cotizacion.PrimeraRuta!.Id, []);
        Aplicar(cotizacion, Foto(2_000_000m, conexos: conexos), _ahora.AddHours(1), pendientes).Should().BeTrue();

        cotizacion.ConexosPendientesDeLeer.Should().BeFalse("la lectura quita la marca");
        cotizacion.PrimeraRuta.Conexos.Select(c => (c.IdServicioConexo, c.Precio, c.PrecioVenta, c.Cantidad)).Should().BeEquivalentTo(
            [("CX-1", 170_000m, 250_000m, 1m), ("CX-2", 85_000m, 85_000m, 1m)]);
        clsCosteoCotizacionService.Vista(cotizacion, null).ServiciosConexos.Select(s => (s.Id, s.Precio, s.Incluido))
            .Should().BeEquivalentTo([("CX-1", 250_000m, true), ("CX-2", 85_000m, true)]);
        cotizacion.PrecioFinal.Should().Be(2_687_941m, "2.000.000 / 0,85 = 2.352.941 + 250.000 + 85.000");
        cotizacion.CostoFinal.Should().Be(2_255_000m, "el costo sigue con valorAplicado");
        cotizacion.CosteoListoAt.Should().Be(_ahora, "costeoListoAt es la primera vez que hubo costeo");
    }

    [Fact]
    public void SinCosteoCompletoNoSePonePrecio()
    {
        var cotizacion = Cotizacion();
        var sinFlete = new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir", [], []));

        Aplicar(cotizacion, sinFlete, _ahora).Should().BeFalse();

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.CostoFinal.Should().BeNull();
    }

    [Fact]
    public void SinMargenResueltoNoSeMuestraElCostoComoPrecioYQuedaLaAdvertencia()
    {
        var cotizacion = Cotizacion(conMargen: false);

        // HU-021: la lectura sí cambia el camión (estado y flete), pero la ruta queda sin precio.
        Aplicar(cotizacion, Foto(2_000_000m), _ahora);

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinMargen);
    }

    [Fact]
    public void ConElPdfYaGeneradoElPrecioNoCambiaYSeAdviertePorElCosteoNuevo()
    {
        var cotizacion = Cotizacion();
        Aplicar(cotizacion, Foto(2_000_000m), _ahora);
        cotizacion.AsignarPdf("PCN-X.pdf");
        var precio = cotizacion.PrecioFinal;

        Aplicar(cotizacion, Foto(2_500_000m), _ahora).Should().BeFalse();

        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.Vehiculos[0].CostoFlete.Should().Be(2_000_000m, "con PDF el camión conserva el costo de la versión");
        cotizacion.MotivoEstadoCosteo.Should().Contain("versión nueva");
    }

    [Fact]
    public void LaMismaFotoNoCambiaNada()
    {
        var cotizacion = Cotizacion();
        Aplicar(cotizacion, Foto(2_000_000m), _ahora).Should().BeTrue();

        Aplicar(cotizacion, Foto(2_000_000m), _ahora.AddMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void LasLineasQueNoSonDeLaSolicitudNoSuman()
    {
        var cotizacion = Cotizacion();
        var foto = new clsCosteoConsultadoNexo("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
            [new clsLineaCosteoNexo("L1", 1_700_000m, null, null), new clsLineaCosteoNexo(null, 999_999m, null, null)], []));

        Aplicar(cotizacion, foto, _ahora);

        cotizacion.CostoFinal.Should().Be(1_700_000m);
        cotizacion.PrecioFinal.Should().Be(2_000_000m);
    }

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora, clsConexosPendientesNexo? pendientes = null) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, ahora, new clsOpcionesCosteoNexo(), _servicio, pendientes);

    // ------------------------------------------------------------------ Regresión #135: reimportación de conexos

    [Fact]
    public void ConLaMarcaSoloSeImportanLosConexosNuevosYUnQuitadoEnCotiflyNoVuelve()
    {
        // CX-1 estaba en Nexo antes del POST incierto y el comercial lo quitó en Cotifly (Nexo no tiene DELETE y lo conserva); CX-2 es
        // el que agregó el POST incierto.
        var cotizacion = Cotizacion();
        Aplicar(cotizacion, Foto(2_000_000m), _ahora);
        IReadOnlyList<clsConexoNexo> enNexo = [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 170_000m), new clsConexoNexo("CX-2", "Seguro", "adicional", 1m, 85_000m)];
        cotizacion.MarcarConexosPendientesDeLeer();

        Aplicar(cotizacion, Foto(2_000_000m, conexos: enNexo), _ahora.AddHours(1),
            new clsConexosPendientesNexo(cotizacion.Folio, cotizacion.PrimeraRuta!.Id, ["CX-1"]));

        cotizacion.PrimeraRuta.Conexos.Select(c => c.IdServicioConexo).Should().Equal(["CX-2"], "CX-1 se quitó en Cotifly y no vuelve");
        cotizacion.ConexosPendientesDeLeer.Should().BeFalse();

        // Una segunda lectura (sin marca) no duplica nada.
        Aplicar(cotizacion, Foto(2_000_000m, conexos: enNexo), _ahora.AddHours(2));
        cotizacion.PrimeraRuta.Conexos.Should().ContainSingle();
    }

    [Fact]
    public void LosConexosNuevosVanALaRutaPedidaYLosDeOtraRutaNoSeDuplicanEnLaRuta1()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m]);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 1m, orden: 2);
        var segunda = cotizacion.RutasOrdenadas[1];
        IReadOnlyList<clsConexoNexo> enNexo = [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 85_000m), new clsConexoNexo("CX-9", "Escolta", "adicional", 1m, 42_500m)];
        cotizacion.MarcarConexosPendientesDeLeer();

        Aplicar(cotizacion, clsCotizacionMuestra.Foto([850_000m, 1_700_000m], conexos: enNexo), _ahora.AddHours(1),
            new clsConexosPendientesNexo(cotizacion.Folio, segunda.Id, []));

        cotizacion.RutasOrdenadas[0].Conexos.Should().BeEmpty("nada se duplica en la ruta 1");
        segunda.Conexos.Select(c => c.IdServicioConexo).Should().BeEquivalentTo(["CX-1", "CX-9"]);
    }

    [Fact]
    public void ConLaMarcaPeroSinElRegistroDePendientesNoSeImportaNada()
    {
        var cotizacion = Cotizacion();
        cotizacion.MarcarConexosPendientesDeLeer();

        Aplicar(cotizacion, Foto(2_000_000m, conexos: [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 170_000m)]), _ahora);

        cotizacion.PrimeraRuta!.Conexos.Should().BeEmpty("sin saber qué tenía Nexo antes no se arriesga a reimportar un conexo quitado");
        cotizacion.ConexosPendientesDeLeer.Should().BeFalse();
    }

    /// <summary>Formulario enviado a Nexo (pendiente de costeo) con el margen de la política resuelto en la ruta (o sin margen).</summary>
    private static clsCotizacionSolicitud Cotizacion(bool conMargen = true)
    {
        var cotizacion = clsCotizacionMuestra.Formulario("PCN-AP-28092026-009");
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        if (conMargen)
        {
            cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        return cotizacion;
    }

    private static clsCosteoConsultadoNexo Foto(decimal flete, decimal? retorno = null, IReadOnlyList<clsConexoNexo>? conexos = null) =>
        new("EnCosteo", ["L1"], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir", [new clsLineaCosteoNexo("L1", flete, null, retorno)], conexos ?? []));
}
