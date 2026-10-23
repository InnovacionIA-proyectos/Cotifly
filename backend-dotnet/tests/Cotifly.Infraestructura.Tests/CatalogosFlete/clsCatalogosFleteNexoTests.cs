using System.Net;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.CatalogosFlete;

/// <summary>
/// HU-013.10 (#106, G1): <see cref="clsCatalogosMaestrosNexo"/> consume los catálogos maestros de Nexo
/// (<c>GET /api/catalogos/tipos-vehiculo</c>, <c>clases-viaje</c>, <c>clases-contenedor</c> y <c>lineas-negocio</c>) contra el Nexo
/// falso con estado (<see cref="clsNexoFalso"/>), y <see cref="clsCatalogosFleteNexo"/> arma la respuesta de
/// <c>GET /web/catalogos-flete</c> con ellos y la carrocería en código. Nunca sale a la red.
/// </summary>
public sealed class clsCatalogosFleteNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private readonly clsNexoFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCatalogosFleteNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    [Fact]
    public async Task ConsultaLosCuatroCatalogosMaestrosConElIdExternoYNoElHistoricoDeTarifas()
    {
        await Maestros().ObtenerAsync(_usuarioId, Ct);

        _nexo.Peticiones.Select(p => p.Url.AbsolutePath).Should().BeEquivalentTo(
            "/api/catalogos/tipos-vehiculo", "/api/catalogos/clases-viaje", "/api/catalogos/clases-contenedor", "/api/catalogos/lineas-negocio");
        _nexo.Peticiones.Should().OnlyContain(p => p.Metodo == HttpMethod.Get && p.IdPortalUsuario == "1007" && p.Accept == "application/json");
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.Contains("biblioteca-sap-historico", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MapeaElCodigoDelTipoDeVehiculoYLaDescripcionDeLaClaseDeViaje()
    {
        var resultado = await Maestros().ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TiposVehiculo.Should().ContainEquivalentOf(new clsOpcionCatalogo("3S3", "Tractocamión 3 ejes, semi 3"));
        resultado.Valor.ClasesViaje.Select(c => c.Valor).Should().Equal("Contenedor", "Cupo", "Carga Suelta", "Extradimensionado");
        resultado.Valor.ClasesContenedor.Select(c => c.Valor).Should().Contain("CNT40.HC");
        resultado.Valor.LineasNegocio.Select(c => c.Valor).Should().Contain("PuertoAInterior");
    }

    [Fact]
    public async Task DescartaTiposDeVehiculoSinCodigoOSinDescripcionYRepetidos()
    {
        _nexo.TiposVehiculo.Clear();
        _nexo.TiposVehiculo.AddRange([(null, "Especial sin código", false), ("V2", null, true), ("2S3", "Tracto", true), ("2S3", "Repetido", true)]);

        var resultado = await Maestros().ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.TiposVehiculo.Should().ContainSingle().Which.Should().Be(new clsOpcionCatalogo("2S3", "Tracto"));
    }

    [Theory]
    [InlineData("tipos-vehiculo")]
    [InlineData("clases-viaje")]
    public async Task SiFallaUnCatalogoObligatorioNoHayCatalogos(string catalogo)
    {
        _nexo.CatalogosFallidos.Add(catalogo);

        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SiFallanLosCatalogosOpcionalesQuedanVaciosConUnWarning()
    {
        _nexo.CatalogosFallidos.UnionWith(["clases-contenedor", "lineas-negocio"]);
        var logger = new clsLoggerFalso<clsCatalogosMaestrosNexo>();

        var resultado = await new clsCatalogosMaestrosNexo(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoFalso.UrlBase }), _usuarios, logger)
            .ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TiposVehiculo.Should().NotBeEmpty();
        resultado.Valor.ClasesContenedor.Should().BeEmpty();
        resultado.Valor.LineasNegocio.Should().BeEmpty();
        resultado.Valor.ClasesContenedorNoDisponible.Should().BeTrue("falló: la caché puede conservar la copia anterior");
        resultado.Valor.LineasNegocioNoDisponible.Should().BeTrue();
        logger.Registros.Should().Contain(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Mensaje.Contains("opcional", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnaListaVaciaRealDeNexoNoSeMarcaComoFallo()
    {
        // Hallazgo M de #179: [] (base de SAP sin configurar) es una respuesta legítima, no un fallo pasajero.
        _nexo.ClasesContenedor.Clear();
        _nexo.LineasNegocio.Clear();

        var resultado = await Maestros().ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.ClasesContenedor.Should().BeEmpty();
        resultado.Valor.ClasesContenedorNoDisponible.Should().BeFalse();
        resultado.Valor.LineasNegocioNoDisponible.Should().BeFalse();
    }

    [Fact]
    public async Task ConLaCacheUnFalloPasajeroDeClasesDeContenedorConservaLasDeLaCopiaAnterior()
    {
        // Hallazgo M de #179, de punta a punta con el Nexo falso: la copia nueva (vigente 30 min) no deja el selector vacío.
        var reloj = new clsRelojFalso();
        var cache = new clsCacheCatalogosMaestros();
        clsCatalogosMaestrosEnCache Proveedor() => new(
            Maestros(), cache, reloj, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoFalso.UrlBase }),
            NullLogger<clsCatalogosMaestrosEnCache>.Instance);

        await Proveedor().ObtenerAsync(_usuarioId, Ct);
        _nexo.CatalogosFallidos.Add("clases-contenedor");
        reloj.Avanzar(TimeSpan.FromMinutes(31));

        var resultado = await new clsCatalogosFleteNexo(Proveedor()).ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.ClasesContenedor.Select(c => c.Valor).Should().Contain("CNT40.HC");
    }

    [Fact]
    public async Task LosTiposDeVehiculoNoOficialesNoSeOfrecenYSinOficialesNoHayCatalogo()
    {
        _nexo.TiposVehiculo.Add(("ESP", "Especial no oficial", false));
        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Valor.TiposVehiculo.Select(t => t.Valor).Should().NotContain("ESP");

        _nexo.TiposVehiculo.Clear();
        _nexo.TiposVehiculo.Add(("ESP", "Especial no oficial", false));
        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task UnaRespuestaDeMasDeCincoMilFilasODeMasDeUnMegaEsNoDisponible()
    {
        _nexo.CuerpoTiposVehiculo = "[" + string.Join(",", Enumerable.Range(0, clsCatalogosMaestrosNexo.MaximoFilas + 1).Select(i => $$"""{"codigo":"C{{i}}","descripcion":"D"}""")) + "]";
        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);

        _nexo.CuerpoTiposVehiculo = $$"""[{"codigo":"2S3","descripcion":"{{new string('x', clsCatalogosMaestrosNexo.MaximoBytes)}}"}]""";
        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SiUnCatalogoFallaElResultadoEsCatalogosFleteNoDisponible()
    {
        _nexo.CatalogosCaidos = HttpStatusCode.Unauthorized;

        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SinUrlBaseConfiguradaFallaSinLlamarANexo()
    {
        var resultado = await Maestros(url: null).ObtenerAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElUsuarioSinIdUsuarioExternoFallaSinLlamarANexo()
    {
        UsuarioResuelto(UsuarioConIdExterno(null));

        (await Maestros().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task UnErrorDeRedEsCatalogosFleteNoDisponibleConUnWarningSinTraza()
    {
        var falso = new clsNexoClientesFalso();
        for (var i = 0; i < 4; i++)
        {
            falso.Fallar(new HttpRequestException("Connection refused"));
        }

        using var http = new HttpClient(falso);
        var logger = new clsLoggerFalso<clsCatalogosMaestrosNexo>();
        var proveedor = new clsCatalogosMaestrosNexo(http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios, logger);

        (await proveedor.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        logger.Registros.Should().OnlyContain(r => r.Excepcion == null || r.Nivel == Microsoft.Extensions.Logging.LogLevel.Debug);
        logger.Registros.Should().Contain(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeNexo()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var consultar = () => Maestros().ObtenerAsync(_usuarioId, cancelacion.Token);

        await consultar.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LosCatalogosDeFleteUsanLosMaestrosYLaCarroceriaEnCodigo()
    {
        var maestros = Substitute.For<IClsCatalogosMaestrosProvider>();
        maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsCatalogosMaestros>.Exito(new clsCatalogosMaestros(
            [new("3S3", "Tractocamión")], [new("Carga Suelta", "Carga Suelta")], [new("CNT40.HC", "CNT40.HC")], [])));

        var resultado = await new clsCatalogosFleteNexo(maestros).ObtenerAsync(_usuarioId, Ct);

        resultado.Valor.TiposVehiculo.Should().Equal(new clsOpcionCatalogo("3S3", "Tractocamión"));
        resultado.Valor.ClasesViaje.Should().Equal(new clsOpcionCatalogo("Carga Suelta", "Carga Suelta"));
        resultado.Valor.ClasesContenedor.Should().Equal(new clsOpcionCatalogo("CNT40.HC", "CNT40.HC"));
        resultado.Valor.TiposCarroceria.Select(c => c.Valor).Should().Contain(["FURGON", "PORTACONTENEDORES", "VOLCO"]).And.HaveCount(9);
    }

    [Fact]
    public async Task LosCatalogosDeFleteExponenSoloLasClasesDeContenedorDeNexoConLaDescripcionComoValor()
    {
        // HU-027.1 (#179): en Nexo la descripcion de clases-contenedor es el código; el valor expuesto es el que valida la captura.
        var resultado = await new clsCatalogosFleteNexo(Maestros()).ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClasesContenedor.Should().Equal(
            _nexo.ClasesContenedor.Select(d => new clsOpcionCatalogo(d, d)));
        resultado.Valor.ClasesContenedor.Select(c => c.Valor).Should().Equal("CNT20.IT", "CNT20.RE", "CNT20.ST", "CNT40.HC", "CNT40.RE")
            .And.NotContain(clsCatalogosCaptura.ClasesContenedorProvisionales);
    }

    [Fact]
    public async Task CadaClaseDeContenedorExpuestaLaAceptaLaValidacionDeLaCaptura()
    {
        // HU-027.1 (#179): el valor que ofrece GET /web/catalogos-flete es el mismo con que se valida clase_contenedor.
        var maestros = Maestros();
        var validador = new clsValidadorCatalogosCaptura(maestros, NullLogger<clsValidadorCatalogosCaptura>.Instance);
        var clases = (await new clsCatalogosFleteNexo(maestros).ObtenerAsync(_usuarioId, Ct)).Valor.ClasesContenedor;

        clases.Should().NotBeEmpty();
        foreach (var clase in clases)
        {
            var linea = clsSolicitudCosteoMuestra.Crear().ALinea() with { ClaseViaje = clsCatalogosCaptura.ClaseViajeContenedor, ClaseContenedor = clase.Valor };
            (await validador.ValidarAsync(_usuarioId, linea, Ct)).Should().BeNull(clase.Valor);
        }
    }

    [Fact]
    public async Task SiNexoDevuelveLasClasesDeContenedorVaciasLosCatalogosDeFleteSiguenConLaListaVacia()
    {
        // Nexo responde [] cuando su base de SAP no está configurada.
        _nexo.ClasesContenedor.Clear();

        var resultado = await new clsCatalogosFleteNexo(Maestros()).ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClasesContenedor.Should().BeEmpty();
        resultado.Valor.ClasesViaje.Should().NotBeEmpty();
        resultado.Valor.TiposVehiculo.Should().NotBeEmpty();
    }

    [Fact]
    public async Task SiFallaElCatalogoDeClasesDeContenedorLosCatalogosDeFleteSiguenConLaListaVacia()
    {
        _nexo.CatalogosFallidos.Add("clases-contenedor");

        var resultado = await new clsCatalogosFleteNexo(Maestros()).ObtenerAsync(_usuarioId, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.ClasesContenedor.Should().BeEmpty();
    }

    [Fact]
    public async Task SinMaestrosLosCatalogosDeFleteNoOfrecenTextoLibre()
    {
        var maestros = Substitute.For<IClsCatalogosMaestrosProvider>();
        maestros.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsCatalogosMaestros>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "x")));

        (await new clsCatalogosFleteNexo(maestros).ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    private void UsuarioResuelto(clsUsuarioWeb? usuario) =>
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(usuario);

    private static clsUsuarioWeb UsuarioConIdExterno(long? idUsuarioExterno) =>
        clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, idUsuarioExterno).Valor;

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }

    private clsCatalogosMaestrosNexo Maestros(string? url = clsNexoFalso.UrlBase) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, NullLogger<clsCatalogosMaestrosNexo>.Instance);
}
