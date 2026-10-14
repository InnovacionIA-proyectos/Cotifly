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

    private static string TextoPdf(params string[] conexos)
    {
        var payload = clsDocumentosPrueba.Payload();
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
