using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// La huella de <c>POST /web/licitaciones</c> con <c>Idempotency-Key</c>
/// (<see cref="clsLicitacionService.Huella"/>): el cuerpo más el nombre y el SHA-256 del archivo, nunca los bytes. La misma clave con la
/// misma huella repite la respuesta; con otra huella es <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (el flujo completo se prueba en los web tests).
/// </summary>
public sealed class clsLicitacionIdempotenteTests
{
    private static clsEntradaLicitacion Entrada(clsArchivoSubido? archivo = null, string nit = "900555111", string? moneda = null, string? origen = null) =>
        new(nit, "Cliente", "Nacional", new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion }, archivo is null ? null : "Tarifas",
            [new clsLineaLicitacionEntrada(0, new clsLineaSolicitudCosteo { Origen = "YUMBO", Destino = "BOGOTA, D.C.", PesoKg = 9000m })],
            archivo, moneda, origen);

    [Fact]
    public void MismoCuerpoYMismoArchivoDanLaMismaHuellaAunqueElArregloDeBytesSeaOtro()
    {
        var a = Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3, 4]));
        var b = Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3, 4]));

        clsLicitacionService.Huella(a).Should().Be(clsLicitacionService.Huella(b));
    }

    [Fact]
    public void OtroContenidoDelArchivoCambiaLaHuella() =>
        clsLicitacionService.Huella(Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3, 4])))
            .Should().NotBe(clsLicitacionService.Huella(Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3, 9]))));

    [Fact]
    public void OtroNombreDelArchivoCambiaLaHuella() =>
        clsLicitacionService.Huella(Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3])))
            .Should().NotBe(clsLicitacionService.Huella(Entrada(new clsArchivoSubido("otro.xlsx", [1, 2, 3]))));

    [Fact]
    public void OtroCuerpoCambiaLaHuella()
    {
        var archivo = new clsArchivoSubido("pliego.xlsx", [1, 2, 3]);
        clsLicitacionService.Huella(Entrada(archivo, nit: "900555111")).Should().NotBe(clsLicitacionService.Huella(Entrada(archivo, nit: "901000000")));
        clsLicitacionService.Huella(Entrada(archivo, moneda: "COP")).Should().NotBe(clsLicitacionService.Huella(Entrada(archivo, moneda: "USD")));
        clsLicitacionService.Huella(Entrada(archivo, origen: clsOrigenLectura.AnalizarExcel))
            .Should().NotBe(clsLicitacionService.Huella(Entrada(archivo, origen: clsOrigenLectura.LeerEstructura)));
    }

    [Fact]
    public void ConArchivoYSinArchivoDanHuellasDistintas() =>
        clsLicitacionService.Huella(Entrada(new clsArchivoSubido("pliego.xlsx", [1, 2, 3])))
            .Should().NotBe(clsLicitacionService.Huella(Entrada(archivo: null)));
}
