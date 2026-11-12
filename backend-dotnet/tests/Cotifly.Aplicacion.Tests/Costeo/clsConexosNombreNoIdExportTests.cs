using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Corrección conexos (nombre nunca id), rutas de EXPORTACIÓN: el PDF, el Excel de la cotización y el export de licitación resuelven el
/// nombre del catálogo de Nexo para un conexo HEREDADO (con el nombre en NULL en la base) cuando el catálogo está caliente (diccionario de
/// nombres), caen a la categoría como respaldo LEGIBLE con el catálogo frío, y nunca exponen el id interno. Misma cadena que la vista web y
/// el asistente de IA.
/// </summary>
public sealed class clsConexosNombreNoIdExportTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    private const string IdConexo = "CX-ID-INTERNO-1";

    private static clsCotizacionSolicitud ConConexoSinNombre() =>
        clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m), IdConexo, nombre: null, costoUnitario: 85_000m, ventaUnitaria: 100_000m);

    private static IReadOnlyDictionary<string, string> CatalogoCaliente(string nombre) =>
        new Dictionary<string, string> { [IdConexo] = nombre };

    // ---- PDF ----

    [Fact]
    public void PdfConCatalogoCalienteImprimeElNombreDelCatalogoNuncaElId()
    {
        var cotizacion = ConConexoSinNombre();

        var payload = clsCosteoCotizacionService.Payload(
            cotizacion, lineaNegocio: null, moneda: "COP", catalogo: null, nombresConexos: CatalogoCaliente("Cargue de contenedor"));

        payload.ServiciosConexos.Should().ContainSingle().Which.Descripcion.Should().Be("Cargue de contenedor");
        payload.ServiciosConexos!.Should().OnlyContain(c => c.Descripcion != IdConexo);
    }

    [Fact]
    public void PdfConCatalogoFrioCaeALaCategoriaNuncaAlId()
    {
        var cotizacion = ConConexoSinNombre();

        var payload = clsCosteoCotizacionService.Payload(cotizacion, lineaNegocio: null, moneda: "COP");

        var descripcion = payload.ServiciosConexos.Should().ContainSingle().Subject.Descripcion;
        descripcion.Should().Be(clsCosteoCotizacionService.CategoriaConexo);
        descripcion.Should().NotBe(IdConexo);
    }

    // ---- Excel de la cotización ----

    [Fact]
    public void ExcelConCatalogoCalienteImprimeElNombreDelCatalogoNuncaElId()
    {
        var cotizacion = ConConexoSinNombre();

        var datos = clsExportacionCotizacionService.Datos(cotizacion, comercial: null, _ahora, nombresConexos: CatalogoCaliente("Precinto"));

        var conexos = datos.Rutas.SelectMany(r => r.Conexos).ToList();
        conexos.Should().ContainSingle().Which.Nombre.Should().Be("Precinto");
        conexos.Should().OnlyContain(c => c.Nombre != IdConexo);
    }

    [Fact]
    public void ExcelConCatalogoFrioCaeALaCategoriaNuncaAlId()
    {
        var cotizacion = ConConexoSinNombre();

        var datos = clsExportacionCotizacionService.Datos(cotizacion, comercial: null, _ahora);

        var conexo = datos.Rutas.SelectMany(r => r.Conexos).Should().ContainSingle().Subject;
        conexo.Nombre.Should().Be(clsCosteoCotizacionService.CategoriaConexo);
        conexo.Nombre.Should().NotBe(IdConexo);
    }

    // ---- Export de licitación ----

    [Fact]
    public void LicitacionConCatalogoCalienteImprimeElNombreDelCatalogoNuncaElId()
    {
        var cotizacion = ConConexoSinNombre();

        var datos = clsLicitacionService.DatosExportacion(cotizacion, archivo: null, _ahora, nombresConexos: CatalogoCaliente("Vigilancia"));

        datos.ServiciosConexos.Should().ContainSingle().Which.Nombre.Should().Be("Vigilancia");
        datos.ServiciosConexos.Should().OnlyContain(c => c.Nombre != IdConexo);
    }

    [Fact]
    public void LicitacionConCatalogoFrioCaeALaCategoriaNuncaAlId()
    {
        var cotizacion = ConConexoSinNombre();

        var datos = clsLicitacionService.DatosExportacion(cotizacion, archivo: null, _ahora);

        var conexo = datos.ServiciosConexos.Should().ContainSingle().Subject;
        conexo.Nombre.Should().Be(clsCosteoCotizacionService.CategoriaConexo);
        conexo.Nombre.Should().NotBe(IdConexo);
    }
}
