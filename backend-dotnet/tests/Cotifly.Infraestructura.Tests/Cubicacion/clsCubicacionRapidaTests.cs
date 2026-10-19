using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): el almacén en memoria de las cubicaciones rápidas (retención, límite de entradas, aislamiento por
/// usuario) y el servicio que las corre en segundo plano (espera síncrona, 202, el cálculo que sigue aunque la petición se cancele,
/// errores que quedan <c>fallida</c> y apagado ordenado).
/// </summary>
public sealed class clsCubicacionRapidaTests
{
    private static readonly Guid _ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _luis = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly clsRelojFalso _reloj = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsResultadoCubicacion Resultado(string resultado = "ASIGNADO") => new(resultado, null, false, [], [], [], null, []);

    private static readonly IReadOnlyList<clsItemCubicacion> _items = [new("CAFE_EN_ESTIBAS", null, 10, 120m, 100m, 150m, 800m)];

    private clsAlmacenCubicacionesRapidasMemoria Almacen(int retencionMinutos = 15, int maximo = 500) =>
        new(_reloj, Options.Create(new clsOpcionesCubicador { RetencionCubicacionMinutos = retencionMinutos, MaximoCubicacionesEnMemoria = maximo }));

    // ---------------------------------------------------------------- Almacén en memoria

    [Fact]
    public void UnaCubicacionNaceEnProcesoYSoloLaVeSuDueno()
    {
        var almacen = Almacen();

        var creada = almacen.Crear(_ana)!;

        creada.Estado.Should().Be(clsEstadosCubicacionRapida.EnProceso);
        almacen.Obtener(creada.Id, _ana).Should().NotBeNull();
        almacen.Obtener(creada.Id, _luis).Should().BeNull("la de otro usuario es igual que una que no existe (sin IDOR)");
        almacen.Obtener(Guid.NewGuid(), _ana).Should().BeNull();
    }

    [Fact]
    public void CompletadaTraeElResultadoYFallidaElError()
    {
        var almacen = Almacen();
        var buena = almacen.Crear(_ana)!;
        var mala = almacen.Crear(_ana)!;

        almacen.Completar(buena.Id, Resultado());
        almacen.Fallar(mala.Id, new clsErrorDominio(clsCodigosError.DimensionesInvalidas, "Medidas inválidas."));

        var completada = almacen.Obtener(buena.Id, _ana)!;
        (completada.Estado, completada.Resultado!.Resultado, completada.Error).Should().Be((clsEstadosCubicacionRapida.Completada, "ASIGNADO", (clsErrorDominio?)null));
        var fallida = almacen.Obtener(mala.Id, _ana)!;
        (fallida.Estado, fallida.Error!.Codigo, fallida.Resultado).Should().Be((clsEstadosCubicacionRapida.Fallida, clsCodigosError.DimensionesInvalidas, (clsResultadoCubicacion?)null));
    }

    [Fact]
    public void VenceAlCumplirLaRetencionDesdeSuUltimoCambio()
    {
        var almacen = Almacen(retencionMinutos: 15);
        var cubicacion = almacen.Crear(_ana)!;
        _reloj.Avanzar(TimeSpan.FromMinutes(10));
        almacen.Completar(cubicacion.Id, Resultado());

        _reloj.Avanzar(TimeSpan.FromMinutes(14));
        almacen.Obtener(cubicacion.Id, _ana).Should().NotBeNull("completar renueva la retención");

        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        almacen.Obtener(cubicacion.Id, _ana).Should().BeNull("pasados 15 minutos desde que terminó, venció (404)");
        almacen.Cantidad.Should().Be(0);
    }

    [Fact]
    public void LlenoDescartaLaTerminadaMasViejaYSiTodasSiguenEnProcesoNoAceptaOtra()
    {
        var almacen = Almacen(maximo: 2);
        var vieja = almacen.Crear(_ana)!;
        var enCurso = almacen.Crear(_ana)!;
        almacen.Completar(vieja.Id, Resultado());

        var nueva = almacen.Crear(_luis);

        nueva.Should().NotBeNull();
        almacen.Obtener(vieja.Id, _ana).Should().BeNull("se descartó la terminada más vieja");
        almacen.Obtener(enCurso.Id, _ana).Should().NotBeNull("una en proceso nunca se descarta");
        almacen.Crear(_luis).Should().BeNull("las dos que quedan siguen en proceso");
    }

    [Fact]
    public void CompletarODescartarUnaQueYaNoExisteNoFalla()
    {
        var almacen = Almacen();
        var cubicacion = almacen.Crear(_ana)!;
        almacen.Descartar(cubicacion.Id);

        almacen.Completar(cubicacion.Id, Resultado());
        almacen.Fallar(Guid.NewGuid(), new clsErrorDominio("X", "x"));

        almacen.Obtener(cubicacion.Id, _ana).Should().BeNull();
    }

    // ---------------------------------------------------------------- Servicio en segundo plano

    private sealed class clsEntorno : IDisposable
    {
        public clsEntorno(clsRelojFalso reloj, int esperaSincrona, Func<CancellationToken, Task<clsResultado<clsResultadoCubicacion>>> calculo, int maximo = 500)
        {
            Calculo.CalcularAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsItemCubicacion>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(llamada => calculo(llamada.ArgAt<CancellationToken>(3)));
            var opciones = Options.Create(new clsOpcionesCubicador { EsperaSincronaCubicacionSegundos = esperaSincrona, MaximoCubicacionesEnMemoria = maximo });
            Almacen = new clsAlmacenCubicacionesRapidasMemoria(reloj, opciones);
            var servicios = new ServiceCollection();
            servicios.AddScoped(_ => Calculo);
            Proveedor = servicios.BuildServiceProvider();
            Servicio = new clsCubicacionRapidaService(Proveedor.GetRequiredService<IServiceScopeFactory>(), Almacen, opciones,
                NullLogger<clsCubicacionRapidaService>.Instance);
        }

        public IClsCalculoCubicajeService Calculo { get; } = Substitute.For<IClsCalculoCubicajeService>();

        public clsAlmacenCubicacionesRapidasMemoria Almacen { get; }

        public ServiceProvider Proveedor { get; }

        public clsCubicacionRapidaService Servicio { get; }

        public void Dispose()
        {
            Servicio.Dispose();
            Proveedor.Dispose();
        }
    }

    [Fact]
    public async Task SiTerminaDentroDeLaEsperaDevuelveElResultadoYNoGuardaNada()
    {
        using var entorno = new clsEntorno(_reloj, 5, _ => Task.FromResult<clsResultado<clsResultadoCubicacion>>(Resultado()));

        var inicio = await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct);

        inicio.Valor.Resultado!.Resultado.Should().Be("ASIGNADO");
        inicio.Valor.IdPendiente.Should().BeNull();
        entorno.Almacen.Cantidad.Should().Be(0, "nadie la va a consultar");
    }

    [Fact]
    public async Task UnErrorDentroDeLaEsperaSeDevuelveTalCual()
    {
        using var entorno = new clsEntorno(_reloj, 5,
            _ => Task.FromResult<clsResultado<clsResultadoCubicacion>>(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído")));

        var inicio = await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct);

        inicio.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    [Fact]
    public async Task SiNoTerminaATiempoDevuelveElIdYElCalculoSigueAunqueLaPeticionSeCancele()
    {
        var compuerta = new TaskCompletionSource<clsResultado<clsResultadoCubicacion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken tokenDelCalculo = default;
        using var entorno = new clsEntorno(_reloj, 0, token =>
        {
            tokenDelCalculo = token;
            return compuerta.Task;
        });
        using var peticion = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var inicio = await entorno.Servicio.IniciarAsync(_ana, _items, "Contenedor", peticion.Token);
        await EsperarAsync(() => entorno.Calculo.ReceivedCalls().Any());
        await peticion.CancelAsync();

        var id = inicio.Valor.IdPendiente!.Value;
        entorno.Servicio.Obtener(_ana, id)!.Estado.Should().Be(clsEstadosCubicacionRapida.EnProceso);
        entorno.Servicio.Obtener(_luis, id).Should().BeNull("solo la ve su dueño");
        tokenDelCalculo.IsCancellationRequested.Should().BeFalse("el cálculo usa el token del host, no el de la petición");
        compuerta.SetResult(Resultado("SIN_SELECCION_AUTOMATICA"));
        await EsperarAsync(() => entorno.Servicio.Obtener(_ana, id)!.Terminada);
        var final = entorno.Servicio.Obtener(_ana, id)!;
        (final.Estado, final.Resultado!.Resultado).Should().Be((clsEstadosCubicacionRapida.Completada, "SIN_SELECCION_AUTOMATICA"));
        await entorno.Calculo.Received(1).CalcularAsync(_ana, Arg.Any<IReadOnlyList<clsItemCubicacion>>(), "Contenedor", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnRechazoDelCubicadorEnSegundoPlanoQuedaFallidaConSuCodigo()
    {
        var compuerta = new TaskCompletionSource<clsResultado<clsResultadoCubicacion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entorno = new clsEntorno(_reloj, 0, _ => compuerta.Task);

        var id = (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).Valor.IdPendiente!.Value;
        compuerta.SetResult(new clsErrorDominio(clsCodigosError.DimensionesInvalidas, "Medidas inválidas."));
        await EsperarAsync(() => entorno.Servicio.Obtener(_ana, id)!.Terminada);

        var final = entorno.Servicio.Obtener(_ana, id)!;
        (final.Estado, final.Error!.Codigo).Should().Be((clsEstadosCubicacionRapida.Fallida, clsCodigosError.DimensionesInvalidas));
    }

    [Fact]
    public async Task UnaExcepcionEnSegundoPlanoQuedaFallidaSinDetallesInternos()
    {
        using var entorno = new clsEntorno(_reloj, 0, async token =>
        {
            await Task.Delay(100, token);
            throw new InvalidOperationException("detalle interno");
        });

        var id = (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).Valor.IdPendiente!.Value;
        await EsperarAsync(() => entorno.Servicio.Obtener(_ana, id)!.Terminada);

        var error = entorno.Servicio.Obtener(_ana, id)!.Error!;
        error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        error.Mensaje.Should().NotContain("detalle interno");
    }

    [Fact]
    public async Task SinEspacioEsCubicadorOcupado()
    {
        var compuerta = new TaskCompletionSource<clsResultado<clsResultadoCubicacion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var entorno = new clsEntorno(_reloj, 0, _ => compuerta.Task, maximo: 1);
        (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).EsExitoso.Should().BeTrue();

        var segunda = await entorno.Servicio.IniciarAsync(_luis, _items, null, Ct);

        segunda.Error.Codigo.Should().Be(clsCodigosError.CubicadorOcupado);
        compuerta.SetResult(Resultado());
    }

    [Fact]
    public async Task AlApagarseCancelaLosCalculosEnCursoYLosEspera()
    {
        using var entorno = new clsEntorno(_reloj, 0, async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Resultado();
        });
        var id = (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).Valor.IdPendiente!.Value;
        entorno.Servicio.EnCurso.Should().Be(1);

        await entorno.Servicio.StopAsync(Ct);

        entorno.Servicio.EnCurso.Should().Be(0, "no quedan tareas sueltas");
        entorno.Servicio.Obtener(_ana, id)!.Error!.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).Error.Mensaje.Should().Be(clsCubicacionRapidaService.MensajeApagando);
    }

    [Fact]
    public async Task TrasDesecharseUnaPeticionNuevaEsCubicadorNoDisponibleSinExcepcion()
    {
        // B1: antes, IniciarAsync leía _apagado.Token con la fuente ya desechada -> ObjectDisposedException -> 500.
        var entorno = new clsEntorno(_reloj, 0, _ => Task.FromResult<clsResultado<clsResultadoCubicacion>>(Resultado()));
        await entorno.Servicio.StopAsync(Ct);
        entorno.Dispose();

        var inicio = await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct);

        (inicio.Error.Codigo, inicio.Error.Mensaje).Should().Be((clsCodigosError.CubicadorNoDisponible, clsCubicacionRapidaService.MensajeApagando));
        entorno.Almacen.Cantidad.Should().Be(0, "una petición rechazada no deja registro");
        await entorno.Servicio.StopAsync(Ct); // Detener después de desechar no falla.
    }

    [Fact]
    public async Task LasPeticionesQueEntranDuranteElApagadoSeRechazanOSeEsperanTodas()
    {
        // B1: Task.WhenAll(_enCurso.Values) tomaba la foto antes de que entrara la última tarea. Ahora toda petición concurrente con
        // StopAsync o se rechaza (CUBICADOR_NO_DISPONIBLE) o queda registrada antes de la foto y StopAsync la espera.
        using var entorno = new clsEntorno(_reloj, 0, async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Resultado();
        });
        using var arranque = new ManualResetEventSlim(false);
        var peticiones = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() =>
            {
                arranque.Wait(Ct);
                return entorno.Servicio.IniciarAsync(_ana, _items, null, Ct);
            }, Ct))
            .ToArray();

        arranque.Set();
        await entorno.Servicio.StopAsync(Ct);
        var inicios = await Task.WhenAll(peticiones);

        entorno.Servicio.EnCurso.Should().Be(0, "StopAsync esperó también las que entraron en la ventana");
        foreach (var inicio in inicios)
        {
            if (inicio.EsExitoso)
            {
                var cubicacion = entorno.Servicio.Obtener(_ana, inicio.Valor.IdPendiente!.Value)!;
                (cubicacion.Estado, cubicacion.Error!.Codigo).Should().Be((clsEstadosCubicacionRapida.Fallida, clsCodigosError.CubicadorNoDisponible));
            }
            else
            {
                inicio.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
            }
        }

        (await entorno.Servicio.IniciarAsync(_ana, _items, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (var i = 0; i < 200 && !condicion(); i++)
        {
            await Task.Delay(25, Ct);
        }

        condicion().Should().BeTrue();
    }
}
