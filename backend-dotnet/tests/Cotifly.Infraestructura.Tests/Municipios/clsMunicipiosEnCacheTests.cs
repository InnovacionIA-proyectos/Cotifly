using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Municipios.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Municipios;

/// <summary>
/// <see cref="clsMunicipiosEnCache"/> (HU-005.13): el catálogo de municipios de Nexo se reutiliza durante
/// <c>Nexo:MinutosCacheMunicipios</c>; los fallos y los catálogos vacíos no se guardan; las consultas simultáneas comparten una
/// sola consulta a Nexo, con éxito o con fallo. Con un proveedor simulado y el reloj falso: sin red.
/// </summary>
public sealed class clsMunicipiosEnCacheTests
{
    private static readonly IReadOnlyList<clsMunicipio> _catalogo = [new(1, "05400", "La Unión", "Antioquia", "Colombia")];

    private readonly IClsMunicipiosProvider _interno = Substitute.For<IClsMunicipiosProvider>();
    private readonly clsCacheCatalogoMunicipios _cache = new();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsLoggerFalso<clsMunicipiosEnCache> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReutilizaElCatalogoMientrasLaFotoEstaVigenteAunqueCambieElComercial()
    {
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo));
        var proveedor = Proveedor(minutos: 60);

        await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);
        _reloj.Avanzar(TimeSpan.FromMinutes(59));
        var segunda = await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);

        segunda.Valor.Should().BeSameAs(_catalogo);
        await _interno.Received(1).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VuelveANexoCuandoLaFotoVence()
    {
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo));
        var proveedor = Proveedor(minutos: 60);

        await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);
        _reloj.Avanzar(TimeSpan.FromMinutes(60));
        await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);

        await _interno.Received(2).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnFalloDeNexoNoSeGuardaYLaSiguienteConsultaReintenta()
    {
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            clsResultado<IReadOnlyList<clsMunicipio>>.Fallo(new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, "caído")),
            clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo));
        var proveedor = Proveedor(minutos: 60);

        var primera = await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);
        var segunda = await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);

        primera.Error.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        segunda.Valor.Should().BeSameAs(_catalogo);
    }

    [Fact]
    public async Task UnCatalogoVacioNoSeGuarda()
    {
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            clsResultado<IReadOnlyList<clsMunicipio>>.Exito([]),
            clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo));
        var proveedor = Proveedor(minutos: 60);

        (await proveedor.ObtenerAsync(Guid.NewGuid(), Ct)).Valor.Should().BeEmpty();
        var segunda = await proveedor.ObtenerAsync(Guid.NewGuid(), Ct);

        segunda.Valor.Should().BeSameAs(_catalogo, "un catálogo vacío no congela 60 minutos sin municipios");
    }

    [Fact]
    public async Task ConsultasSimultaneasVanUnaSolaVezANexo()
    {
        var liberar = new TaskCompletionSource();
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await liberar.Task;
            return clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo);
        });
        var proveedor = Proveedor(minutos: 60);

        var consultas = Enumerable.Range(0, 5).Select(_ => proveedor.ObtenerAsync(Guid.NewGuid(), Ct)).ToList();
        liberar.SetResult();
        var resultados = await Task.WhenAll(consultas);

        resultados.Should().OnlyContain(r => r.EsExitoso);
        await _interno.Received(1).ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConNexoCaidoLosQueEsperabanRecibenElMismoFalloSinEncolarse()
    {
        var llamadas = 0;
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Interlocked.Increment(ref llamadas);
            await Task.Delay(200);
            return clsResultado<IReadOnlyList<clsMunicipio>>.Fallo(new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, "caído"));
        });
        var proveedor = Proveedor(minutos: 60);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var resultados = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => proveedor.ObtenerAsync(Guid.NewGuid(), Ct)));

        llamadas.Should().Be(1);
        resultados.Should().OnlyContain(r => r.EsFallo);
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(900), "no espera una consulta detrás de otra");
    }

    [Fact]
    public async Task CancelarAlPrimeroNoCancelaLaConsultaDeLosDemas()
    {
        var liberar = new TaskCompletionSource();
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await liberar.Task.WaitAsync(ci.Arg<CancellationToken>());
            return clsResultado<IReadOnlyList<clsMunicipio>>.Exito(_catalogo);
        });
        var proveedor = Proveedor(minutos: 60);
        using var cts = new CancellationTokenSource();

        var primero = proveedor.ObtenerAsync(Guid.NewGuid(), cts.Token);
        var segundo = proveedor.ObtenerAsync(Guid.NewGuid(), Ct);
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => primero).Should().ThrowAsync<OperationCanceledException>();
        liberar.SetResult();

        (await segundo).Valor.Should().BeSameAs(_catalogo);
    }

    [Fact]
    public async Task UnaExcepcionDeLaConsultaCompartidaSeRegistraUnaVezYLlegaATodos()
    {
        var liberar = new TaskCompletionSource();
        _interno.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns<Task<clsResultado<IReadOnlyList<clsMunicipio>>>>(async _ =>
        {
            await liberar.Task;
            throw new InvalidOperationException("inesperado");
        });
        var proveedor = Proveedor(minutos: 60);

        var consultas = Enumerable.Range(0, 3).Select(_ => proveedor.ObtenerAsync(Guid.NewGuid(), Ct)).ToList();
        liberar.SetResult();

        foreach (var consulta in consultas)
        {
            await FluentActions.Awaiting(() => consulta).Should().ThrowAsync<InvalidOperationException>();
        }

        _logger.Registros.Should().ContainSingle(e => e.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && e.Excepcion is InvalidOperationException);
    }

    private clsMunicipiosEnCache Proveedor(int minutos) =>
        new(_interno, _cache, _reloj, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase, MinutosCacheMunicipios = minutos }), _logger);
}
