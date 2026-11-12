using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-044.10 (#293, parte 1): <c>origen_lectura</c> en la vista de la licitación (<see cref="clsLicitacionService.Vista"/>): <c>null</c> sin
/// archivo, <c>leer_estructura</c> si lo leyó la IA de NEXO y <c>analizar_excel</c> si el archivo no guardó el valor (anterior al script 22).
/// </summary>
public sealed class clsLicitacionLeidaConIaTests
{
    private static clsCotizacionSolicitud UnaLicitacion()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-293-20261111-001" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", TipoVehiculo = "3S3", PesoKg = 10_000m }], [0], 3);
        return cotizacion;
    }

    private static clsArchivoLicitacionResumen Resumen(string? origenLectura) => new("pliego.xlsx", "Tarifas", 1024, origenLectura);

    [Fact]
    public void SinArchivoElOrigenLecturaEsNull()
    {
        clsLicitacionService.OrigenLecturaVista(null).Should().BeNull();
        clsLicitacionService.Vista(UnaLicitacion(), null).OrigenLectura.Should().BeNull();
    }

    [Theory]
    [InlineData(clsOrigenLectura.LeerEstructura, clsOrigenLectura.LeerEstructura)]
    [InlineData(clsOrigenLectura.AnalizarExcel, clsOrigenLectura.AnalizarExcel)]
    // Anterior al script 22: la columna llegaba null; se trata como analizar_excel.
    [InlineData(null, clsOrigenLectura.AnalizarExcel)]
    [InlineData("", clsOrigenLectura.AnalizarExcel)]
    public void ConArchivoElOrigenLecturaEsElGuardadoOAnalizarExcelSiEsNull(string? guardado, string esperado)
    {
        clsLicitacionService.OrigenLecturaVista(Resumen(guardado)).Should().Be(esperado);
        clsLicitacionService.Vista(UnaLicitacion(), Resumen(guardado)).OrigenLectura.Should().Be(esperado);
    }
}
