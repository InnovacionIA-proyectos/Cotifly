using System.Net;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.CatalogosFlete;

/// <summary>
/// HU-031.1 (#194): el diccionario de alias de Nexo (<c>GET /api/catalogos/alias</c>, NEXO #321) en vivo y con caché. Encontrado, no
/// encontrado, error y caída: nunca lanza; la caché sirve la copia con Nexo caído y no insiste durante un minuto tras un fallo.
/// </summary>
public sealed class clsAliasNexoTests : IDisposable
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private const string Diccionario = """
        [
          {"id":"00000000-0000-0000-0000-000000000001","alias":"TB","aliasNormalizado":"TB","tipoElemento":"Vehiculo","codigo":"2L2","activo":true},
          {"id":"00000000-0000-0000-0000-000000000002","alias":"Turbo viejo","aliasNormalizado":"TURBO VIEJO","tipoElemento":"Vehiculo","codigo":"2L2","activo":false},
          {"id":"00000000-0000-0000-0000-000000000003","alias":"40 HC","aliasNormalizado":"40 HC","tipoElemento":"Contenedor","codigo":"CNT40.HC","activo":true},
          {"id":"00000000-0000-0000-0000-000000000004","alias":"C2","aliasNormalizado":"C2","tipoElemento":"Vehiculo","codigo":"2"},
          {"id":"00000000-0000-0000-0000-000000000005","alias":"raro","aliasNormalizado":"RARO","tipoElemento":"Carroceria","codigo":"X","activo":true},
          {"id":"00000000-0000-0000-0000-000000000006","alias":"sin codigo","tipoElemento":"Vehiculo","activo":true}
        ]
        """;

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsAliasNexoTests()
    {
        _http = new HttpClient(_nexo);
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LeeElDiccionarioConLaIdentidadDelComercialYSoloLasFilasActivasDeLosDosTipos()
    {
        _nexo.Responder(HttpStatusCode.OK, Diccionario);

        var resultado = await Nexo().ObtenerAsync(_usuarioId, Ct);

        var peticion = _nexo.Peticiones.Single();
        peticion.Url.AbsoluteUri.Should().Be($"{clsNexoClientesFalso.UrlBase}/api/catalogos/alias", "sin tipo: los dos tipos en una petición");
        peticion.IdPortalUsuario.Should().Be("1007");
        var diccionario = resultado.Valor;
        diccionario.Resolver(TipoAliasNexo.Vehiculo, "tb").Should().Be("2L2");
        diccionario.Resolver(TipoAliasNexo.Vehiculo, "C2").Should().Be("2", "sin 'activo' la fila cuenta como activa");
        diccionario.Resolver(TipoAliasNexo.Vehiculo, "Turbo viejo").Should().BeNull("inactiva");
        diccionario.Resolver(TipoAliasNexo.Contenedor, "40 hc").Should().Be("CNT40.HC");
        diccionario.Resolver(TipoAliasNexo.Vehiculo, "raro").Should().BeNull("tipo desconocido");
        diccionario.Cantidad(TipoAliasNexo.Vehiculo).Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnErrorDeNexoEsNoDisponibleSinLanzar(HttpStatusCode estado)
    {
        _nexo.Responder(estado, """{"code":"X","message":"x"}""");

        (await Nexo().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task UnaCaidaDeRedOUnJsonInvalidoEsNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("caído"));
        (await Nexo().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);

        _nexo.Responder(HttpStatusCode.OK, "{no es json");
        (await Nexo().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);

        _nexo.Responder(HttpStatusCode.OK, """{"alias":"TB"}""");
        (await Nexo().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
    }

    [Fact]
    public async Task SinNexoConfiguradoOSinIdentidadNoConsulta()
    {
        var sinUrl = new clsAliasNexo(_http, Options.Create(new clsOpcionesNexo()), _usuarios, NullLogger<clsAliasNexo>.Instance);
        (await sinUrl.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        (await Nexo().ObtenerAsync(Guid.NewGuid(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        (await new clsAliasNexoNoDisponible().ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task LaCacheSirveLaCopiaVigenteYConNexoCaidoLaVencidaYNoInsisteTrasUnFallo()
    {
        var interno = Substitute.For<IClsAliasNexoProvider>();
        var diccionario = new clsDiccionarioAliasNexo([new(TipoAliasNexo.Vehiculo, "C2", "C2", "2")]);
        var caido = clsResultado<clsDiccionarioAliasNexo>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído"));
        interno.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(clsResultado<clsDiccionarioAliasNexo>.Exito(diccionario));
        var reloj = new clsRelojFalso();
        var cache = new clsAliasNexoEnCache(interno, new clsCacheAliasNexo(), reloj, Options.Create(new clsOpcionesNexo()), NullLogger<clsAliasNexoEnCache>.Instance);

        (await cache.ObtenerAsync(_usuarioId, Ct)).Valor.Should().BeSameAs(diccionario);
        reloj.Avanzar(TimeSpan.FromMinutes(29));
        (await cache.ObtenerAsync(_usuarioId, Ct)).Valor.Should().BeSameAs(diccionario);
        await interno.Received(1).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());

        interno.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(caido);
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        (await cache.ObtenerAsync(_usuarioId, Ct)).Valor.Should().BeSameAs(diccionario, "Nexo caído: la copia vencida sirve de respaldo");
        (await cache.ObtenerAsync(_usuarioId, Ct)).Valor.Should().BeSameAs(diccionario);
        await interno.Received(2).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());

        reloj.Avanzar(TimeSpan.FromHours(25));
        (await cache.ObtenerAsync(_usuarioId, Ct)).Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible, "la copia superó HorasCatalogosVencidos");
    }

    [Fact]
    public async Task SinCopiaYConNexoCaidoEsNoDisponibleYNoVuelveANexoDuranteUnMinuto()
    {
        var interno = Substitute.For<IClsAliasNexoProvider>();
        interno.ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsDiccionarioAliasNexo>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));
        var reloj = new clsRelojFalso();
        var cache = new clsAliasNexoEnCache(interno, new clsCacheAliasNexo(), reloj, Options.Create(new clsOpcionesNexo()), NullLogger<clsAliasNexoEnCache>.Instance);

        (await cache.ObtenerAsync(_usuarioId, Ct)).EsFallo.Should().BeTrue();
        reloj.Avanzar(TimeSpan.FromSeconds(30));
        (await cache.ObtenerAsync(_usuarioId, Ct)).EsFallo.Should().BeTrue();
        await interno.Received(1).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());

        reloj.Avanzar(clsAliasNexoEnCache.EsperaTrasFallo);
        await cache.ObtenerAsync(_usuarioId, Ct);
        await interno.Received(2).ObtenerAsync(_usuarioId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Validación #194: un comercial sin <c>idUsuarioExterno</c> no consulta Nexo, y ese "no disponible" no es una caída de Nexo: la caché no
    /// frena durante un minuto a los demás comerciales (antes, el primero sin identidad dejaba a todos sin diccionario).
    /// </summary>
    [Fact]
    public async Task UnComercialSinIdentidadEnNexoNoDejaSinDiccionarioALosDemas()
    {
        var sinIdentidad = Guid.NewGuid();
        _usuarios.ObtenerPorIdAsync(sinIdentidad, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("corp", "Usuario corporativo", "corp@boterosoto.com.co", null, DateTimeOffset.UtcNow, null).Valor);
        _nexo.Responder(HttpStatusCode.OK, Diccionario);
        var cache = new clsAliasNexoEnCache(
            Nexo(), new clsCacheAliasNexo(), new clsRelojFalso(), Options.Create(new clsOpcionesNexo()), NullLogger<clsAliasNexoEnCache>.Instance);

        var fallo = await cache.ObtenerAsync(sinIdentidad, Ct);
        fallo.Error.Codigo.Should().Be(clsCodigosError.CatalogosFleteNoDisponible);
        fallo.Error.Detalle.Should().Be(clsAliasNexo.DetalleSinIdentidad);
        _nexo.Peticiones.Should().BeEmpty();

        (await cache.ObtenerAsync(_usuarioId, Ct)).Valor.Resolver(TipoAliasNexo.Vehiculo, "C2").Should().Be("2", "el otro comercial sí consulta");
        _nexo.Peticiones.Should().ContainSingle();
    }

    private clsAliasNexo Nexo() =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios, NullLogger<clsAliasNexo>.Instance);

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
