using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Margen y precio línea por línea (HU-011.2, ADR-017): cada línea recibe su id de Nexo por orden, su estado y su precio en cuanto
/// tiene flete; la cotización suma los precios solo con el costeo completo. Con una línea el resultado es idéntico al de HU-005.7.
/// </summary>
public sealed class clsPrecioCosteoLineasTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new(new clsPrecioService(NSubstitute.Substitute.For<Cotifly.Aplicacion.Precio.Interfaces.IClsProveedorTrm>(), NSubstitute.Substitute.For<Cotifly.Aplicacion.Comun.Interfaces.IClsCalendarioNegocio>()));

    [Fact]
    public void ConUnaLineaElResultadoEsElMismoQueElDeHu0057()
    {
        var legado = Cotizacion();
        var conLineas = Cotizacion();
        var lineas = Lineas(conLineas, 1);
        var foto = Foto(estadoSolicitud: "EnCosteo", fletes: [("L1", 2_000_000m)], conexos: [new clsConexoNexo("CX", "Cargue", "logistico", 1m, 170_000m)], ids: ["L1"]);

        var a = _servicio.Aplicar(legado, foto, _ahora);
        var b = _servicio.Aplicar(conLineas, lineas, foto, _ahora);

        b.Should().Be(a);
        conLineas.Costo.Should().Be(legado.Costo);
        conLineas.PrecioFinal.Should().Be(legado.PrecioFinal).And.Be(2_352_941m);
        conLineas.ServiciosConexos.Should().Be(legado.ServiciosConexos);
        lineas[0].LineaSolicitudNexoId.Should().Be("L1");
        lineas[0].PrecioFinal.Should().Be(conLineas.PrecioFinal);
        _servicio.Aplicar(conLineas, lineas, foto, _ahora.AddMinutes(1)).Should().Be(_servicio.Aplicar(legado, foto, _ahora.AddMinutes(1)), "la misma foto no cambia nada en ninguno de los dos");
    }

    [Fact]
    public void LasLineasCosteadasTienenPrecioAunqueLaLicitacionSigaPendiente()
    {
        var cotizacion = Cotizacion(EstadoCosteo.PendienteCosteo);
        var lineas = Lineas(cotizacion, 3);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L3", 850_000m)], [], ["L1", "L2", "L3"]);

        _servicio.Aplicar(cotizacion, lineas, foto, _ahora).Should().BeTrue();

        lineas.Select(l => (l.LineaSolicitudNexoId, l.EstadoCosteo, l.PrecioFinal)).Should().Equal(
            ("L1", EstadoCosteo.Autocosteo, 1_176_471m), ("L2", EstadoCosteo.PendienteCosteo, null), ("L3", EstadoCosteo.Autocosteo, 1_000_000m));
        cotizacion.PrecioFinal.Should().BeNull("la licitación no tiene precio total hasta que todas las líneas tienen flete");
        cotizacion.Costo.Should().BeNull();
    }

    [Fact]
    public void ConTodasLasLineasCosteadasLaLicitacionSumaLosPreciosDeSusLineas()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 3);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 2_000_000m), ("L3", 850_000m)], [], ["L1", "L2", "L3"]);

        _servicio.Aplicar(cotizacion, lineas, foto, _ahora).Should().BeTrue();

        cotizacion.Costo.Should().Be(3_850_000m);
        cotizacion.PrecioFinal.Should().Be(lineas.Sum(l => l.PrecioFinal!.Value)).And.Be(1_176_471m + 2_352_941m + 1_000_000m);
        _servicio.Aplicar(cotizacion, lineas, foto, _ahora.AddMinutes(1)).Should().BeFalse("la misma foto no cambia nada");
    }

    [Fact]
    public void LosIdsDeNexoNoSeAdivinanSiElNumeroDeLineasNoCuadra()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 3);

        _servicio.Aplicar(cotizacion, lineas, Foto("EnCosteo", [("L1", 1m)], [], ["L1", "L2"]), _ahora).Should().BeFalse();

        lineas.Should().OnlyContain(l => l.LineaSolicitudNexoId == null && l.PrecioFinal == null);
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoLineasDescuadradas);
    }

    [Fact]
    public void UnaEntregaSinFleteParaAlgunaLineaNoDaPrecioTotal()
    {
        var cotizacion = Cotizacion(EstadoCosteo.Entregada);
        var lineas = Lineas(cotizacion, 2);
        var foto = Foto("Entregada", [("L1", 1_000_000m)], [], ["L1", "L2"]);

        _servicio.Aplicar(cotizacion, lineas, foto, _ahora);

        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Contain("1 de 2 líneas");
        lineas[1].EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        lineas[1].PrecioFinal.Should().BeNull();
    }

    [Fact]
    public void ConPdfLasLineasConservanSuPrecioYSoloCambianDeEstado()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 2);
        _servicio.Aplicar(cotizacion, lineas, Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 1_000_000m)], [], ["L1", "L2"]), _ahora);
        cotizacion.AsignarPdf("PCN-X.pdf");
        var precio = cotizacion.PrecioFinal;

        _servicio.Aplicar(cotizacion, lineas, Foto("Entregada", [("L1", 5_000_000m), ("L2", 1_000_000m)], [], ["L1", "L2"]), _ahora);

        cotizacion.PrecioFinal.Should().Be(precio);
        lineas[0].PrecioFinal.Should().Be(1_176_471m);
        lineas[0].EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.MotivoEstadoCosteo.Should().Contain("versión nueva");
    }

    private static clsCotizacion Cotizacion(EstadoCosteo estado = EstadoCosteo.Autocosteo)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-AP-01102026-001" }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        cotizacion.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.RegistrarConsultaCosteo(estado, "COT-1", _ahora, _ahora);
        return cotizacion;
    }

    private static IReadOnlyList<clsCotizacionLineaSolicitud> Lineas(clsCotizacion cotizacion, int numero) =>
        clsCotizacionLineaSolicitud.Desde(cotizacion.Id,
            [.. Enumerable.Range(0, numero).Select(_ => new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", PesoKg = 1m })]);

    private static clsCosteoConsultadoNexo Foto(
        string estadoSolicitud, IReadOnlyList<(string Id, decimal Flete)> fletes, IReadOnlyList<clsConexoNexo> conexos, IReadOnlyList<string> ids) =>
        new(estadoSolicitud, ids, new clsCotizacionNexo("COT-1", estadoSolicitud == "Entregada" ? "Entregada" : "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], conexos));
}
