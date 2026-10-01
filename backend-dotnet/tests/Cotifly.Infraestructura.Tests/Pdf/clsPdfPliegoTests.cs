using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// PDF de pliego (HU-011.5, ADR-017 §6) renderizado con PDFsharp-MigraDoc y leído con PdfPig: resumen con todas las rutas y el
/// total, tablas por origen, solo los servicios conexos incluidos y la aclaración en dólares cuando aplica. Caso de 12 líneas.
/// </summary>
public sealed class clsPdfPliegoTests
{
    private static readonly clsGeneradorPdfMigraDoc _generador = new();
    private static readonly DateTimeOffset _ahora = new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
    private static readonly clsFirmaDocumento _firma = new("Ana Pérez", null, "aperez@boterosoto.com.co", "3001234567");

    private static (clsCotizacion Cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> Lineas) Pliego(int numero)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-900555111-AP-01102026-001", ClienteNombre = "AGENCIA DE ADUANAS JF", NitCliente = "900555111" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, Referencia = "Pliego 2026-04" });
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        cotizacion.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", _ahora, _ahora);
        var lineas = clsCotizacionLineaSolicitud.Desde(cotizacion.Id, [.. Enumerable.Range(0, numero).Select(i =>
        {
            var (origen, destino, vehiculo, toneladas) = clsPliegoMuestra.Ruta(i);
            return new clsLineaSolicitudCosteo { Origen = origen, Destino = destino, TipoVehiculo = vehiculo, PesoKg = toneladas * 1000m };
        })]);
        for (var i = 0; i < lineas.Count; i++)
        {
            lineas[i].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m + (i * 85_000m));
            lineas[i].RegistrarPrecio(Math.Round((1_000_000m + (i * 85_000m)) / 0.85m, 0, MidpointRounding.ToEven));
        }

        var conexos = clsServiciosConexosJson.Escribir(
        [
            new clsServicioConexoCosteo("CX-1", "Cargue y descargue", "logistico", 1m, 170_000m, 200_000m, true),
            new clsServicioConexoCosteo("CX-2", "Escolta", "adicional", 1m, 425_000m, 500_000m, false),
        ]);
        cotizacion.RegistrarPrecioCosteo(lineas.Sum(l => l.Costo!.Value), lineas.Sum(l => l.PrecioFinal!.Value), conexos, _ahora);
        return (cotizacion, lineas);
    }

    private static clsLectorPdf Generar(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string moneda = "COP", decimal trm = 0m, decimal? precioCop = null)
    {
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, lineas, "Nacional", moneda);
        clsValidadorCotizacionPdfPayload.Validar(payload).Should().BeNull();
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 1), moneda, trm, precioCop).Valor
            with { Variante = VarianteDocumento.Pliego, Pliego = clsCosteoCotizacionService.ResumenPliego(cotizacion, lineas, moneda) };
        var pdf = _generador.Generar(documento);
        pdf.Paginas.Should().BeGreaterThanOrEqualTo(2, "carta y condiciones en la hoja 1; resumen y tarifas desde la hoja 2");
        if (lineas.Count >= 12)
        {
            pdf.Paginas.Should().BeGreaterThan(2, "un pliego de 12 rutas ocupa más de una hoja de tarifas");
        }
        return clsLectorPdf.Leer(pdf.Contenido);
    }

    [Fact]
    public void UnPliegoDeDoceRutasTraeElResumenConTodasLasRutasYElTotal()
    {
        var (cotizacion, lineas) = Pliego(12);

        var texto = Generar(cotizacion, lineas).Texto;

        texto.Should().Contain("RESUMEN DEL PLIEGO PLIEGO 2026-04");
        texto.Should().Contain("TOTAL DEL PLIEGO (12 RUTAS)");
        texto.Should().Contain(clsFormatoMoneda.Formatear(cotizacion.PrecioFinal!.Value, "COP"));
        foreach (var linea in lineas)
        {
            texto.Should().Contain(clsFormatoMoneda.Formatear(linea.PrecioFinal!.Value, "COP"), $"la ruta {linea.Orden} va con su precio");
            texto.Should().Contain(linea.Destino);
        }

        texto.Should().Contain("PLIEGO DE TRANSPORTE NACIONAL").And.Contain("PCN-900555111-AP-01102026-001");
    }

    [Fact]
    public void SoloLosServiciosConexosIncluidosAparecenYNuncaLosCostosDeNexo()
    {
        var (cotizacion, lineas) = Pliego(3);

        var texto = Generar(cotizacion, lineas).Texto;

        texto.Should().Contain("Cargue y descargue").And.Contain("$200.000");
        texto.Should().NotContain("Escolta", "solo se muestra lo realmente incluido (HU-003.5)");
        texto.Should().NotContain("$1.000.000").And.NotContain("$170.000", "el costo de Nexo nunca va al PDF");
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
    public void EnDolaresElPliegoLlevaLaAclaracionDeLaTrm()
    {
        var (cotizacion, lineas) = Pliego(3);

        var texto = Generar(cotizacion, lineas, "USD", trm: 3_950.25m, precioCop: 3_000_000m).Texto;

        texto.Should().Contain("Cotización en dólares:");
    }
}
