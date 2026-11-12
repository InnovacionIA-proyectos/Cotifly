using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Corrección conexos (nombre nunca id): un conexo con el nombre en NULL en la base (fila migrada del JSON) se expone en la vista del costeo
/// con el nombre resuelto del catálogo de Nexo o, si no se conoce, con la categoría como respaldo LEGIBLE. El id interno del conexo nunca
/// aparece como etiqueta visible (sigue en <see cref="clsServicioConexoPrecio.Id"/> para operar: incluir/excluir).
/// </summary>
public sealed class clsConexosNombreNoIdTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    private const string IdConexo = "CX-ID-INTERNO-1";

    private static clsCotizacionSolicitud ConConexoSinNombre() =>
        clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m), IdConexo, nombre: null, costoUnitario: 85_000m, ventaUnitaria: 100_000m);

    [Fact]
    public void ConNombreNullElCatalogoResuelveElNombreYNuncaSaleElId()
    {
        var cotizacion = ConConexoSinNombre();
        var ruta = cotizacion.PrimeraRuta!;

        var conexo = clsCosteoCotizacionService
            .ConexosConPrecio(cotizacion, ruta, categorias: null, nombres: new Dictionary<string, string> { [IdConexo] = "Cargue de contenedor" })
            .Should().ContainSingle().Subject;

        conexo.Nombre.Should().Be("Cargue de contenedor");
        conexo.Id.Should().Be(IdConexo, "el id interno sigue disponible para operar, pero no como etiqueta");
        conexo.Nombre.Should().NotBe(IdConexo);
    }

    [Fact]
    public void ConNombreNullYSinCatalogoCaeALaCategoriaNuncaAlId()
    {
        var cotizacion = ConConexoSinNombre();
        var ruta = cotizacion.PrimeraRuta!;

        var conexo = clsCosteoCotizacionService
            .ConexosConPrecio(cotizacion, ruta, categorias: new Dictionary<string, string> { [IdConexo] = "adicional" }, nombres: null)
            .Should().ContainSingle().Subject;

        conexo.Nombre.Should().Be("adicional");
        conexo.Nombre.Should().NotBe(IdConexo);
    }

    [Fact]
    public void ConNombreNullYSinCatalogoNiCategoriaUsaLaCategoriaDeRespaldoNuncaElId()
    {
        var cotizacion = ConConexoSinNombre();
        var ruta = cotizacion.PrimeraRuta!;

        var conexo = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Should().ContainSingle().Subject;

        conexo.Nombre.Should().Be(clsCosteoCotizacionService.CategoriaConexo);
        conexo.Nombre.Should().NotBe(IdConexo);
    }

    [Fact]
    public void LaVistaWebDelCosteoNuncaExponeElIdComoNombreDelConexo()
    {
        var cotizacion = ConConexoSinNombre();

        var vista = clsCosteoCotizacionService.Vista(
            cotizacion, baseUrlPdf: null, nombres: new Dictionary<string, string> { [IdConexo] = "Precinto" });

        var conexos = vista.Rutas.SelectMany(r => r.ServiciosConexos).ToList();
        conexos.Should().ContainSingle();
        conexos[0].Nombre.Should().Be("Precinto");
        conexos.Should().OnlyContain(c => c.Nombre != IdConexo, "el id interno nunca es la etiqueta visible");
        // La lista plana de ServiciosConexos (compatibilidad) también.
        vista.ServiciosConexos.Should().OnlyContain(c => c.Nombre != IdConexo);
    }

    [Fact]
    public void ElNombreEnMemoriaSaleDelCatalogoPorElIdGuardadoSinLeerNexo()
    {
        var cotizacion = ConConexoSinNombre();
        var almacen = new clsAlmacenCategoriasConexos();
        almacen.Guardar(new clsIndiceCategorias(
            new Dictionary<string, string>(), new Dictionary<string, string>(), _ahora,
            NombresPorId: new Dictionary<string, string> { [IdConexo] = "Cargue" }));

        clsCategoriasConexosService.NombresEnMemoria(almacen, cotizacion)
            .Should().ContainKey(IdConexo).WhoseValue.Should().Be("Cargue");
        almacen.UltimaLectura.Should().BeSameAs(Task.CompletedTask, "el detalle no dispara la lectura del catálogo en segundo plano");
    }

    [Fact]
    public void UnConexoConNombreGuardadoNoNecesitaElCatalogoNiCaeAlId()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m), IdConexo, nombre: "Vigilancia", costoUnitario: 85_000m, ventaUnitaria: 100_000m);

        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, cotizacion.PrimeraRuta!)
            .Should().ContainSingle().Which.Nombre.Should().Be("Vigilancia");
    }
}
