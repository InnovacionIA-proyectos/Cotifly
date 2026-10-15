using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Pdf;

/// <summary>
/// #173 (E2E real del 2026-10-14): el PDF decía "Estas tarifas no incluyen: Acompañamiento vehicular" mientras itemizaba el conexo
/// "Acompañante vehicular en moto". El "no incluye" se depura contra los conexos reales. Render real (MigraDoc) leído con PdfPig.
/// </summary>
[Collection(clsColeccionRenderPdf.Nombre)]
public sealed partial class clsPdfIncluyeConexosTests
{
    private static readonly clsGeneradorPdfMigraDoc _generador = new();

    private static string TextoPdf(params string[] conexos) => TextoPdf(null, null, conexos);

    private static string TextoPdf(decimal? horasCargue, decimal? horasDescargue, params string[] conexos)
    {
        var payload = clsDocumentosPrueba.Payload();
        payload.HorasCargue = horasCargue;
        payload.HorasDescargue = horasDescargue;
        payload.ServiciosConexos = [.. conexos.Select(c => new clsServicioConexoPayload { Descripcion = c, Precio = 219_512m, Moneda = "COP" })];
        var documento = clsDocumentosPrueba.Documento(payload);
        return Espacios().Replace(clsLectorPdf.Leer(_generador.Generar(documento).Contenido).Texto, " ");
    }

    [Fact]
    public void ConElAcompananteVehicularIncluidoElPdfNoDiceQueNoLoIncluye()
    {
        var texto = TextoPdf("Acompañante vehicular en moto (AVP_MOTO)");

        texto.Should().Contain("Acompañante vehicular en moto (AVP_MOTO)", "el conexo va itemizado en lo que incluye");
        texto.Should().Contain(clsTextosEstandar.TituloNoIncluye);
        texto.Should().NotContain("Acompañamiento vehicular", "ya no aparece en lo que no incluye");
        texto.Should().Contain("Otros costos ajenos a la operación").And.Contain("Cargues y descargues",
            "lo que no cubre ningún conexo sigue en la lista");
    }

    [Fact]
    public void SinConexosElPdfConservaElNoIncluyeEstandar()
    {
        TextoPdf().Should().Contain("Acompañamiento vehicular");
    }

    /// <summary>#173 (E2E real): la captura pidió 2 h y el PDF decía 4 h fijas.</summary>
    [Fact]
    public void LaNotaUsaLasHorasCapturadas()
    {
        var texto = TextoPdf(2m, 2m);

        texto.Should().Contain("Se basan en tiempos logísticos de máximo 2 horas para cada labor.");
        texto.Should().NotContain("4 horas");
    }

    [Fact]
    public void ConHorasDistintasYElCargueIncluidoLaNotaLoDiceAsi()
    {
        var texto = TextoPdf(2m, 6.5m, "Cargue en puerto");

        texto.Should().Contain("Las tarifas cotizadas no incluyen el servicio de descargue.");
        texto.Should().Contain("máximo 2 horas para el cargue y 6,5 horas para el descargue.");
        texto.Should().NotContain("no incluyen servicios de cargue ni descargue");
    }

    [Fact]
    public void SinHorasNiConexosLaNotaEsLaDeSiempre() =>
        TextoPdf().Should().Contain("no incluyen servicios de cargue ni descargue. Se basan en tiempos logísticos de máximo 4 horas para cada labor.");

    [Fact]
    public void UnConexoQueCubreSoloParteDeUnTextoNoLoQuita()
    {
        clsConstructorDocumentoCotizacion.SinLoIncluido(
                ["Acompañamiento vehicular", "Cargues y descargues", "Otros costos ajenos a la operación"],
                [new clsServicioConexoPayload { Descripcion = "Cargue en puerto" }])
            .Should().Equal("Acompañamiento vehicular", "Cargues y descargues", "Otros costos ajenos a la operación");

        clsConstructorDocumentoCotizacion.SinLoIncluido(
                ["Acompañamiento vehicular", "Cargues y descargues"],
                [new clsServicioConexoPayload { Descripcion = "ACOMPAÑAMIENTO VEHICULAR" }, new clsServicioConexoPayload { Descripcion = "Cargue y descargue" }])
            .Should().BeEmpty();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacios();
}

/// <summary>
/// #173: las pruebas que ocupan o miden el turno de render global (<c>clsGeneradorPdfMigraDoc.TurnoDeRender</c>) no corren en paralelo entre
/// sí: un render de otra clase podía tomar el turno justo cuando la prueba de espera acotada lo necesitaba.
/// </summary>
[CollectionDefinition(Nombre)]
public sealed class clsColeccionRenderPdf
{
    public const string Nombre = "RenderPdf";
}
