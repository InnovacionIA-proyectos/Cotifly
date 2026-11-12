using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Corrección conexos (nombre nunca id): lo que el asistente de IA le muestra al comercial sobre los servicios conexos siempre es el nombre
/// (el guardado, el del catálogo de Nexo si el guardado es NULL, o la categoría como respaldo LEGIBLE), nunca el id interno del conexo. La IA
/// sigue sin recibir montos.
/// </summary>
public sealed class clsAsistenteConexosNombreNoIdTests
{
    private const string IdConexo = "CX-ID-INTERNO-1";

    private readonly clsContextoAsistenteFolio _contexto = new();
    private readonly IClsCategoriasConexos _categorias = Substitute.For<IClsCategoriasConexos>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsHerramientasAsistenteFolio Herramientas(clsCotizacionSolicitud cotizacion)
    {
        _contexto.Iniciar(Guid.NewGuid(), cotizacion, "Importacion", "web/s1/" + cotizacion.Folio);
        return new clsHerramientasAsistenteFolio(
            _contexto, Substitute.For<IClsServiciosConexosCosteoService>(), Substitute.For<IClsReSolicitudCosteoService>(), _categorias);
    }

    private static clsCotizacionSolicitud ConConexoSinNombre() =>
        clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m), IdConexo, nombre: null, costoUnitario: 85_000m, ventaUnitaria: 100_000m);

    private static IEnumerable<string?> NombresDeConexos(clsResultadoHerramienta resultado)
    {
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        var conexos = (IEnumerable<object?>)salida["conexos"]!;
        var nombresPlano = conexos.Cast<IReadOnlyDictionary<string, object?>>().Select(c => c["nombre"] as string);

        var rutas = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)salida["rutas"]!;
        var nombresRuta = rutas.SelectMany(r => (IEnumerable<object?>)r["conexos"]!)
            .Cast<IReadOnlyDictionary<string, object?>>().Select(c => c["nombre"] as string);

        return nombresPlano.Concat(nombresRuta);
    }

    [Fact]
    public async Task ConNombreNullElAsistenteMuestraElNombreDelCatalogoNuncaElId()
    {
        _categorias.NombresAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string> { [IdConexo] = "Cargue de contenedor" });
        var herramientas = Herramientas(ConConexoSinNombre());

        var resultado = await herramientas.ConsultarCotizacionAsync(Ct);

        resultado.EsExitoso.Should().BeTrue();
        var nombres = NombresDeConexos(resultado).ToList();
        nombres.Should().NotBeEmpty().And.OnlyContain(n => n == "Cargue de contenedor");
        nombres.Should().NotContain(IdConexo);
    }

    [Fact]
    public async Task ConNombreNullYSinCatalogoElAsistenteCaeALaCategoriaNuncaAlId()
    {
        _categorias.NombresAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
        var herramientas = Herramientas(ConConexoSinNombre());

        var resultado = await herramientas.ConsultarCotizacionAsync(Ct);

        var nombres = NombresDeConexos(resultado).ToList();
        nombres.Should().NotBeEmpty().And.OnlyContain(n => n == clsCosteoCotizacionService.CategoriaConexo);
        nombres.Should().NotContain(IdConexo);
    }

    [Fact]
    public async Task ConNombreGuardadoElAsistenteLoUsaTalCual()
    {
        _categorias.NombresAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>());
        var cotizacion = clsCotizacionMuestra.ConConexo(
            clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m), IdConexo, nombre: "Vigilancia", costoUnitario: 85_000m, ventaUnitaria: 100_000m);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ConsultarCotizacionAsync(Ct);

        NombresDeConexos(resultado).Should().NotBeEmpty().And.OnlyContain(n => n == "Vigilancia");
    }
}
