using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Comun;

/// <summary>
/// <c>Idempotency-Key</c> en memoria (ADR-018 §8): la repetición devuelve el resultado guardado sin ejecutar otra vez, también
/// cuando llega a la vez (doble clic); otra huella con la misma clave es un error; los fallos no consumen la clave; vence en 24 h.
/// </summary>
public sealed class clsAlmacenIdempotenciaEnMemoriaTests
{
    private const string Ambito = "re_solicitar_costeo:ana";
    private const string Clave = "5f0c1e2a-9b8d-4c7e";
    private readonly clsRelojFalso _reloj = new();
    private int _ejecuciones;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LaRepeticionDevuelveElResultadoGuardadoSinEjecutarOtraVez()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        var primero = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), null, Ct);
        var segundo = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v2"), null, Ct);

        primero.Valor.Should().Be("v1");
        segundo.Valor.Should().BeSameAs(primero.Valor);
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task DosPeticionesSimultaneasConLaMismaClaveSeEjecutanUnaSolaVez()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        var liberar = new TaskCompletionSource();
        async Task<clsResultado<string>> Lenta(CancellationToken ct)
        {
            Interlocked.Increment(ref _ejecuciones);
            await liberar.Task.WaitAsync(ct);
            return "unico";
        }

        var a = almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Lenta, null, Ct);
        var b = almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Lenta, null, Ct);
        liberar.SetResult();
        var resultados = await Task.WhenAll(a, b);

        resultados.Should().OnlyContain(r => r.Valor == "unico");
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task LaMismaClaveConOtraHuellaEsIdempotencyKeyReutilizada()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), null, Ct);

        var otra = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h2", Accion("v2"), null, Ct);

        // Un código estable (409 ErrorNegocio), no SOLICITUD_INVALIDA (400 ErrorWeb).
        otra.Error.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada);
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task OtroAmbitoUOtraClaveSeEjecutanAparte()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("a"), null, Ct);
        await almacen.EjecutarUnaVezAsync("re_solicitar_costeo:luis", Clave, "h1", Accion("b"), null, Ct);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave + "-2", "h1", Accion("c"), null, Ct);

        _ejecuciones.Should().Be(3);
    }

    [Fact]
    public async Task UnFalloNoConsumeLaClave()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        var fallo = await almacen.EjecutarUnaVezAsync<string>(Ambito, Clave, "h1",
            _ => { _ejecuciones++; return Task.FromResult<clsResultado<string>>(new clsErrorDominio("CONFLICTO_CONCURRENCIA", "x")); }, null, Ct);
        var exito = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), null, Ct);

        fallo.EsFallo.Should().BeTrue();
        exito.Valor.Should().Be("v1");
        _ejecuciones.Should().Be(2);
    }

    [Fact]
    public async Task PasadasVeinticuatroHorasLaClaveSeOlvida()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), null, Ct);

        _reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia + TimeSpan.FromSeconds(1));
        var despues = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v2"), null, Ct);

        despues.Valor.Should().Be("v2");
        _ejecuciones.Should().Be(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("corta")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public async Task UnaClaveFueraDelLargoDelContratoEsInvalida(string clave)
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        var resultado = await almacen.EjecutarUnaVezAsync(Ambito, clave, "h1", Accion("v1"), null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _ejecuciones.Should().Be(0);
    }

    [Fact]
    public async Task SeGuardaDecideQueConsumeLaClaveYLasFallidasSePurgan()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("error_envio"), r => r.Valor != "error_envio", Ct);
        almacen.Cantidad.Should().Be(0, "una clave que no se guardó se olvida enseguida");
        var guardado = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("ok"), r => r.Valor != "error_envio", Ct);
        var repetido = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("otra"), r => r.Valor != "error_envio", Ct);

        guardado.Valor.Should().Be("ok");
        repetido.Valor.Should().Be("ok");
        _ejecuciones.Should().Be(2);
    }

    [Fact]
    public async Task LasClavesVencidasSePurganAlUsarOtra()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), null, Ct);

        _reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia + TimeSpan.FromSeconds(1));
        await almacen.EjecutarUnaVezAsync(Ambito, "otra-clave-123", "h1", Accion("v2"), null, Ct);

        almacen.Cantidad.Should().Be(1, "la clave vencida se purgó");
    }

    // La purga no corre en cada petición, sino cada IntervaloPurga (o al llegar al tope).
    [Fact]
    public async Task LaPurgaEsPeriodicaYNoEnCadaPeticion()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, "clave-0001", "h", Accion("v"), null, Ct);
        var purgas = almacen.Purgas;

        for (var i = 2; i <= 50; i++)
        {
            await almacen.EjecutarUnaVezAsync(Ambito, $"clave-{i:D4}", "h", Accion("v"), null, Ct);
        }

        almacen.Purgas.Should().Be(purgas, "dentro del intervalo no se recorre el diccionario en cada clave nueva");
        _reloj.Avanzar(clsAlmacenIdempotenciaEnMemoria.IntervaloPurga + TimeSpan.FromSeconds(1));
        await almacen.EjecutarUnaVezAsync(Ambito, "clave-0051", "h", Accion("v"), null, Ct);
        almacen.Purgas.Should().Be(purgas + 1);
    }

    // Tope de claves (20.000 por defecto); al llenarse desaloja por lotes (10 % del tope), primero las más
    // próximas a vencer.
    [Fact]
    public async Task AlLlegarAlTopeDesalojaUnLoteDeLasClavesMasProximasAVencer()
    {
        clsAlmacenIdempotenciaEnMemoria.MaximoEntradasPorDefecto.Should().Be(20_000);
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj, maximoEntradas: 20);
        for (var i = 1; i <= 20; i++)
        {
            await almacen.EjecutarUnaVezAsync(Ambito, $"clave-{i:D4}", "h", Accion($"v{i}"), null, Ct);
            _reloj.Avanzar(TimeSpan.FromSeconds(1));
        }

        await almacen.EjecutarUnaVezAsync(Ambito, "clave-0021", "h", Accion("v21"), null, Ct);

        almacen.Cantidad.Should().Be(19, "se desalojó un lote de 2 (10 % de 20) y entró la nueva");
        var ejecucionesAntes = _ejecuciones;
        (await almacen.EjecutarUnaVezAsync(Ambito, "clave-0003", "h", Accion("otra"), null, Ct)).Valor.Should().Be("v3", "las recientes se conservan");
        _ejecuciones.Should().Be(ejecucionesAntes);
        (await almacen.EjecutarUnaVezAsync(Ambito, "clave-0001", "h", Accion("nueva"), null, Ct)).Valor.Should().Be("nueva", "la más próxima a vencer se desalojó");
    }

    private Func<CancellationToken, Task<clsResultado<string>>> Accion(string valor) => _ =>
    {
        _ejecuciones++;
        return Task.FromResult<clsResultado<string>>(valor);
    };
}
