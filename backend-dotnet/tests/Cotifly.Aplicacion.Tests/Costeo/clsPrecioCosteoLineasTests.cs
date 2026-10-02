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

    [Theory]
    [InlineData(-1_000)]
    [InlineData(0)]
    public void ConUnaLineaUnCostoInvalidoDeNexoDaElMismoResultadoQueHu0057SinLanzar(int costo)
    {
        var legado = Cotizacion();
        var conLineas = Cotizacion();
        var lineas = Lineas(conLineas, 1);
        var foto = Foto("EnCosteo", [("L1", costo)], [], ["L1"]);

        var a = _servicio.Aplicar(legado, foto, _ahora);
        var aplicar = () => _servicio.Aplicar(conLineas, lineas, foto, _ahora);

        aplicar.Should().NotThrow("un costo inválido de Nexo nunca debe envenenar el polling");
        _servicio.Aplicar(Cotizacion(), Lineas(Cotizacion(), 1), foto, _ahora).Should().Be(a);
        conLineas.PrecioFinal.Should().Be(legado.PrecioFinal).And.BeNull();
        conLineas.MotivoEstadoCosteo.Should().Be(legado.MotivoEstadoCosteo);
        lineas[0].PrecioFinal.Should().BeNull();
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
    public void LasLineasSeAsocianPorContenidoAunqueNexoLasDevuelvaEnOtroOrden()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 3);
        var foto = Foto("EnCosteo", [("L3", 850_000m), ("L1", 1_000_000m), ("L2", 2_000_000m)], [], ["L3", "L1", "L2"]);

        _servicio.Aplicar(cotizacion, lineas, foto, _ahora).Should().BeTrue();

        // Cada precio va con SU ruta, no con la que ocupa la misma posición en la respuesta de Nexo.
        lineas.Select(l => (l.LineaSolicitudNexoId, l.PrecioFinal)).Should().Equal(("L1", (decimal?)1_176_471m), ("L2", 2_352_941m), ("L3", 1_000_000m));
        cotizacion.PrecioFinal.Should().Be(1_176_471m + 2_352_941m + 1_000_000m);
    }

    [Fact]
    public void DosLineasIdenticasNoSeAsocianYLaLicitacionQuedaSinTotal()
    {
        var cotizacion = Cotizacion();
        var lineas = clsCotizacionLineaSolicitud.Desde(cotizacion.Id,
            [new clsLineaSolicitudCosteo { Origen = "Origen 1", Destino = "Destino", PesoKg = 1m }, new clsLineaSolicitudCosteo { Origen = "Origen 1", Destino = "Destino", PesoKg = 1m },
             new clsLineaSolicitudCosteo { Origen = "Origen 3", Destino = "Destino", PesoKg = 1m }]);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 2_000_000m), ("L3", 850_000m)], [], ["L1", "L3"]) with { };
        foto = foto with
        {
            LineasSolicitud = ["L1", "L2", "L3"],
            Detalle = [new clsLineaSolicitudNexo("L1", "ORIGEN 1 → DESTINO", null, 1m, null, null), new clsLineaSolicitudNexo("L2", "ORIGEN 1 → DESTINO", null, 1m, null, null),
                       new clsLineaSolicitudNexo("L3", "ORIGEN 3 → DESTINO", null, 1m, null, null)],
        };

        _servicio.Aplicar(cotizacion, lineas, foto, _ahora);

        lineas[0].LineaSolicitudNexoId.Should().BeNull("dos rutas idénticas con fletes distintos: no se adivina cuál es cuál");
        lineas[1].LineaSolicitudNexoId.Should().BeNull();
        lineas[2].LineaSolicitudNexoId.Should().Be("L3");
        lineas[2].PrecioFinal.Should().Be(1_000_000m, "la línea inequívoca sí lleva su precio");
        cotizacion.PrecioFinal.Should().BeNull("el total solo existe con todas las líneas asociadas y con precio");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinAsociar(2, 3));
    }

    [Fact]
    public void SiNexoTraeOtroNumeroDeLineasSoloSeAsocianLasQueCoinciden()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 3);

        _servicio.Aplicar(cotizacion, lineas, Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 1_000_000m)], [], ["L2", "L1"]), _ahora);

        lineas.Select(l => l.LineaSolicitudNexoId).Should().Equal("L1", "L2", null);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinAsociar(1, 3));
    }

    [Fact]
    public void UnCostoNegativoDeNexoNoRompeElPollingYSeAdvierte()
    {
        var cotizacion = Cotizacion();
        var lineas = Lineas(cotizacion, 2);

        var aplicar = () => _servicio.Aplicar(cotizacion, lineas, Foto("EnCosteo", [("L1", -5m), ("L2", 1_000_000m)], [], ["L1", "L2"]), _ahora);

        aplicar.Should().NotThrow();
        lineas[0].PrecioFinal.Should().BeNull();
        lineas[1].PrecioFinal.Should().Be(1_176_471m);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Contain("costo negativo");
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

    /// <summary>Líneas con rutas distintas: la línea k va de <c>Origen k</c> a <c>Destino</c> (en Nexo, la <c>Lk</c>).</summary>
    private static IReadOnlyList<clsCotizacionLineaSolicitud> Lineas(clsCotizacion cotizacion, int numero) =>
        clsCotizacionLineaSolicitud.Desde(cotizacion.Id,
            [.. Enumerable.Range(1, numero).Select(k => new clsLineaSolicitudCosteo { Origen = $"Origen {k}", Destino = "Destino", PesoKg = 1m })]);

    /// <summary>
    /// Foto de Nexo: <paramref name="ids"/> en el orden en que Nexo los devuelve (cualquiera) y, por cada <c>Lk</c>, su contenido
    /// (<c>ORIGEN K → DESTINO</c>, como lo normaliza Nexo, en mayúsculas y sin tildes).
    /// </summary>
    private static clsCosteoConsultadoNexo Foto(
        string estadoSolicitud, IReadOnlyList<(string Id, decimal Flete)> fletes, IReadOnlyList<clsConexoNexo> conexos, IReadOnlyList<string> ids) =>
        new(estadoSolicitud, ids, new clsCotizacionNexo("COT-1", estadoSolicitud == "Entregada" ? "Entregada" : "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], conexos))
        {
            Detalle = [.. ids.Select(id => new clsLineaSolicitudNexo(id, $"ORIGEN {id[1..]} → DESTINO", null, 1m, null, null))],
        };
}
