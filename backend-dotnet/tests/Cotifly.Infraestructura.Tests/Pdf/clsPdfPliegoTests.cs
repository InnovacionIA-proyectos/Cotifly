using System.Diagnostics;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// PDF de pliego (HU-011.5, ADR-017 §6) renderizado con PDFsharp-MigraDoc y leído con PdfPig: resumen con todas las rutas y el
/// total sin conexos, una sección por ruta (HU-021.9; o solo el resumen en pliegos grandes), los servicios conexos aparte, nunca costos,
/// el encabezado del resumen en cada página y pliegos de hasta 1000 rutas. En USD, la nota de TRM mínima (ADR-014 §5).
/// </summary>
public sealed class clsPdfPliegoTests
{
    private static readonly clsGeneradorPdfMigraDoc _generador = new();
    private static readonly DateTimeOffset _ahora = new(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
    private static readonly clsFirmaDocumento _firma = new("Ana Pérez", null, "aperez@boterosoto.com.co", "3001234567");

    /// <summary>Costo de Nexo de la línea i (base 0): termina en 7 para que ningún precio redondeado lo repita.</summary>
    private static decimal Costo(int i) => 1_000_000m + (i * 85_000m) + 7m;

    /// <summary>
    /// Licitación de <paramref name="numero"/> rutas costeadas (HU-021: una ruta por fila con un camión; el flete en el camión y el precio
    /// en la ruta con el 15 % de la política) y un conexo de 170.007 en la ruta 1 (los conexos son filas de la ruta: todos van en el PDF).
    /// </summary>
    internal static (clsCotizacionSolicitud Cotizacion, IReadOnlyList<clsLineaSolicitud> Lineas) Pliego(int numero, Func<int, (string Origen, string Destino)>? ruta = null)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-900555111-AP-01102026-001", ClienteNombre = "AGENCIA DE ADUANAS JF", NitCliente = "900555111" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [.. Enumerable.Range(0, numero).Select(i =>
            {
                var (origen, destino, vehiculo, toneladas) = clsPliegoMuestra.Ruta(i);
                if (ruta is not null)
                {
                    (origen, destino) = ruta(i);
                }

                return new clsLineaSolicitudCosteo { Origen = origen, Destino = destino, TipoVehiculo = vehiculo, PesoKg = toneladas * 1000m };
            })],
            null,
            1);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        var lineas = cotizacion.RutasOrdenadas;
        for (var i = 0; i < lineas.Count; i++)
        {
            lineas[i].RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
            lineas[i].Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, Costo(i), null);
        }

        lineas[0].AgregarOActualizarConexo("CX-1", "Cargue y descargue", 170_007m, 1m);
        cotizacion.ResumirRutas();
        cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Autocosteo, "COT-1", null);
        cotizacion.RecalcularTotales(_ahora);
        return (cotizacion, lineas);
    }

    /// <summary>El mismo camino de <c>clsCosteoCotizacionService.GenerarAsync</c>: validación del pliego, payload, documento y render.</summary>
    internal static (clsLectorPdf Lector, int Paginas) Generar(clsCotizacionSolicitud cotizacion)
    {
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull();
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, "Nacional", "COP");
        clsValidadorCotizacionPdfPayload.Validar(payload, clsCosteoCotizacionService.MaximoLineasPliego, clsCosteoCotizacionService.MaximoLineasPliego).Should().BeNull();
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 1), "COP", 0m).Valor;
        var pdf = _generador.Generar(clsCosteoCotizacionService.DocumentoPliego(documento, cotizacion, "COP"));
        pdf.Paginas.Should().BeGreaterThanOrEqualTo(2, "carta y condiciones en la hoja 1; resumen y tarifas desde la hoja 2");
        return (clsLectorPdf.Leer(pdf.Contenido), pdf.Paginas);
    }

    /// <summary>
    /// HU-021 (corrección del núcleo): el Excel de la licitación y el PDF del pliego cuadran. Las dos tarifas son el transporte de la ruta
    /// (sin conexos), el total es su suma (sin conexos) y los conexos van aparte una sola vez: tarifas + conexos = precio final.
    /// </summary>
    [Fact]
    public void ElExcelYElPdfDelPliegoCuadranYNoCobranDosVecesLosConexos()
    {
        var (cotizacion, _) = Pliego(3);
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, "Nacional", "COP");
        var datos = clsLicitacionService.DatosExportacion(cotizacion, null, _ahora);
        var excel = new clsExportadorExcelLicitacionClosedXml().Generar(datos);
        excel.EsExitoso.Should().BeTrue();
        using var libro = new XLWorkbook(new MemoryStream(excel.Valor));
        var hoja = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaSinOriginal);
        var resumen = libro.Worksheet(clsExportadorExcelLicitacionClosedXml.HojaResumen);
        var columnaPrecio = hoja.Row(1).CellsUsed().Single(c => c.GetString() == clsExportadorExcelLicitacionClosedXml.ColumnaPrecio("COP")).Address.ColumnNumber;
        var tarifasExcel = Enumerable.Range(2, 3).Select(f => hoja.Cell(f, columnaPrecio).GetValue<decimal>()).ToList();
        var totalExcel = resumen.Column(1).CellsUsed().Single(c => c.GetString() == clsExportadorExcelLicitacionClosedXml.EtiquetaTotal)
            .CellRight().GetValue<decimal>();
        var conexos = datos.ServiciosConexos.Sum(c => c.Precio);

        tarifasExcel.Should().BeEquivalentTo(payload.Secciones!.SelectMany(s => s.Filas!).Select(f => f.Tarifa!.Value), "la misma tarifa en el Excel y el PDF");
        totalExcel.Should().Be(tarifasExcel.Sum(), "el total es sin servicios conexos");
        conexos.Should().BeGreaterThan(0m);
        (totalExcel + conexos).Should().Be(cotizacion.PrecioFinal, "el conexo se cobra una sola vez");
        payload.ServiciosConexos!.Sum(c => c.Precio).Should().Be(conexos);
        Generar(cotizacion).Lector.Texto.Should().Contain(clsFormatoMoneda.Formatear(totalExcel, "COP"), "el total del resumen del PDF es el del Excel");
    }

    /// <summary>
    /// Cada precio se redondea por separado: con fracciones de ,4 el transporte y el conexo bajan y la ruta sube, y sumarlos daría 1 peso
    /// menos. La tarifa absorbe el redondeo: tarifa + conexos = precio de la ruta, exacto.
    /// </summary>
    [Fact]
    public void LaTarifaMasLosConexosDaExactamenteElPrecioDeLaRuta()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 999_999.84m, margen: 0.15m);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 100_000.29m, 1m)], clsCotizacionMuestra.Ahora).EsExitoso.Should().BeTrue();
        var ruta = cotizacion.PrimeraRuta!;
        var conexo = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Single().Precio;
        ruta.PrecioDe(ruta.CostoTransporte, "COP", 0m, out var transporteSolo);

        var tarifa = clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta)!.Value;

        (tarifa + conexo).Should().Be(ruta.PrecioFinalLinea!.Value).And.Be(cotizacion.PrecioFinal!.Value);
        Math.Abs(tarifa - transporteSolo!.Value).Should().BeLessThanOrEqualTo(1m, "a lo sumo una unidad por conexo");
    }

    [Fact]
    public void UnPliegoDeDoceRutasTraeElResumenConTodasLasRutasYElTotalSinConexos()
    {
        var (cotizacion, lineas) = Pliego(12);

        var (lector, paginas) = Generar(cotizacion);
        var texto = lector.Texto;

        paginas.Should().BeGreaterThan(2, "un pliego de 12 rutas ocupa más de una hoja de tarifas");
        texto.Should().Contain("RESUMEN DEL PLIEGO", "HU-021: la referencia del pliego se eliminó (decisión 1)");
        texto.Should().Contain("TOTAL DEL PLIEGO (12 RUTAS, SIN SERVICIOS CONEXOS)");
        texto.Should().Contain(clsFormatoMoneda.Formatear(lineas.Sum(l => clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)!.Value), "COP"), "el total del pliego es sin conexos");
        foreach (var linea in lineas)
        {
            texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, linea)!.Value, "COP"), $"la ruta {linea.Orden} va con su precio");
            texto.Should().Contain(linea.DestinoNombre);
        }

        texto.Should().Contain("PLIEGO DE TRANSPORTE NACIONAL", "con 12 rutas también salen las tablas por origen").And.Contain("PCN-900555111-AP-01102026-001");
    }

    [Fact]
    public void LosServiciosConexosDeLasRutasAparecenConSuPrecioYNuncaLosCostosDeNexo()
    {
        var (cotizacion, lineas) = Pliego(3);

        var texto = Generar(cotizacion).Lector.Texto;

        texto.Should().Contain("Cargue y descargue").And.Contain("$200.008");
        texto.Should().NotContain("$170.007", "el costo del conexo nunca va al PDF");
        foreach (var linea in lineas)
        {
            texto.Should().NotContain(clsFormatoMoneda.Formatear(linea.Vehiculos[0].CostoFlete!.Value, "COP"), $"costo de la línea {linea.Orden}");
        }
    }

    /// <summary>HU-021.9 (#136): el pliego trae una sección por ruta (en su orden), con su origen, su destino, sus camiones y su tarifa.</summary>
    [Fact]
    public void ElPliegoTraeUnaSeccionPorRutaConSuPrecioYSusCamiones()
    {
        var (cotizacion, lineas) = Pliego(12);

        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, "Nacional", "COP");

        payload.Secciones.Should().HaveCount(12).And.OnlyContain(s => s.Filas!.Count == 1);
        payload.Secciones!.Select(s => s.Titulo).Should().Equal(lineas.Select(l => $"RUTA {l.Orden}: PLIEGO DE TRANSPORTE NACIONAL"));
        payload.Secciones!.Select(s => s.Origen).Should().Equal(lineas.Select(l => l.OrigenNombre));
        payload.Secciones!.Select(s => s.Filas![0].Destino).Should().Equal(lineas.Select(l => l.DestinoNombre));
        payload.Secciones!.Select(s => s.Filas![0].VehiculoPeso).Should().Equal(lineas.Select(clsCosteoCotizacionService.Camiones));
        payload.Secciones!.Select(s => s.Filas![0].Tarifa).Should().Equal(lineas.Select(l => clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)));
    }

    /// <summary>HU-021.9: con rutas de varias líneas de negocio, cada sección lleva la de su ruta.</summary>
    [Fact]
    public void CadaSeccionDelPliegoLlevaLaLineaDeNegocioDeSuRuta()
    {
        var (cotizacion, lineas) = Pliego(3);
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.LineaNegocioId))!.SetValue(lineas[2], 7);
        var nombres = new Dictionary<int, string> { [1] = "Nacional", [7] = "Contenedores" };

        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, null, "COP", nombres);

        payload.Secciones!.Select(s => s.Titulo).Should().Equal(
            "RUTA 1: PLIEGO DE TRANSPORTE NACIONAL", "RUTA 2: PLIEGO DE TRANSPORTE NACIONAL", "RUTA 3: PLIEGO DE TRANSPORTE CONTENEDORES");
        clsLineasNegocioRutas.Comun(cotizacion, nombres).Should().BeNull("las rutas tienen líneas distintas");
    }

    /// <summary>
    /// HU-021.9: un pliego de 3 rutas renderizado: cada ruta en su sección con su precio, el resumen con el total sin conexos, los conexos
    /// aparte, y tarifas + conexos = precio final (Σ precioFinalLinea).
    /// </summary>
    [Fact]
    public void UnPliegoDeTresRutasImprimeCadaRutaConSuPrecioYCuadraConElTotal()
    {
        var (cotizacion, lineas) = Pliego(3);

        var texto = System.Text.RegularExpressions.Regex.Replace(Generar(cotizacion).Lector.Texto, @"\s+", " ");

        foreach (var linea in lineas)
        {
            texto.Should().Contain($"RUTA {linea.Orden}: PLIEGO DE TRANSPORTE NACIONAL");
            texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, linea)!.Value, "COP"));
        }

        var tarifas = lineas.Sum(l => clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)!.Value);
        var conexos = lineas.SelectMany(l => clsCosteoCotizacionService.ConexosConPrecio(cotizacion, l)).Sum(c => c.Precio);
        (tarifas + conexos).Should().Be(cotizacion.PrecioFinal, "precioFinal = Σ precioFinalLinea, con los conexos");
        texto.Should().Contain(clsFormatoMoneda.Formatear(tarifas, "COP")).And.Contain("TOTAL DEL PLIEGO (3 RUTAS, SIN SERVICIOS CONEXOS)");
    }

    [Fact]
    public void ElEncabezadoDelResumenSeRepiteEnCadaPaginaYNoHayCostos()
    {
        var (cotizacion, lineas) = Pliego(60);

        var (lector, _) = Generar(cotizacion);

        var conRutas = lector.Paginas.Select((t, i) => (Texto: t, Pagina: i + 1)).Where(p => p.Texto.Contains('→')).ToList();
        conRutas.Count.Should().BeGreaterThan(1, "60 rutas ocupan más de una hoja");
        conRutas.Should().OnlyContain(p => p.Texto.Contains("VEHÍCULO Y PESO"), "el encabezado del resumen se repite en cada página");
        foreach (var linea in lineas)
        {
            lector.Texto.Should().NotContain(clsFormatoMoneda.Formatear(linea.Vehiculos[0].CostoFlete!.Value, "COP"), $"costo de la línea {linea.Orden}");
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

        var (lector, paginas) = Generar(cotizacion);

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), $"{numero} rutas");
        paginas.Should().BeLessThan(numero / 10 + 10, "solo el resumen: unas 30 rutas por página, sin las tablas por origen");
        lector.Texto.Should().NotContain("PLIEGO DE TRANSPORTE NACIONAL", "sin tablas por origen en modo compacto");
        lector.Texto.Should().Contain($"TOTAL DEL PLIEGO ({numero} RUTAS, SIN SERVICIOS CONEXOS)");
        lector.Texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, lineas[^1])!.Value, "COP"), "la última ruta también está");
    }

    [Fact]
    public void VeintiunaRutasSalenCompactas()
    {
        var (cotizacion, _) = Pliego(21, i => ($"ORIGEN {i}", "BOGOTA, D.C."));
        var (veinte, _) = Pliego(20);

        clsCosteoCotizacionService.EsPliegoCompacto(cotizacion).Should().BeTrue("una sección por ruta: más de 20 secciones no caben");
        clsCosteoCotizacionService.EsPliegoCompacto(veinte).Should().BeFalse();
        Generar(cotizacion).Lector.Texto.Should().Contain("ORIGEN 20").And.NotContain("RUTA 21: PLIEGO");
    }

    [Fact]
    public void LasCiudadesMuyLargasSeImprimenCompletas()
    {
        var largo = new string('X', 140) + "FINAL";
        var (cotizacion, lineas) = Pliego(6, i => (largo + "O" + i, largo + "D" + i));

        Generar(cotizacion).Lector.Texto.Replace(" ", string.Empty, StringComparison.Ordinal).Should().Contain("FINALD5");
    }

    /// <summary>HU-021.9 (#136): <c>EsPliego</c> = "tiene más de una ruta"; una licitación de una fila sale con el PDF de una ruta.</summary>
    [Fact]
    public void UnaLicitacionDeUnaLineaSaleConElPdfDeUnaRuta()
    {
        var (cotizacion, lineas) = Pliego(1);

        lineas.Should().ContainSingle();
        clsCosteoCotizacionService.EsPliego(cotizacion).Should().BeFalse("el pliego es para más de una ruta");
        clsCosteoCotizacionService.EsPliego(Pliego(2).Cotizacion).Should().BeTrue();
        var texto = GenerarRuta(cotizacion, "COP", 0m);
        texto.Should().Contain("BUENAVENTURA").And.Contain("BOGOTA").And.NotContain("TOTAL DEL PLIEGO");
        texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, lineas[0])!.Value, "COP"));
    }

    /// <summary>
    /// HU-021.9: el PDF de una ruta con dos camiones iguales los agrupa ("2 × 3S3 - FURGON") con el peso total, imprime la tarifa del
    /// transporte y los conexos aparte: tarifa + conexos = precio de la ruta = precio final. Nunca los costos.
    /// </summary>
    [Fact]
    public void ElPdfDeUnaRutaConDosCamionesLosAgrupaYCuadraConElPrecio()
    {
        var (cotizacion, ruta) = RutaConDosCamiones();

        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull();
        var texto = GenerarRuta(cotizacion, "COP", 0m);

        clsCosteoCotizacionService.Camiones(ruta).Should().Be("2 × 3S3 - FURGON (20000 KG)");
        texto.Should().Contain("2 × 3S3 - FURGON (20000", "la celda puede partirse en dos renglones junto a la tarifa");
        var tarifa = clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta)!.Value;
        var conexo = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Single().Precio;
        (tarifa + conexo).Should().Be(ruta.PrecioFinalLinea!.Value).And.Be(cotizacion.PrecioFinal!.Value);
        texto.Should().Contain(clsFormatoMoneda.Formatear(tarifa, "COP")).And.Contain("Cargue y descargue").And.Contain(clsFormatoMoneda.Formatear(conexo, "COP"));
        foreach (var camion in ruta.Vehiculos)
        {
            texto.Should().NotContain(clsFormatoMoneda.Formatear(camion.CostoFlete!.Value, "COP"), "el costo de Nexo nunca va al PDF");
        }
    }

    /// <summary>HU-021.9 + ADR-014 §5: el PDF de una ruta en USD imprime la tarifa en US$ y la nota de TRM mínima tal como estaba.</summary>
    [Fact]
    public void ElPdfDeUnaRutaEnDolaresLlevaLaNotaDeTrmMinima()
    {
        var (cotizacion, ruta) = RutaConDosCamiones("USD", 3273.49m);

        clsCosteoCotizacionService.ValidarTotal(cotizacion, "USD").Should().BeNull();
        var texto = GenerarRuta(cotizacion, "USD", 3273.49m);

        texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta)!.Value, "USD"));
        texto.Should().Contain(
            "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que la tarifa puede variar de acuerdo al "
            + "comportamiento de la misma a la fecha de elaboración de la CPI.");
    }

    /// <summary>HU-021.9: el PDF exige todas las rutas con precio (el ajuste de margen es por ruta, el PDF no).</summary>
    [Fact]
    public void SinPrecioEnAlgunaRutaElPdfEsCosteoNoListo()
    {
        var (cotizacion, lineas) = Pliego(3);
        lineas[1].FijarPrecio(null);
        cotizacion.RecalcularPrecio(_ahora);

        var error = clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP")!;

        error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        error.Mensaje.Should().Contain("ruta 2");
    }

    /// <summary>Formulario de una ruta con dos camiones 3S3 FURGON (10.000 kg cada uno) costeados y el conexo "Cargue y descargue".</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) RutaConDosCamiones(string moneda = "COP", decimal trm = 0m)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-01102026-002", ClienteNombre = "AGENCIA DE ADUANAS JF", NitCliente = "900555111", Moneda = moneda, TrmAplicada = trm,
        }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(clsSolicitudCosteoMuestra.RutaCaptura() with { PesoKg = 20_000m, Posiciones = [] }, 1, 0,
                [new clsCamionPlaneado("3S3", "FURGON", 10_000m), new clsCamionPlaneado("3S3", "FURGON", 10_000m)],
                [new clsCambioConexo("CX-1", "Cargue y descargue", 170_007m, 1m)]),
        ]);
        cotizacion.RegistrarEnvioCosteo("SOL-2", false, _ahora, _ahora);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        clsCotizacionMuestra.AplicarCosteo(cotizacion, [1_000_007m, 1_000_007m], EstadoCosteo.Autocosteo, null);
        return (cotizacion, ruta);
    }

    /// <summary>El camino de <c>GenerarAsync</c> para una ruta: payload de la ruta, documento y render; devuelve el texto (espacios normalizados).</summary>
    private static string GenerarRuta(clsCotizacionSolicitud cotizacion, string moneda, decimal trm)
    {
        clsCosteoCotizacionService.ValidarTotal(cotizacion, moneda).Should().BeNull();
        var payload = clsCosteoCotizacionService.Payload(cotizacion, "Nacional", moneda);
        clsValidadorCotizacionPdfPayload.Validar(payload).Should().BeNull();
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 1), moneda, trm).Valor;
        var pdf = _generador.Generar(documento);
        return System.Text.RegularExpressions.Regex.Replace(clsLectorPdf.Leer(pdf.Contenido).Texto, @"\s+", " ");
    }

    // ADR-014 §5: el pliego en USD sale con las tarifas en US$ y la nota de TRM mínima con la TRM aplicada de la licitación.
    [Fact]
    public void EnDolaresElPliegoSaleEnUsdConLaNotaDeTrmMinima()
    {
        var (cotizacion, lineas) = Pliego(3);

        clsCosteoCotizacionService.ValidarTotal(cotizacion, "USD").Should().BeNull();
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, "Nacional", "USD");
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 5), "USD", 3273.49m).Valor;
        var pdf = _generador.Generar(clsCosteoCotizacionService.DocumentoPliego(documento, cotizacion, "USD"));
        var texto = System.Text.RegularExpressions.Regex.Replace(clsLectorPdf.Leer(pdf.Contenido).Texto, @"\s+", " ");

        texto.Should().Contain(clsFormatoMoneda.Formatear(clsCosteoCotizacionService.PrecioTransporte(cotizacion, lineas[0])!.Value, "USD"));
        texto.Should().Contain(
            "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que la tarifa puede variar de acuerdo al "
            + "comportamiento de la misma a la fecha de elaboración de la CPI.");
    }

    [Fact]
    public void UnPliegoEnPesosNoLlevaLaNotaDeTrm()
    {
        var (cotizacion, lineas) = Pliego(3);

        Generar(cotizacion).Lector.Texto.Should().NotContain("TRM mínima").And.NotContain("elaboración de la CPI");
    }

    [Fact]
    public void UnPliegoEnDolaresSinTrmEsPayloadInvalido()
    {
        var (cotizacion, _) = Pliego(3);
        var payload = clsCosteoCotizacionService.PayloadPliego(cotizacion, "Nacional", "USD");

        clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, _firma, new DateOnly(2026, 10, 5), "USD", 0m).Error.Codigo
            .Should().Be(clsCodigosError.PdfPayloadInvalido);
    }

    [Fact]
    public void UnTotalQueNoCuadraConLasLineasEsCosteoNoListo()
    {
        var (cotizacion, lineas) = Pliego(3);
        lineas[0].FijarPrecio(1m);

        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP")!.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }
}
