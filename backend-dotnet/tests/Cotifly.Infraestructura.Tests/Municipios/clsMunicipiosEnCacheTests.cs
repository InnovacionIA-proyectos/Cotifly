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
/// <c>Nexo:MinutosCacheMunicipios</c>; los fallos no se guardan. Con un proveedor simulado y el reloj falso: sin red.
/// </summary>
public sealed class clsMunicipiosEnCacheTests : IDisposable
{
    private static readonly IReadOnlyList<clsMunicipio> _catalogo = [new(1, "05400", "La Unión", "Antioquia", "Colombia")];

    private readonly IClsMunicipiosProvider _interno = Substitute.For<IClsMunicipiosProvider>();
    private readonly clsCacheCatalogoMunicipios _cache = new();
    private readonly clsRelojFalso _reloj = new();

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

    public void Dispose() => _cache.Dispose();

    private clsMunicipiosEnCache Proveedor(int minutos) =>
        new(_interno, _cache, _reloj, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase, MinutosCacheMunicipios = minutos }));
}
