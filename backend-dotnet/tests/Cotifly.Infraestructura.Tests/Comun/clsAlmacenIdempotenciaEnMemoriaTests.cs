using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Comun;

/// <summary>
/// <c>Idempotency-Key</c> en memoria (ADR-018 §8, #100): la repetición devuelve el resultado guardado sin ejecutar otra vez, también
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

        var primero = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), Ct);
        var segundo = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v2"), Ct);

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

        var a = almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Lenta, Ct);
        var b = almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Lenta, Ct);
        liberar.SetResult();
        var resultados = await Task.WhenAll(a, b);

        resultados.Should().OnlyContain(r => r.Valor == "unico");
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task LaMismaClaveConOtraHuellaEsSolicitudInvalida()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), Ct);

        var otra = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h2", Accion("v2"), Ct);

        otra.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task OtroAmbitoUOtraClaveSeEjecutanAparte()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("a"), Ct);
        await almacen.EjecutarUnaVezAsync("re_solicitar_costeo:luis", Clave, "h1", Accion("b"), Ct);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave + "-2", "h1", Accion("c"), Ct);

        _ejecuciones.Should().Be(3);
    }

    [Fact]
    public async Task UnFalloNoConsumeLaClave()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);

        var fallo = await almacen.EjecutarUnaVezAsync<string>(Ambito, Clave, "h1",
            _ => { _ejecuciones++; return Task.FromResult<clsResultado<string>>(new clsErrorDominio("CONFLICTO_CONCURRENCIA", "x")); }, Ct);
        var exito = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), Ct);

        fallo.EsFallo.Should().BeTrue();
        exito.Valor.Should().Be("v1");
        _ejecuciones.Should().Be(2);
    }

    [Fact]
    public async Task PasadasVeinticuatroHorasLaClaveSeOlvida()
    {
        var almacen = new clsAlmacenIdempotenciaEnMemoria(_reloj);
        await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v1"), Ct);

        _reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia + TimeSpan.FromSeconds(1));
        var despues = await almacen.EjecutarUnaVezAsync(Ambito, Clave, "h1", Accion("v2"), Ct);

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

        var resultado = await almacen.EjecutarUnaVezAsync(Ambito, clave, "h1", Accion("v1"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _ejecuciones.Should().Be(0);
    }

    private Func<CancellationToken, Task<clsResultado<string>>> Accion(string valor) => _ =>
    {
        _ejecuciones++;
        return Task.FromResult<clsResultado<string>>(valor);
    };
}
