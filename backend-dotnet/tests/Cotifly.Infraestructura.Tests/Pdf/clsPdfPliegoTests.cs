using System.Diagnostics;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// PDF de pliego (HU-011.5, ADR-017 §6) renderizado con PDFsharp-MigraDoc y leído con PdfPig: resumen con todas las rutas y el
/// total sin conexos, tablas por origen (o solo el resumen en pliegos grandes), solo los servicios conexos incluidos, nunca costos,
/// el encabezado del resumen en cada página y pliegos de hasta 1000 rutas. USD se rechaza hasta integrar la TRM.
/// </summary>
public sealed class clsPdfPliegoTests
{
    private static readonly clsGeneradorPdfMigraDoc _generador = new();
    private static readonly DateTimeOffset _ahora = new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
    private static readonly clsFirmaDocumento _firma = new("Ana Pérez", null, "aperez@boterosoto.com.co", "3001234567");

    /// <summary>Costo de Nexo de la línea i (base 0): termina en 7 para que ningún precio redondeado lo repita.</summary>
    private static decimal Costo(int i) => 1_000_000m + (i * 85_000m) + 7m;

    private static (clsCotizacion Cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> Lineas) Pliego(int numero, Func<int, (string Origen, string Destino)>? ruta = null)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-900555111-AP-01102026-001", ClienteNombre = "AGENCIA DE ADUANAS JF", NitCliente = "900555111" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, Referencia = "Pliego 2026-04" });
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        cotizacion.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _ahora, _ahora);
        var lineas = clsCotizacionLineaSolicitud.Desde(cotizacion.Id, [.. Enumerable.Range(0, numero).Select(i =>
        {
            var (origen, destino, vehiculo, toneladas) = clsPliegoMuestra.Ruta(i);
            if (ruta is not null)
            {
                (origen, destino) = ruta(i);
            }

            return new clsLineaSolicitudCosteo { Origen = origen, Destino = destino, TipoVehiculo = vehiculo, PesoKg = toneladas * 1000m };
        })]);
        for (var i = 0; i < lineas.Count; i++)
        {
            lineas[i].RegistrarCosteo(EstadoCosteo.Autocosteo, Costo(i));
            lineas[i].RegistrarPrecio(Math.Round(Costo(i) / 0.85m, 0, MidpointRounding.ToEven));
        }

        var conexos = clsServiciosConexosJson.Escribir(
        [
            new clsServicioConexoCosteo("CX-1", "Cargue y descargue", "logistico", 1m, 170_007m, 200_008m, true),
            new clsServicioConexoCosteo("CX-2", "Escolta", "adicional", 1m, 425_000m, 500_000m, false),
        ]);
        cotizacion.RegistrarPrecioCosteo(lineas.Sum(l => l.Costo!.Value), lineas.Sum(l => l.PrecioFinal!.Value), conexos, _ahora);
        return (cotizacion, lineas);
    }

    /// <summary>El mismo camino de <c>clsCosteoCotizacionService.GenerarAsync</c>: validación del pliego, payload, documento y render.</summary>
    private static (clsLectorPdf Lector, int Paginas) Generar(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        clsCosteoCotizacionService.ValidarPliego(cotizacion, lineas, "COP").Should().BeNull();
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, lineas, "Nacional", "COP");
        clsValidadorCotizacionPdfPayload.Validar(payload, clsCosteoCotizacionService.MaximoLineasPliego, clsCosteoCotizacionService.MaximoLineasPliego).Should().BeNull();
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 1), "COP", 0m).Valor;
        var pdf = _generador.Generar(clsCosteoCotizacionService.DocumentoPliego(documento, cotizacion, lineas, "COP"));
        pdf.Paginas.Should().BeGreaterThanOrEqualTo(2, "carta y condiciones en la hoja 1; resumen y tarifas desde la hoja 2");
        return (clsLectorPdf.Leer(pdf.Contenido), pdf.Paginas);
    }

    [Fact]
    public void UnPliegoDeDoceRutasTraeElResumenConTodasLasRutasYElTotalSinConexos()
    {
        var (cotizacion, lineas) = Pliego(12);

        var (lector, paginas) = Generar(cotizacion, lineas);
        var texto = lector.Texto;

        paginas.Should().BeGreaterThan(2, "un pliego de 12 rutas ocupa más de una hoja de tarifas");
        texto.Should().Contain("RESUMEN DEL PLIEGO PLIEGO 2026-04");
        texto.Should().Contain("TOTAL DEL PLIEGO (12 RUTAS, SIN SERVICIOS CONEXOS)");
        texto.Should().Contain(clsFormatoMoneda.Formatear(cotizacion.PrecioFinal!.Value, "COP"));
        foreach (var linea in lineas)
        {
            texto.Should().Contain(clsFormatoMoneda.Formatear(linea.PrecioFinal!.Value, "COP"), $"la ruta {linea.Orden} va con su precio");
            texto.Should().Contain(linea.Destino);
        }

        texto.Should().Contain("PLIEGO DE TRANSPORTE NACIONAL", "con 12 rutas también salen las tablas por origen").And.Contain("PCN-900555111-AP-01102026-001");
    }

    [Fact]
    public void SoloLosServiciosConexosIncluidosAparecenYNuncaLosCostosDeNexo()
    {
        var (cotizacion, lineas) = Pliego(3);

        var texto = Generar(cotizacion, lineas).Lector.Texto;

        texto.Should().Contain("Cargue y descargue").And.Contain("$200.008");
        texto.Should().NotContain("Escolta", "solo se muestra lo realmente incluido (HU-003.5)");
        texto.Should().NotContain("$170.007", "el costo del conexo nunca va al PDF");
        foreach (var linea in lineas)
        {
            texto.Should().NotContain(clsFormatoMoneda.Formatear(linea.Costo!.Value, "COP"), $"costo de la línea {linea.Orden}");
        }
    }

    [Fact]
    public void LasRutasSeAgrupanPorOrigenEnLasTablasDeTarifa()
    {
        var (cotizacion, lineas) = Pliego(12);

        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, lineas, "Nacional", "COP");

        payload.Secciones!.Select(s => s.Origen).Should().OnlyHaveUniqueItems().And.Contain(["BUENAVENTURA", "BOGOTA, D.C."]);
        payload.Secciones!.Sum(s => s.Filas!.Count).Should().Be(12);
        payload.Secciones!.SelectMany(s => s.Filas!).Select(f => f.Tarifa).Should().BeEquivalentTo(lineas.Select(l => l.PrecioFinal));
    }

    [Fact]
    public void ElEncabezadoDelResumenSeRepiteEnCadaPaginaYNoHayCostos()
    {
        var (cotizacion, lineas) = Pliego(60);

        var (lector, _) = Generar(cotizacion, lineas);

        var conRutas = lector.Paginas.Select((t, i) => (Texto: t, Pagina: i + 1)).Where(p => p.Texto.Contains('→')).ToList();
        conRutas.Count.Should().BeGreaterThan(1, "60 rutas ocupan más de una hoja");
        conRutas.Should().OnlyContain(p => p.Texto.Contains("VEHÍCULO Y PESO"), "el encabezado del resumen se repite en cada página");
        foreach (var linea in lineas)
        {
            lector.Texto.Should().NotContain(clsFormatoMoneda.Formatear(linea.Costo!.Value, "COP"), $"costo de la línea {linea.Orden}");
        }
    }

    [Theory]
    [InlineData(61)]
    [InlineData(300)]
    [InlineData(1000)]
    public void UnPliegoGrandeSaleCompactoConTodasLasRutasEnTiempoRazonable(int numero)
    {
        var (cotizacion, lineas) = Pliego(numero);
        var reloj = Stopwatch.StartNew();

        var (lector, paginas) = Generar(cotizacion, lineas);

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), $"{numero} rutas");
        paginas.Should().BeLessThan(numero / 10 + 10, "solo el resumen: unas 30 rutas por página, sin las tablas por origen");
        lector.Texto.Should().NotContain("PLIEGO DE TRANSPORTE NACIONAL", "sin tablas por origen en modo compacto");
        lector.Texto.Should().Contain($"TOTAL DEL PLIEGO ({numero} RUTAS, SIN SERVICIOS CONEXOS)");
        lector.Texto.Should().Contain(clsFormatoMoneda.Formatear(lineas[^1].PrecioFinal!.Value, "COP"), "la última ruta también está");
    }

    [Fact]
    public void VeintiunOrigenesSalenCompactos()
    {
        var (cotizacion, lineas) = Pliego(21, i => ($"ORIGEN {i}", "BOGOTA, D.C."));

        clsCosteoCotizacionService.EsPliegoCompacto(lineas).Should().BeTrue();
        Generar(cotizacion, lineas).Lector.Texto.Should().Contain("ORIGEN 20");
    }

    [Fact]
    public void LasCiudadesMuyLargasSeImprimenCompletas()
    {
        var largo = new string('X', 140) + "FINAL";
        var (cotizacion, lineas) = Pliego(6, i => (largo + "O" + i, largo + "D" + i));

        Generar(cotizacion, lineas).Lector.Texto.Replace(" ", string.Empty, StringComparison.Ordinal).Should().Contain("FINALD5");
    }

    [Fact]
    public void UnaLicitacionDeUnaLineaTambienSaleComoPliegoConSuRuta()
    {
        var (cotizacion, lineas) = Pliego(1);

        clsCosteoCotizacionService.EsPliego(cotizacion, lineas).Should().BeTrue("sus columnas de línea en Cotizaciones son nulas");
        Generar(cotizacion, lineas).Lector.Texto.Should().Contain("BUENAVENTURA").And.Contain("BOGOTA").And.Contain("TOTAL DEL PLIEGO (1 RUTAS");
    }

    [Fact]
    public void EnDolaresElPliegoSeRechazaHastaIntegrarLaTrm()
    {
        var (cotizacion, lineas) = Pliego(3);

        clsCosteoCotizacionService.ValidarPliego(cotizacion, lineas, "USD")!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
    }

    [Fact]
    public void UnTotalQueNoCuadraConLasLineasEsCosteoNoListo()
    {
        var (cotizacion, lineas) = Pliego(3);
        lineas[0].RegistrarPrecio(1m);

        clsCosteoCotizacionService.ValidarPliego(cotizacion, lineas, "COP")!.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }
}
