using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Precio;

/// <summary>Caché por fecha de negocio de la TRM (<see cref="clsProveedorTrmEnCache"/>, HU-012.1) con un reloj falso.</summary>
public sealed class clsProveedorTrmEnCacheTests
{
    private static readonly DateOnly _lunes = new(2026, 9, 28);
    private static readonly DateOnly _martes = new(2026, 9, 29);

    private readonly IClsProveedorTrm _fuente = Substitute.For<IClsProveedorTrm>();
    private readonly clsRelojFalso _reloj = new(new DateTimeOffset(2026, 9, 28, 13, 0, 0, TimeSpan.Zero));
    private readonly clsProveedorTrmEnCache _cache;

    public clsProveedorTrmEnCacheTests()
    {
        _fuente.ObtenerAsync(_lunes, Arg.Any<CancellationToken>()).Returns(Trm(3306.86m, _lunes));
        _fuente.ObtenerAsync(_martes, Arg.Any<CancellationToken>()).Returns(Trm(3312.50m, _martes));
        _cache = new clsProveedorTrmEnCache(_fuente, _reloj);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElMismoDiaConsultaLaFuenteUnaSolaVez()
    {
        var primera = await _cache.ObtenerAsync(_lunes, Ct);
        _reloj.Avanzar(TimeSpan.FromHours(3));
        var segunda = await _cache.ObtenerAsync(_lunes, Ct);

        primera.Valor.Should().Be(segunda.Valor);
        segunda.Valor.Valor.Should().Be(3306.86m);
        await _fuente.Received(1).ObtenerAsync(_lunes, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OtraFechaEsOtraEntrada()
    {
        (await _cache.ObtenerAsync(_lunes, Ct)).Valor.Valor.Should().Be(3306.86m);
        (await _cache.ObtenerAsync(_martes, Ct)).Valor.Valor.Should().Be(3312.50m);

        await _fuente.Received(1).ObtenerAsync(_lunes, Arg.Any<CancellationToken>());
        await _fuente.Received(1).ObtenerAsync(_martes, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VencidaLaEntradaSeVuelveAConsultar()
    {
        await _cache.ObtenerAsync(_lunes, Ct);
        _reloj.Avanzar(clsProveedorTrmEnCache.Duracion);

        await _cache.ObtenerAsync(_lunes, Ct);

        await _fuente.Received(2).ObtenerAsync(_lunes, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnFalloSeRecuerdaPocoYDespuesElReintentoConsultaDeNuevo()
    {
        var fecha = new DateOnly(2026, 9, 30);
        _fuente.ObtenerAsync(fecha, Arg.Any<CancellationToken>()).Returns(
            new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"),
            Trm(3320m, fecha));

        (await _cache.ObtenerAsync(fecha, Ct)).Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        (await _cache.ObtenerAsync(fecha, Ct)).Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible, "caché negativa: no golpea la fuente caída");
        await _fuente.Received(1).ObtenerAsync(fecha, Arg.Any<CancellationToken>());

        _reloj.Avanzar(clsProveedorTrmEnCache.DuracionFallo);
        (await _cache.ObtenerAsync(fecha, Ct)).Valor.Valor.Should().Be(3320m);
        (await _cache.ObtenerAsync(fecha, Ct)).Valor.Valor.Should().Be(3320m);

        await _fuente.Received(2).ObtenerAsync(fecha, Arg.Any<CancellationToken>());
    }

    // Auditoría: con la fuente caída, 5 consultas simultáneas hacen una sola llamada y todas reciben el mismo fallo.
    [Fact]
    public async Task ConsultasSimultaneasContraUnaFuenteCaidaHacenUnaSolaLlamada()
    {
        var liberar = new TaskCompletionSource<clsResultado<clsTrm>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fecha = new DateOnly(2026, 10, 2);
        _fuente.ObtenerAsync(fecha, Arg.Any<CancellationToken>()).Returns(_ => liberar.Task);

        var consultas = Enumerable.Range(0, 5).Select(_ => Task.Run(() => _cache.ObtenerAsync(fecha, Ct), Ct)).ToList();
        await Task.Delay(50, Ct);
        liberar.SetResult(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "caída"));
        var resultados = await Task.WhenAll(consultas);

        resultados.Should().OnlyContain(r => r.EsFallo && r.Error.Codigo == clsCodigosError.TrmNoDisponible);
        await _fuente.Received(1).ObtenerAsync(fecha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnLlamadorQueCancelaNoCancelaLaConsultaDeLosDemas()
    {
        var liberar = new TaskCompletionSource<clsResultado<clsTrm>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fecha = new DateOnly(2026, 10, 3);
        _fuente.ObtenerAsync(fecha, Arg.Any<CancellationToken>()).Returns(_ => liberar.Task);
        using var cancelacion = new CancellationTokenSource();

        var impaciente = _cache.ObtenerAsync(fecha, cancelacion.Token);
        var paciente = _cache.ObtenerAsync(fecha, Ct);
        await cancelacion.CancelAsync();
        liberar.SetResult(Trm(3340m, fecha));

        await ((Func<Task>)(() => impaciente)).Should().ThrowAsync<OperationCanceledException>();
        (await paciente).Valor.Valor.Should().Be(3340m);
        await _fuente.Received(1).ObtenerAsync(fecha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsultasSimultaneasSinCacheEsperanLaMismaRespuesta()
    {
        var liberar = new TaskCompletionSource<clsResultado<clsTrm>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fecha = new DateOnly(2026, 10, 1);
        _fuente.ObtenerAsync(fecha, Arg.Any<CancellationToken>()).Returns(_ => liberar.Task);

        var consultas = Enumerable.Range(0, 5).Select(_ => _cache.ObtenerAsync(fecha, Ct)).ToList();
        liberar.SetResult(Trm(3330m, fecha));
        var resultados = await Task.WhenAll(consultas);

        resultados.Should().OnlyContain(r => r.EsExitoso && r.Valor.Valor == 3330m);
        await _fuente.Received(1).ObtenerAsync(fecha, Arg.Any<CancellationToken>());
    }

    private static clsResultado<clsTrm> Trm(decimal valor, DateOnly fecha) => new clsTrm(valor, fecha, fecha, "prueba");
}
