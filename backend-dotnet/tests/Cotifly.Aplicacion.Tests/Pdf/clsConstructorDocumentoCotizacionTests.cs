using System.Text.Json;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Tests.Pdf;

/// <summary>
/// Reglas del documento (port de las pruebas puras de <c>tests/test_pdf.py</c> sobre <c>construir_html</c>) sin renderizar:
/// datos de la cotización, bloques condicionales, variante multi, tarifa calculada, textos del payload y servicios conexos.
/// </summary>
public sealed class clsConstructorDocumentoCotizacionTests
{
    private static readonly DateOnly _hoy = new(2026, 9, 24);
    private static readonly clsFirmaDocumento _firma = new("Nathaly Vargas Garcia", "Ejecutiva Comercial", "nvargas@boterosoto.com.co", null);

    private static clsDocumentoCotizacion Construir(clsCotizacionPdfPayload payload, clsFirmaDocumento? firma = null)
    {
        var resultado = clsConstructorDocumentoCotizacion.Construir(payload, "PCN-19032026", firma ?? _firma, _hoy, "COP", 0m);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        return resultado.Valor;
    }

    // test_html_contiene_datos_de_la_cotizacion
    [Fact]
    public void ElDocumentoTieneLosDatosDeLaCotizacion()
    {
        var documento = Construir(clsEjemploPdf.Payload());

        documento.Variante.Should().Be(VarianteDocumento.TarifaUnica);
        documento.Cliente.Should().Be("LONGYIN");
        documento.Referencia.Should().Be("PCN-19032026");
        documento.FechaLarga.Should().Be("Yumbo, 19 de marzo de 2026");
        documento.Contactos.Should().Equal("Sr. Nicolás Alarcon", "Sra. Sandra Fernandez");
        documento.Asunto.Should().Be("presentación oferta de servicios de Transporte Nacional");
        documento.Subtitulo.Should().Be("TRANSPORTE NACIONAL EN CALIDAD DE EXPORTACIONES:");
        documento.Secciones.Should().ContainSingle();
        documento.Secciones[0].Titulo.Should().Be("COTIZACIÓN TRANSPORTE NACIONAL EXPO – BSSL");
        documento.Secciones[0].Origen.Should().Be("YUMBO");
        documento.Secciones[0].Filas.Select(f => f.Tarifa).Should().Equal("$3.000.000", "$3.600.000");
        documento.Secciones[0].Filas[0].VehiculoPeso.Should().Be("Tractomula - 34 TON");
        documento.Firma.Nombre.Should().Be("Nathaly Vargas Garcia");
        documento.Facturacion.Should().Be(clsTextosEstandar.TarifaUnica.Facturacion);
        documento.TituloStandBy.Should().Be("Horas logísticas adicionales");
        documento.NotaDecreto.Should().Be(clsTextosEstandar.NotaDecreto);
        documento.NotaGeneral.Should().Be(clsTextosEstandar.TarifaUnica.NotaGeneral);
        documento.Incluye.Should().Equal(clsTextosEstandar.TarifaUnica.Incluye);
        documento.NoIncluye.Should().Equal(clsTextosEstandar.TarifaUnica.NoIncluye);
        documento.FraseFinal.Should().Be(new clsFraseFinalDocumento(clsTextosEstandar.FraseFinal, clsTextosEstandar.EnlaceWeb));
        documento.BloquesAdicionales.Should().BeEmpty();
    }

    [Fact]
    public void LaLineaNacionalLlevaSuImagenYSuLeyenda()
    {
        var documento = Construir(clsEjemploPdf.Payload());

        documento.ImagenLinea.Should().Be("nacional");
        documento.LeyendaImagen.Should().Be("Transporte nacional seguro, confiable y a tiempo en todo el país.");
    }

    [Theory]
    [InlineData("Internacional", "andino", true)]
    [InlineData("ANDINO", "andino", true)]
    [InlineData("Importación", "importacion", true)]
    [InlineData("Distribución", "distribucion", false)]
    [InlineData("Almacenamiento", "almacenamiento", false)]
    [InlineData("ITR", "itr", false)]
    [InlineData("Almacenamiento3PL", null, false)]
    public void CadaLineaTieneSuImagenYSoloAlgunasSuLeyenda(string linea, string? imagen, bool conLeyenda)
    {
        var payload = clsEjemploPdf.Payload();
        payload.LineaNegocio = linea;

        var documento = Construir(payload);

        documento.ImagenLinea.Should().Be(imagen);
        (documento.LeyendaImagen is not null).Should().Be(conLeyenda);
    }

    // test_html_determinista
    [Fact]
    public void ElDocumentoEsDeterminista() =>
        JsonSerializer.Serialize(Construir(clsEjemploPdf.Payload())).Should().Be(JsonSerializer.Serialize(Construir(clsEjemploPdf.Payload())));

    // test_bloque_aduanero_solo_internacional
    [Fact]
    public void ElBloqueAduaneroSoloApareceEnLaLineaInternacional()
    {
        var nacional = clsEjemploPdf.Payload();
        nacional.Aduana = new clsAduanaPayload { Modalidad = "ANDINO", Frontera = "Ipiales", Arancel = "5%" };
        Construir(nacional).BloquesAdicionales.Should().BeEmpty();

        var internacional = clsEjemploPdf.Payload();
        internacional.LineaNegocio = "Internacional";
        internacional.Aduana = new clsAduanaPayload { Modalidad = "ANDINO", Frontera = "Ipiales", Arancel = "5%" };
        var bloque = Construir(internacional).BloquesAdicionales.Should().ContainSingle().Subject;

        bloque.Titulo.Should().Be("Datos aduaneros (ANDINO)");
        bloque.Datos.Should().Equal(
            new clsDatoDocumento("Modalidad aduanera", "ANDINO"),
            new clsDatoDocumento("Frontera", "Ipiales"),
            new clsDatoDocumento("Arancel", "5%"));
    }

    // test_bloque_extradimensionada_y_refrigerada
    [Fact]
    public void LosBloquesExtradimensionadaYRefrigeradaMuestranSoloLoQueTraen()
    {
        var payload = clsCotizacionPdfPayload.DesdeJson(File.ReadAllText(clsEjemploPdf.Ruta)
            .Replace("\"extradimensionada\": null", "\"extradimensionada\": {\"peso_kg\": 40000, \"ancho\": \"3m\", \"largo\": \"20m\"}", StringComparison.Ordinal)
            .Replace("\"refrigerada\": null", "\"refrigerada\": {\"temperatura\": \"-18°C\"}", StringComparison.Ordinal)).Valor;

        var bloques = Construir(payload).BloquesAdicionales;

        bloques.Select(b => b.Titulo).Should().Equal("Mercancía extradimensionada", "Mercancía refrigerada");
        bloques[0].Datos.Should().Equal(
            new clsDatoDocumento("Peso (kg)", "40000"),
            new clsDatoDocumento("Ancho", "3m"),
            new clsDatoDocumento("Largo", "20m"));
        bloques[1].Datos.Should().Equal(new clsDatoDocumento("Temperatura", "-18°C"));
    }

    // test_multi_seccion_usa_armar_multi
    [Fact]
    public void ConSeccionesSeUsaLaVarianteMulti()
    {
        var documento = Construir(clsEjemploPdf.Multi());

        documento.Variante.Should().Be(VarianteDocumento.MultiSeccion);
        documento.Secciones.Select(s => s.Titulo).Should().Equal("TRAMO 1", "TRAMO 2");
        documento.Secciones.Select(s => s.Origen).Should().Equal("YUMBO", "BOGOTA");
        documento.Secciones[1].Filas[0].Tarifa.Should().Be("$1.200.000");
        documento.Subtitulo.Should().BeNull();
        documento.NotaDecreto.Should().BeNull();
        documento.NotaGeneral.Should().BeNull();
        documento.ImagenLinea.Should().BeNull();
        documento.TituloStandBy.Should().Be("Stand by");
        documento.Seguro.Should().Equal(clsTextosEstandar.MultiSeccion.Seguro);
        documento.Introduccion.Count(p => p.EsVineta).Should().Be(3);
        documento.Introduccion.Where(p => p.EsVineta).Should().OnlyContain(p => !p.Texto.StartsWith('·'));
    }

    [Theory]
    [InlineData("margen", "0.25", "$4.000.000")]
    [InlineData("markup", "0.25", "$3.750.000")]
    [InlineData(null, "0.25", "$4.000.000")]
    public void SinTarifaSeCalculaConCostoYMargen(string? metodo, string margen, string esperado)
    {
        var payload = clsEjemploPdf.Payload();
        payload.MetodoCalculo = metodo;
        payload.Tarifa!.Filas![0].Tarifa = null;
        payload.Tarifa.Filas[0].Costo = 3_000_000m;
        payload.Tarifa.Filas[0].Margen = decimal.Parse(margen, System.Globalization.CultureInfo.InvariantCulture);

        Construir(payload).Secciones[0].Filas[0].Tarifa.Should().Be(esperado);
    }

    [Fact]
    public void SinTarifaNiCostoSeMuestraLaRaya()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Filas![0].Tarifa = null;

        Construir(payload).Secciones[0].Filas[0].Tarifa.Should().Be("—");
    }

    [Fact]
    public void UnMargenDeUnoConMetodoMargenEsPayloadInvalido()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Filas![0].Tarifa = null;
        payload.Tarifa.Filas[0].Costo = 1000m;
        payload.Tarifa.Filas[0].Margen = 1m;

        var resultado = clsConstructorDocumentoCotizacion.Construir(payload, "PCN-1", _firma, _hoy, "COP", 0m);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
    }

    [Fact]
    public void LaTarifaEnDolaresLlevaElPrefijoUsd()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Filas![0].Moneda = "USD";
        payload.Tarifa.Filas[0].Tarifa = 1200m;

        Construir(payload).Secciones[0].Filas[0].Tarifa.Should().Be("US$1.200,00");
    }

    [Fact]
    public void LosTextosDelPayloadReemplazanLosEstandar()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Textos = new clsTextosPayload
        {
            Intro = ["Intro propia."],
            Seguro = "Seguro propio.\n\nSegundo párrafo.",
            Vigencia = "Vigencia propia.",
            NoIncluye = ["Nada más"],
        };
        payload.Asunto = "Asunto propio";
        payload.DiasLibres = "3 días libres";
        payload.NotaDecreto = "Decreto propio.";
        payload.FraseFinal = "Gracias.";

        var documento = Construir(payload);

        documento.Introduccion.Should().Equal(new clsParrafoDocumento("Intro propia."));
        documento.Seguro.Should().Equal("Seguro propio.", "Segundo párrafo.");
        documento.Vigencia.Should().Be("Vigencia propia.");
        documento.NoIncluye.Should().Equal("Nada más");
        documento.Asunto.Should().Be("Asunto propio");
        documento.DiasLibres.Should().Be("3 días libres");
        documento.NotaDecreto.Should().Be("Decreto propio.");
        documento.FraseFinal.Should().Be(new clsFraseFinalDocumento("Gracias.", null));
        documento.Facturacion.Should().Be(clsTextosEstandar.TarifaUnica.Facturacion, "lo que no se reemplaza queda estándar");
    }

    [Fact]
    public void LasListasDeLaTarifaGananALasDeTextos()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Incluye = ["De la tarifa"];
        payload.Textos = new clsTextosPayload { Incluye = ["De textos"] };

        Construir(payload).Incluye.Should().Equal("De la tarifa");
    }

    [Fact]
    public void LosServiciosConexosSeItemizanConElFleteYSuPrecio()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Incluye = ["Texto fijo que ya no aplica"];
        payload.ServiciosConexos =
        [
            new clsServicioConexoPayload { Descripcion = "Levante de contenedor", Precio = 350_000m },
            new clsServicioConexoPayload { Descripcion = "Devolución de contenedor", Precio = 420_000m },
            new clsServicioConexoPayload { Descripcion = "Inspección", Precio = null },
        ];

        Construir(payload).Incluye.Should().Equal(
            "Flete YUMBO – BUN",
            "Levante de contenedor: $350.000",
            "Devolución de contenedor: $420.000",
            "Inspección");
    }

    [Fact]
    public void LaFirmaEsLaQueLlegaYElCelularDelPayloadSeIgnora()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Comercial!.Cel = "+57 300 000 0000";
        payload.Comercial.Nombre = "Nombre inventado";

        var documento = Construir(payload, _firma with { Celular = null });

        documento.Firma.Celular.Should().BeNull();
        documento.Firma.Nombre.Should().Be("Nathaly Vargas Garcia");
    }

    [Fact]
    public void LosTextosSeLimpianDeCaracteresDeControlYNoSeEscapan()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Cliente = "<b>ACME</b>\u0000\u0007";

        Construir(payload).Cliente.Should().Be("<b>ACME</b>");
    }

    [Fact]
    public void SinFechaSeUsaLaDeHoy()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Fecha = null;
        payload.Ciudad = null;

        Construir(payload).FechaLarga.Should().Be("Yumbo, 24 de septiembre de 2026");
    }

    [Fact]
    public void ElEjemploEsValido() => clsValidadorCotizacionPdfPayload.Validar(clsEjemploPdf.Payload()).Should().BeNull();

    [Fact]
    public void ElPayloadSinLineaSinFilasOConTextosDesbordadosEsInvalido()
    {
        var sinLinea = clsEjemploPdf.Payload();
        sinLinea.LineaNegocio = " ";
        var sinFilas = clsEjemploPdf.Payload();
        sinFilas.Tarifa!.Filas = [];
        var largo = clsEjemploPdf.Payload();
        largo.Cliente = new string('x', clsValidadorCotizacionPdfPayload.MaxTextoCorto + 1);
        var metodo = clsEjemploPdf.Payload();
        metodo.MetodoCalculo = "descuento";
        var servicio = clsEjemploPdf.Payload();
        servicio.ServiciosConexos = [new clsServicioConexoPayload { Descripcion = "Levante", Precio = -1m }];
        var sinDescripcion = clsEjemploPdf.Payload();
        sinDescripcion.ServiciosConexos = [new clsServicioConexoPayload { Precio = 10m }];
        var tarifaNegativa = clsEjemploPdf.Payload();
        tarifaNegativa.Tarifa!.Filas![0].Tarifa = -1m;
        var costoNegativo = clsEjemploPdf.Payload();
        costoNegativo.Tarifa!.Filas![0].Tarifa = null;
        costoNegativo.Tarifa.Filas[0].Costo = -100m;
        costoNegativo.Tarifa.Filas[0].Margen = 0.1m;
        var margenNegativo = clsEjemploPdf.Payload();
        margenNegativo.Tarifa!.Filas![0].Margen = -0.1m;
        var muchasFilas = clsEjemploPdf.Payload();
        muchasFilas.Tarifa!.Filas = [.. Enumerable.Range(0, clsValidadorCotizacionPdfPayload.MaxFilas + 1).Select(_ => new clsFilaTarifaPayload { Destino = "X", Tarifa = 1m })];

        foreach (var payload in new[] { sinLinea, sinFilas, largo, metodo, servicio, sinDescripcion, muchasFilas, tarifaNegativa, costoNegativo, margenNegativo })
        {
            clsValidadorCotizacionPdfPayload.Validar(payload)!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        }

        clsValidadorCotizacionPdfPayload.Validar(null)!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
    }

    [Theory]
    [InlineData("{\"linea_negocio\": \"Nacional\", ")]
    [InlineData("{\"tarifa\": {\"filas\": \"no es una lista\"}}")]
    [InlineData("{\"tarifa\": {\"filas\": [{\"tarifa\": \"tres millones\"}]}}")]
    [InlineData("{\"cliente\": \"\\uD800 suelto\"}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("   ")]
    public void UnJsonInvalidoEsPayloadInvalidoSinLanzar(string json)
    {
        var resultado = clsCotizacionPdfPayload.DesdeJson(json);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
    }

    [Fact]
    public void LasPalabrasLargasDeLasCeldasSePartenConEspaciosDeAnchoCero()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Tarifa!.Filas![0].Destino = new string('D', 20);
        payload.Tarifa.Filas[0].Nota = "corta " + new string('n', 61);

        var fila = Construir(payload).Secciones[0].Filas[0];

        fila.Destino.Split(clsTextoSeguro.CorteInvisible).Select(p => p.Length).Should().Equal(8, 8, 4);
        fila.Nota.Should().Be("corta " + new string('n', 30) + clsTextoSeguro.CorteInvisible + new string('n', 30) + clsTextoSeguro.CorteInvisible + "n");
        fila.Tarifa.Should().Be("$3.000.000");
        clsTextoSeguro.PartirPalabrasLargas("palabras cortas normales").Should().Be("palabras cortas normales");
        clsTextoSeguro.Limpiar("a\u200Bb").Should().Be("ab", "un U+200B del payload se descarta antes de partir");
    }

    [Fact]
    public void LaMonedaDeLaCotizacionSeAplicaALasFilasSinMonedaPropia()
    {
        var payload = clsEjemploPdf.Payload();
        payload.ServiciosConexos = [new clsServicioConexoPayload { Descripcion = "Levante", Precio = 100m }];

        var documento = clsConstructorDocumentoCotizacion.Construir(payload, "PCN-1", _firma, _hoy, "USD", 3306.86m).Valor;

        documento.Secciones[0].Filas[0].Tarifa.Should().Be("US$3.000.000,00");
        documento.Incluye.Should().Contain("Levante: US$100,00");
    }

    // En USD la nota de TRM mínima con la TRM aplicada a 2 decimales; en COP ninguna nota.
    [Fact]
    public void EnDolaresElDocumentoLlevaLaNotaDeTrmMinima()
    {
        var documento = clsConstructorDocumentoCotizacion.Construir(clsEjemploPdf.Payload(), "PCN-1", _firma, _hoy, "USD", 3273.49m).Valor;

        documento.NotaMoneda.Should().Be(
            "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que la tarifa puede variar de acuerdo al "
            + "comportamiento de la misma a la fecha de elaboración de la CPI.");
    }

    [Theory]
    [InlineData("3306.8612", "$3.306,86")]
    [InlineData("3306.865", "$3.306,86")]
    [InlineData("3306.875", "$3.306,88")]
    [InlineData("950", "$950,00")]
    [InlineData("4000.1", "$4.000,10")]
    public void LaTrmDeLaNotaVaSiempreConDosDecimales(string trm, string esperado)
    {
        var nota = clsConstructorDocumentoCotizacion.Construir(
            clsEjemploPdf.Payload(), "PCN-1", _firma, _hoy, "USD", decimal.Parse(trm, System.Globalization.CultureInfo.InvariantCulture)).Valor.NotaMoneda;

        nota.Should().Contain($"TRM mínima de {esperado} y es de resaltar");
    }

    [Fact]
    public void LaNotaYaNoHablaDelPisoEnPesos()
    {
        var nota = clsConstructorDocumentoCotizacion.Construir(clsEjemploPdf.Payload(), "PCN-1", _firma, _hoy, "USD", 3306.86m).Valor.NotaMoneda;

        nota.Should().NotContain("mínimo a pagar").And.NotContain("valor cotizado originalmente").And.NotContain("día a día");
    }

    [Fact]
    public void EnPesosNoHayNotaDeTrm() =>
        clsConstructorDocumentoCotizacion.Construir(clsEjemploPdf.Payload(), "PCN-1", _firma, _hoy, "COP", 0m).Valor.NotaMoneda.Should().BeNull();

    // Sin TRM no hay PDF en dólares (nunca sale sin la aclaración).
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EnDolaresSinTrmEsPayloadInvalido(int trm)
    {
        var resultado = clsConstructorDocumentoCotizacion.Construir(clsEjemploPdf.Payload(), "PCN-1", _firma, _hoy, "USD", trm);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        resultado.Error.Mensaje.Should().Contain("TRM");
    }

    [Fact]
    public void LaVarianteMultiTambienLlevaLaNota()
    {
        var payload = clsEjemploPdf.Payload();
        payload.Secciones = [payload.Tarifa!];
        payload.Tarifa = null;

        clsConstructorDocumentoCotizacion.Construir(payload, "PCN-1", _firma, _hoy, "USD", 3306.86m).Valor.NotaMoneda.Should().Contain("TRM mínima de $3.306,86");
    }

    [Fact]
    public void ElJsonDelPayloadAceptaNumerosComoTextoEnLosCamposLibres()
    {
        var payload = clsCotizacionPdfPayload.DesdeJson("""
            {"linea_negocio":"Internacional","aduana":{"arancel":5,"frontera":"Ipiales"},"comercial":{"cel":3134156187},
             "tarifa":{"filas":[{"destino":"QUITO","tarifa":"1500000"}]}}
            """).Valor;

        payload.Aduana!.Arancel.Should().Be("5");
        payload.Comercial!.Cel.Should().Be("3134156187");
        payload.Tarifa!.Filas![0].Tarifa.Should().Be(1_500_000m);
    }
}
