using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Margen y precio ruta por ruta (HU-011.2, ADR-017; HU-021, ADR-019): cada camión recibe su línea de Nexo por contenido, su estado y su
/// flete; cada ruta tiene precio en cuanto todos sus camiones tienen flete, y la solicitud suma los precios de sus rutas solo cuando todas
/// lo tienen. Con una ruta el resultado es idéntico al del formulario (HU-005.7).
/// </summary>
public sealed class clsPrecioCosteoLineasTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new();

    [Fact]
    public void ConUnaRutaElResultadoEsElMismoQueElDelFormulario()
    {
        // HU-021: el "legado" (columnas de la cabecera) ya no existe; el formulario y la licitación de una ruta comparten el agregado.
        var formulario = Formulario();
        var licitacion = Cotizacion(1);
        var fotoFormulario = Foto("EnCosteo", [("L1", 2_000_000m)], ["L1"], detalle: false);
        var foto = Foto("EnCosteo", [("L1", 2_000_000m)], ["L1"]);

        var a = Aplicar(formulario, fotoFormulario, _ahora);
        var b = Aplicar(licitacion, foto, _ahora);

        b.Should().Be(a);
        licitacion.CostoFinal.Should().Be(formulario.CostoFinal);
        licitacion.PrecioFinal.Should().Be(formulario.PrecioFinal).And.Be(2_352_941m);
        licitacion.Vehiculos[0].LineaSolicitudNexoId.Should().Be("L1");
        licitacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(licitacion.PrecioFinal);
        Aplicar(licitacion, foto, _ahora.AddMinutes(1)).Should().Be(Aplicar(formulario, fotoFormulario, _ahora.AddMinutes(1)),
            "la misma foto no cambia nada en ninguno de los dos");
    }

    [Theory]
    [InlineData(-1_000)]
    [InlineData(0)]
    public void ConUnaRutaUnCostoInvalidoDeNexoDaElMismoResultadoQueElFormularioSinLanzar(int costo)
    {
        var formulario = Formulario();
        var licitacion = Cotizacion(1);

        var a = Aplicar(formulario, Foto("EnCosteo", [("L1", costo)], ["L1"], detalle: false), _ahora);
        var aplicar = () => Aplicar(licitacion, Foto("EnCosteo", [("L1", costo)], ["L1"]), _ahora);

        aplicar.Should().NotThrow("un costo inválido de Nexo nunca debe envenenar el polling");
        Aplicar(Cotizacion(1), Foto("EnCosteo", [("L1", costo)], ["L1"]), _ahora).Should().Be(a);
        licitacion.PrecioFinal.Should().Be(formulario.PrecioFinal).And.BeNull();
        licitacion.MotivoEstadoCosteo.Should().Be(formulario.MotivoEstadoCosteo);
        licitacion.PrimeraRuta!.PrecioFinalLinea.Should().BeNull();
    }

    [Fact]
    public void LasRutasCosteadasTienenPrecioAunqueLaLicitacionSigaPendiente()
    {
        var cotizacion = Cotizacion(3);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L3", 850_000m)], ["L1", "L2", "L3"]);

        Aplicar(cotizacion, foto, _ahora).Should().BeTrue();

        cotizacion.RutasOrdenadas.Select(r => (r.Vehiculos[0].LineaSolicitudNexoId, r.EstadoCosteo, r.PrecioFinalLinea)).Should().Equal(
            ("L1", EstadoCosteo.Autocosteo, 1_176_471m), ("L2", EstadoCosteo.PendienteCosteo, null), ("L3", EstadoCosteo.Autocosteo, 1_000_000m));
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "HU-021: la solicitud resume a sus rutas (la más atrasada)");
        cotizacion.PrecioFinal.Should().BeNull("la licitación no tiene precio total hasta que todas las rutas tienen flete");
        cotizacion.CostoFinal.Should().BeNull();
    }

    [Fact]
    public void ConTodasLasRutasCosteadasLaLicitacionSumaLosPreciosDeSusRutas()
    {
        var cotizacion = Cotizacion(3);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 2_000_000m), ("L3", 850_000m)], ["L1", "L2", "L3"]);

        Aplicar(cotizacion, foto, _ahora).Should().BeTrue();

        cotizacion.CostoFinal.Should().Be(3_850_000m);
        cotizacion.PrecioFinal.Should().Be(cotizacion.Rutas.Sum(r => r.PrecioFinalLinea!.Value)).And.Be(1_176_471m + 2_352_941m + 1_000_000m);
        Aplicar(cotizacion, foto, _ahora.AddMinutes(1)).Should().BeFalse("la misma foto no cambia nada");
    }

    // ADR-014 §5: en una licitación en USD cada precio se calcula en pesos y se convierte con la trmAplicada; el costo sigue en pesos.
    [Fact]
    public void EnDolaresLasRutasElTotalYLosConexosSeConviertenConLaTrmDeLaLicitacion()
    {
        var cotizacion = Cotizacion(3, moneda: "USD", trm: 3273.49m);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 2_000_000m), ("L3", 850_000m)], ["L1", "L2", "L3"]);

        Aplicar(cotizacion, foto, _ahora).Should().BeTrue();

        cotizacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(359.39m, 718.79m, 305.48m);
        cotizacion.PrecioFinal.Should().Be(359.39m + 718.79m + 305.48m, "el total es la suma de las rutas en USD");
        cotizacion.CostoFinal.Should().Be(3_850_000m, "el costo de Nexo se guarda en pesos");

        // HU-021: el conexo es de la ruta (la lectura del costeo no lo importa). HU-043.2 (#251): su precio es su venta (aquí el costo)
        // sin margen, convertida a USD con la trmAplicada: 170.000 / 3.273,49 = 51,93.
        clsCotizacionMuestra.ConConexo(cotizacion, "CX", "Cargue", 170_000m);
        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, cotizacion.PrimeraRuta!).Should().ContainSingle().Which.Precio.Should().Be(51.93m);
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "USD").Should().BeNull("el pliego en USD ya no se rechaza");
        clsCosteoCotizacionService.ValidarTotal(cotizacion, " ")!.Codigo.Should().Be(Cotifly.Dominio.Comun.clsCodigosError.PdfPayloadInvalido,
            "sin moneda es un error de dominio, no una excepción");
    }

    [Fact]
    public void EnDolaresConUnaRutaElPrecioTambienSeConvierte()
    {
        var cotizacion = Cotizacion(1, moneda: "USD", trm: 3273.49m);

        Aplicar(cotizacion, Foto("EnCosteo", [("L1", 1_000_000m)], ["L1"]), _ahora).Should().BeTrue();

        cotizacion.PrecioFinal.Should().Be(359.39m);
        cotizacion.PrimeraRuta!.PrecioFinalLinea.Should().Be(359.39m);
    }

    [Fact]
    public void LosCamionesSeAsocianPorContenidoAunqueNexoLosDevuelvaEnOtroOrden()
    {
        var cotizacion = Cotizacion(3);
        var foto = Foto("EnCosteo", [("L3", 850_000m), ("L1", 1_000_000m), ("L2", 2_000_000m)], ["L3", "L1", "L2"]);

        Aplicar(cotizacion, foto, _ahora).Should().BeTrue();

        // Cada precio va con SU ruta, no con la que ocupa la misma posición en la respuesta de Nexo.
        cotizacion.RutasOrdenadas.Select(r => (r.Vehiculos[0].LineaSolicitudNexoId, r.PrecioFinalLinea))
            .Should().Equal(("L1", (decimal?)1_176_471m), ("L2", 2_352_941m), ("L3", 1_000_000m));
        cotizacion.PrecioFinal.Should().Be(1_176_471m + 2_352_941m + 1_000_000m);
    }

    [Fact]
    public void DosRutasIdenticasNoSeAsocianYLaLicitacionQuedaSinTotal()
    {
        var cotizacion = Cotizacion(["Origen 1", "Origen 1", "Origen 3"]);
        var foto = Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 2_000_000m), ("L3", 850_000m)], ["L1", "L3"]) with { };
        foto = foto with
        {
            LineasSolicitud = ["L1", "L2", "L3"],
            Detalle = [new clsLineaSolicitudNexo("L1", "ORIGEN 1 → DESTINO", null, 1m, null, null), new clsLineaSolicitudNexo("L2", "ORIGEN 1 → DESTINO", null, 1m, null, null),
                       new clsLineaSolicitudNexo("L3", "ORIGEN 3 → DESTINO", null, 1m, null, null)],
        };

        Aplicar(cotizacion, foto, _ahora);

        var rutas = cotizacion.RutasOrdenadas;
        rutas[0].Vehiculos[0].LineaSolicitudNexoId.Should().BeNull("dos rutas idénticas con fletes distintos: no se adivina cuál es cuál");
        rutas[1].Vehiculos[0].LineaSolicitudNexoId.Should().BeNull();
        rutas[2].Vehiculos[0].LineaSolicitudNexoId.Should().Be("L3");
        rutas[2].PrecioFinalLinea.Should().Be(1_000_000m, "la ruta inequívoca sí lleva su precio");
        cotizacion.PrecioFinal.Should().BeNull("el total solo existe con todos los camiones asociados y con precio");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinAsociar(2, 3));
    }

    [Fact]
    public void SiNexoTraeOtroNumeroDeLineasSoloSeAsocianLasQueCoinciden()
    {
        var cotizacion = Cotizacion(3);

        Aplicar(cotizacion, Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 1_000_000m)], ["L2", "L1"]), _ahora);

        cotizacion.Vehiculos.Select(c => c.LineaSolicitudNexoId).Should().Equal("L1", "L2", null);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoSinAsociar(1, 3));
    }

    [Fact]
    public void UnCostoNegativoDeNexoNoRompeElPollingYSeAdvierte()
    {
        var cotizacion = Cotizacion(2);

        var aplicar = () => Aplicar(cotizacion, Foto("EnCosteo", [("L1", -5m), ("L2", 1_000_000m)], ["L1", "L2"]), _ahora);

        aplicar.Should().NotThrow();
        cotizacion.RutasOrdenadas[0].PrecioFinalLinea.Should().BeNull();
        cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(1_176_471m);
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Contain("costo negativo");
    }

    [Fact]
    public void UnaEntregaSinFleteParaAlgunaLineaNoDaPrecioTotal()
    {
        var cotizacion = Cotizacion(2);

        Aplicar(cotizacion, Foto("Entregada", [("L1", 1_000_000m)], ["L1", "L2"]), _ahora);

        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Contain("1 de 2 líneas");
        cotizacion.RutasOrdenadas[1].EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().BeNull();
    }

    [Fact]
    public void ConPdfLasRutasConservanSuPrecioYSoloCambianDeEstado()
    {
        var cotizacion = Cotizacion(2);
        Aplicar(cotizacion, Foto("EnCosteo", [("L1", 1_000_000m), ("L2", 1_000_000m)], ["L1", "L2"]), _ahora);
        cotizacion.AsignarPdf("PCN-X.pdf");
        var precio = cotizacion.PrecioFinal;

        Aplicar(cotizacion, Foto("Entregada", [("L1", 5_000_000m), ("L2", 1_000_000m)], ["L1", "L2"]), _ahora);

        cotizacion.PrecioFinal.Should().Be(precio);
        cotizacion.RutasOrdenadas[0].PrecioFinalLinea.Should().Be(1_176_471m);
        cotizacion.RutasOrdenadas[0].EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.MotivoEstadoCosteo.Should().Contain("versión nueva");
    }

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, ahora, new clsOpcionesCosteoNexo(), _servicio);

    /// <summary>El formulario de una ruta (la muestra), enviado y con el margen de la política.</summary>
    private static clsCotizacionSolicitud Formulario()
    {
        var cotizacion = clsCotizacionMuestra.Formulario("PCN-AP-01102026-002");
        Enviar(cotizacion);
        return cotizacion;
    }

    /// <summary>Licitación de <paramref name="numero"/> rutas distintas: la ruta k va de <c>Origen k</c> a <c>Destino</c> (en Nexo, la <c>Lk</c>).</summary>
    private static clsCotizacionSolicitud Cotizacion(int numero, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m) =>
        Cotizacion([.. Enumerable.Range(1, numero).Select(k => $"Origen {k}")], moneda, trm);

    private static clsCotizacionSolicitud Cotizacion(IReadOnlyList<string> origenes, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-01102026-001", Moneda = moneda, TrmAplicada = trm }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [.. origenes.Select(o => new clsLineaSolicitudCosteo { Origen = o, Destino = "Destino", PesoKg = 1m })],
            [.. origenes.Select((_, i) => (int?)i)],
            3);
        Enviar(cotizacion);
        return cotizacion;
    }

    private static void Enviar(clsCotizacionSolicitud cotizacion)
    {
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }
    }

    /// <summary>
    /// Foto de Nexo: <paramref name="ids"/> en el orden en que Nexo los devuelve (cualquiera) y, por cada <c>Lk</c>, su contenido
    /// (<c>ORIGEN K → DESTINO</c>, como lo normaliza Nexo, en mayúsculas y sin tildes). Sin <paramref name="detalle"/>, sin contenido (un
    /// camión con una línea se asocia directo).
    /// </summary>
    private static clsCosteoConsultadoNexo Foto(
        string estadoSolicitud, IReadOnlyList<(string Id, decimal Flete)> fletes, IReadOnlyList<string> ids, bool detalle = true) =>
        new(estadoSolicitud, ids, new clsCotizacionNexo("COT-1", estadoSolicitud == "Entregada" ? "Entregada" : "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], []))
        {
            Detalle = detalle ? [.. ids.Select(id => new clsLineaSolicitudNexo(id, $"ORIGEN {id[1..]} → DESTINO", null, 1m, null, null))] : [],
        };
}
