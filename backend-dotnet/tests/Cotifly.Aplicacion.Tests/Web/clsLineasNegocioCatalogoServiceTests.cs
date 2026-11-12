using Cotifly.Aplicacion.Administracion.DTOs;
using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Web;

/// <summary>
/// Catálogo de líneas de negocio activas para el desplegable de la captura, sin la
/// política de margen.
/// </summary>
public sealed class clsLineasNegocioCatalogoServiceTests
{
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsMargenPolitica Linea(string nombre, bool activa = true, decimal margen = 0.15m)
    {
        var linea = clsMargenPolitica.Crear(nombre, margen).Valor;
        if (!activa)
        {
            linea.Desactivar();
        }

        return linea;
    }

    [Fact]
    public async Task ListaSoloLasActivasOrdenadasConSuEtiqueta()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Linea("Nacional"), Linea("DevolucionContenedor", margen: 0.3m), Linea("Licitaciones", activa: false), Linea("Importacion"),
            Linea("Distribucion"), Linea("Exportacion"), Linea("Internacional"),
        ]);

        var lineas = await new clsLineasNegocioCatalogoService(_politicas).ListarActivasAsync(Ct);

        lineas.Should().Equal(
            new clsLineaNegocioOpcion("DevolucionContenedor", "Devolución Contenedor"),
            new clsLineaNegocioOpcion("Distribucion", "Distribución"),
            new clsLineaNegocioOpcion("Exportacion", "Exportación"),
            new clsLineaNegocioOpcion("Importacion", "Importación"),
            new clsLineaNegocioOpcion("Internacional", "Internacional"),
            new clsLineaNegocioOpcion("Nacional", "Nacional"));
    }

    [Fact]
    public void LaOpcionNoTieneMargenNiMetodo() =>
        typeof(clsLineaNegocioOpcion).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["LineaNegocio", "Etiqueta"],
            "la política de margen no sale de la administración (decisión P4b)");

    [Theory]
    [InlineData("LineaNueva", "LineaNueva")]
    [InlineData("  Nacional  ", "Nacional")]
    [InlineData("nacional", "Nacional")]
    [InlineData("devolucioncontenedor", "Devolución Contenedor")]
    public void LaEtiquetaEsLaConocidaOElMismoNombre(string nombre, string etiqueta) =>
        clsLineaNegocioOpcion.EtiquetaDe(nombre).Should().Be(etiqueta);

    [Fact]
    public async Task SinLineasActivasLaListaVaVacia()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([Linea("Nacional", activa: false)]);

        var lineas = await new clsLineasNegocioCatalogoService(_politicas).ListarActivasAsync(Ct);

        lineas.Should().BeEmpty();
    }
}
