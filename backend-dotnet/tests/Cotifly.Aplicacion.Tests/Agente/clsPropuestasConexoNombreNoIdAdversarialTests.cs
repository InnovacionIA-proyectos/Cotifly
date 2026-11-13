using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Corrección conexos (nombre nunca id), revisión adversarial: el resumen de la propuesta <c>agregar_servicios_conexos</c>
/// (<see cref="clsPropuestasAsistente.AgregarConexo"/>) que ve el comercial nunca muestra el id interno del servicio del catálogo. El
/// nombre, si no el código, si no la categoría y, en último caso, un literal LEGIBLE; jamás el GUID.
/// </summary>
public sealed class clsPropuestasConexoNombreNoIdAdversarialTests
{
    private const string Id = "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f";

    private static clsServicioConexoCatalogo Servicio(string? nombre, string? codigo, string? categoria) =>
        new(Id, codigo, nombre, categoria, "UND", null, null, null, null, null, null);

    private static string Resumen(clsServicioConexoCatalogo servicio)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m);
        var propuesta = clsPropuestasAsistente.AgregarConexo(cotizacion, servicio, 1m, lineaOrden: null);
        propuesta.EsExitoso.Should().BeTrue(propuesta.EsFallo ? propuesta.Error.Mensaje : null);
        return propuesta.Valor.Resumen;
    }

    [Fact]
    public void ConNombreElResumenLoUsa()
    {
        var resumen = Resumen(Servicio("Cargue en puerto", "CAR-01", "logistico"));
        resumen.Should().Contain("«Cargue en puerto»").And.NotContain(Id);
    }

    [Fact]
    public void SinNombrePeroConCodigoUsaElCodigoNuncaElId()
    {
        var resumen = Resumen(Servicio(nombre: null, codigo: "CAR-01", categoria: "logistico"));
        resumen.Should().Contain("«CAR-01»").And.NotContain(Id);
    }

    // Sin nombre ni código cae a la categoría, nunca al GUID interno.
    [Fact]
    public void SinNombreNiCodigoUsaLaCategoriaNuncaElId()
    {
        var resumen = Resumen(Servicio(nombre: null, codigo: null, categoria: "adicional"));
        resumen.Should().Contain("«adicional»").And.NotContain(Id);
    }

    // Sin nombre, sin código y sin categoría: un literal legible, jamás el id.
    [Fact]
    public void SinNombreNiCodigoNiCategoriaUsaUnLiteralLegibleNuncaElId()
    {
        var resumen = Resumen(Servicio(nombre: null, codigo: null, categoria: null));
        resumen.Should().Contain("«servicio conexo»").And.NotContain(Id);
    }
}
