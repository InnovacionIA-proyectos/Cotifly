using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// #191: la <c>Idempotency-Key</c> persistente sobre <c>dbo_cotifly.CapturasIdempotentes</c> (<c>db/scripts/06</c>) con SQL Server real.
/// Cada "réplica" es una instancia propia de <see cref="clsAlmacenIdempotenciaSql"/> (su propia conexión), así un reinicio o dos
/// réplicas se prueban sin levantar dos APIs: la repetición devuelve lo guardado sin ejecutar, dos peticiones a la vez → una ejecuta y la
/// otra recibe <c>CAPTURA_EN_CURSO</c>, la clave vence en 24 h, una toma huérfana se retoma pasado su tiempo y el registro de la captura
/// queda en la transacción del folio.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsIdempotenciaSqlTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    /// <summary>La tabla de la propuesta de #173 (sin <c>operacion</c> ni las columnas de #191), para las pruebas de la sonda y del 06.</summary>
    public const string TablaPropuestaAnterior = """
        CREATE TABLE dbo_cotifly.CapturasIdempotentes (
            id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(), usuarioId UNIQUEIDENTIFIER NOT NULL, clave NVARCHAR(64) NOT NULL,
            huella CHAR(64) NOT NULL, estado NVARCHAR(20) NOT NULL, cotizacionId UNIQUEIDENTIFIER NULL, folio NVARCHAR(50) NULL,
            solicitudNexoId NVARCHAR(64) NULL, createdAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(),
            updatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET(), expiraAt DATETIMEOFFSET NOT NULL);
        """;

    private const string Operacion = "conexos";
    private const string HuellaA = "AAAA000000000000000000000000000000000000000000000000000000000000";
    private const string HuellaB = "BBBB000000000000000000000000000000000000000000000000000000000000";

    private readonly Guid _usuario = Guid.NewGuid();
    private int _ejecuciones;

    private string Ambito => $"{Operacion}:{_usuario}";

    private static string NuevaClave() => $"clave-{Guid.NewGuid():N}";

    [Fact]
    public async Task LaRepeticionEnOtraReplicaDevuelveElResultadoGuardadoSinEjecutar()
    {
        var clave = NuevaClave();

        var primero = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1.50m, ["a", "b"])), null, Ct);
        var otraReplica = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-2", 9m, [])), null, Ct);

        primero.Valor.Folio.Should().Be("F-1");
        otraReplica.Valor.Should().BeEquivalentTo(primero.Valor, "la segunda réplica (o la API reiniciada) devuelve lo guardado");
        otraReplica.Valor.Monto.Scale.Should().Be(2, "el decimal vuelve con su escala");
        _ejecuciones.Should().Be(1);
        (await EstadoAsync(clave)).Should().Be("terminada");
    }

    [Fact]
    public async Task LaMismaClaveConOtraHuellaEsIdempotencyKeyReutilizada()
    {
        var clave = NuevaClave();
        await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);

        var otra = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaB, Accion(new clsValorPrueba("F-2", 1m, [])), null, Ct);

        otra.Error.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada);
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task LaMismaClaveDeOtroComercialOOtraOperacionEsOtraClave()
    {
        var clave = NuevaClave();
        await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);

        (await Replica().EjecutarUnaVezAsync($"{Operacion}:{Guid.NewGuid()}", clave, HuellaB, Accion(new clsValorPrueba("F-2", 1m, [])), null, Ct))
            .Valor.Folio.Should().Be("F-2");
        (await Replica().EjecutarUnaVezAsync($"editar_solicitud:{_usuario}", clave, HuellaB, Accion(new clsValorPrueba("F-3", 1m, [])), null, Ct))
            .Valor.Folio.Should().Be("F-3");
        _ejecuciones.Should().Be(3);
    }

    [Fact]
    public async Task DosPeticionesALaVezEnDosReplicasUnaEjecutaYLaOtraRecibeCapturaEnCurso()
    {
        var clave = NuevaClave();
        var liberar = new TaskCompletionSource();
        var empezo = new TaskCompletionSource();
        async Task<clsResultado<clsValorPrueba>> Lenta(CancellationToken ct)
        {
            Interlocked.Increment(ref _ejecuciones);
            empezo.TrySetResult();
            await liberar.Task.WaitAsync(ct);
            return new clsValorPrueba("F-LENTA", 1m, []);
        }

        var primera = Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Lenta, null, Ct);
        await empezo.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var segunda = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-OTRA", 1m, [])), null, Ct);
        liberar.SetResult();
        var resultadoPrimera = await primera;
        var despues = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-OTRA", 1m, [])), null, Ct);

        segunda.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite, "409 sin esperar a la otra réplica");
        segunda.Error.Detalle.Should().Be(clsCodigosAplicacion.MotivoCapturaEnCurso);
        resultadoPrimera.Valor.Folio.Should().Be("F-LENTA");
        despues.Valor.Folio.Should().Be("F-LENTA", "al terminar, la repetición recibe el resultado de la que ganó");
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task UnDobleClicEnLaMismaReplicaEsperaALaPrimeraYRecibeSuResultado()
    {
        // Dentro de una réplica las peticiones con la misma clave se serializan (como en memoria): la segunda no recibe 409.
        var clave = NuevaClave();
        var almacen = Replica();
        async Task<clsResultado<clsValorPrueba>> Lenta(CancellationToken ct)
        {
            Interlocked.Increment(ref _ejecuciones);
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return new clsValorPrueba("F-DOBLE", 1m, []);
        }

        var resultados = await Task.WhenAll(
            almacen.EjecutarUnaVezAsync(Ambito, clave, HuellaA, Lenta, null, Ct),
            almacen.EjecutarUnaVezAsync(Ambito, clave, HuellaA, Lenta, null, Ct));

        resultados.Should().AllSatisfy(r => r.Valor.Folio.Should().Be("F-DOBLE"));
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task EnUnaCarreraDeVariasPeticionesSoloUnaEjecuta()
    {
        // El UNIQUE (usuarioId, operacion, clave) decide: ocho peticiones simultáneas, cada una con su "réplica".
        var clave = NuevaClave();
        async Task<clsResultado<clsValorPrueba>> Lenta(CancellationToken ct)
        {
            Interlocked.Increment(ref _ejecuciones);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return new clsValorPrueba("F-GANA", 1m, []);
        }

        var resultados = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Lenta, null, Ct), Ct)));

        _ejecuciones.Should().Be(1);
        resultados.Count(r => r.EsExitoso).Should().Be(1);
        resultados.Where(r => r.EsFallo).Should().AllSatisfy(r => r.Error.Detalle.Should().Be(clsCodigosAplicacion.MotivoCapturaEnCurso));
    }

    [Fact]
    public async Task LaClaveVenceEn24HorasYLaPurgaBorraLasVencidas()
    {
        var clave = NuevaClave();
        var almacen = Replica();
        await almacen.EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);

        Reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia - TimeSpan.FromMinutes(1));
        (await almacen.EjecutarUnaVezAsync(Ambito, clave, HuellaB, Accion(new clsValorPrueba("F-2", 1m, [])), null, Ct))
            .Error.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada, "todavía vigente");

        Reloj.Avanzar(TimeSpan.FromMinutes(2));
        (await almacen.EjecutarUnaVezAsync(Ambito, clave, HuellaB, Accion(new clsValorPrueba("F-2", 1m, [])), null, Ct))
            .Valor.Folio.Should().Be("F-2", "vencida, la clave se puede usar con otro cuerpo");
        _ejecuciones.Should().Be(2);

        var otraClave = NuevaClave();
        await almacen.EjecutarUnaVezAsync(Ambito, otraClave, HuellaA, Accion(new clsValorPrueba("F-3", 1m, [])), null, Ct);
        Reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia + TimeSpan.FromSeconds(1));
        var purga = new clsLimpiezaIdempotenciaHostedService(almacen, TimeProvider.System, NullLogger<clsLimpiezaIdempotenciaHostedService>.Instance);

        (await purga.LimpiarAsync(Ct)).Should().BeGreaterThanOrEqualTo(2);
        (await EstadoAsync(clave)).Should().BeNull();
        (await EstadoAsync(otraClave)).Should().BeNull();
    }

    [Fact]
    public async Task UnaTomaHuerfanaBloqueaHastaSuVencimientoYDespuesSeRetoma()
    {
        // La API se cayó con la petición a mitad: la fila quedó en_curso con la toma de otra petición.
        var clave = NuevaClave();
        await InsertarHuerfanaAsync(clave, HuellaA, folio: null);

        var mientras = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);
        Reloj.Avanzar(clsAlmacenIdempotenciaSql.TiempoMaximoEnCurso + TimeSpan.FromSeconds(1));
        var despues = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);

        mientras.Error.Detalle.Should().Be(clsCodigosAplicacion.MotivoCapturaEnCurso);
        despues.Valor.Folio.Should().Be("F-1", "la toma huérfana venció: nunca bloquea para siempre");
        _ejecuciones.Should().Be(1);
    }

    [Fact]
    public async Task UnaHuerfanaConFolioSoloSeRetomaConLaMismaHuellaYConservaElFolio()
    {
        var clave = NuevaClave();
        await InsertarHuerfanaAsync(clave, HuellaA, folio: "PCN-HUERFANA");
        Reloj.Avanzar(clsAlmacenIdempotenciaSql.TiempoMaximoEnCurso + TimeSpan.FromSeconds(1));

        var otroCuerpo = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaB, Accion(new clsValorPrueba("F-X", 1m, [])), null, Ct);
        string? folioVisto = null;
        var mismoCuerpo = await Replica().EjecutarUnaVezAsync<clsValorPrueba>(Ambito, clave, HuellaA, async ct =>
        {
            await using var ambito = NuevoAmbito();
            folioVisto = (await ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().BuscarAsync(Ambito, clave, ct))?.Folio;
            return new clsValorPrueba(folioVisto ?? "?", 1m, []);
        }, null, Ct);

        otroCuerpo.Error.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada, "con folio, la clave es de ese cuerpo");
        mismoCuerpo.Valor.Folio.Should().Be("PCN-HUERFANA", "quien retoma ve el folio de la primera y no vuelve a capturar");
    }

    [Fact]
    public async Task LaPeticionOriginalQueTerminaTardeNoPisaALaQueLaRetomo()
    {
        var clave = NuevaClave();
        var liberar = new TaskCompletionSource();
        var empezo = new TaskCompletionSource();
        async Task<clsResultado<clsValorPrueba>> Original(CancellationToken ct)
        {
            empezo.TrySetResult();
            await liberar.Task.WaitAsync(ct);
            return new clsValorPrueba("F-ORIGINAL", 1m, []);
        }

        var original = Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Original, null, Ct);
        await empezo.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Reloj.Avanzar(clsAlmacenIdempotenciaSql.TiempoMaximoEnCurso + TimeSpan.FromSeconds(1));
        var retoma = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-RETOMA", 1m, [])), null, Ct);
        liberar.SetResult();
        await original;

        var repeticion = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-OTRA", 1m, [])), null, Ct);
        retoma.Valor.Folio.Should().Be("F-RETOMA");
        repeticion.Valor.Folio.Should().Be("F-RETOMA", "la original ya no tenía la toma: su resultado no se guarda");
    }

    [Fact]
    public async Task LoQueNoSeGuardaNoConsumeLaClaveYUnaExcepcionLaSuelta()
    {
        var clave = NuevaClave();
        var fallo = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA,
            _ => Task.FromResult<clsResultado<clsValorPrueba>>(new clsErrorDominio(clsCodigosError.ErrorInterno, "falló")), null, Ct);
        (await EstadoAsync(clave)).Should().BeNull("sin folio, la fila de un resultado no guardado se borra");

        var lanza = () => Replica().EjecutarUnaVezAsync<clsValorPrueba>(Ambito, clave, HuellaB, _ => throw new TimeoutException("corte"), null, Ct);
        await lanza.Should().ThrowAsync<TimeoutException>();
        var otroCuerpo = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaB, Accion(new clsValorPrueba("F-1", 1m, [])), null, Ct);

        fallo.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        otroCuerpo.Valor.Folio.Should().Be("F-1", "ni el error ni la excepción consumieron la clave (como en memoria)");
    }

    [Fact]
    public async Task UnErrorQueSeGuardaVuelveConSuCodigoYSusDatos()
    {
        // Los conexos guardan el error cuyo POST a Nexo pudo ocurrir (agregado_en_nexo): la repetición lo devuelve tal cual.
        var clave = NuevaClave();
        var error = new clsErrorDominio(clsCodigosError.EstadoNoPermite, "Nexo no confirmó", "detalle")
        {
            Datos = new Dictionary<string, object?>
            {
                ["agregado_en_nexo"] = "desconocido",
                ["intentos"] = 2,
                ["margen"] = 0.15m,
                ["ok"] = true,
                ["errores"] = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["codigo"] = "X" } },
            },
        };
        await Replica().EjecutarUnaVezAsync<clsValorPrueba>(Ambito, clave, HuellaA, _ => Task.FromResult<clsResultado<clsValorPrueba>>(error), _ => true, Ct);

        var repetido = await Replica().EjecutarUnaVezAsync(Ambito, clave, HuellaA, Accion(new clsValorPrueba("F-1", 1m, [])), _ => true, Ct);

        repetido.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        repetido.Error.Mensaje.Should().Be("Nexo no confirmó");
        repetido.Error.Detalle.Should().Be("detalle");
        repetido.Error.Datos!["agregado_en_nexo"].Should().Be("desconocido");
        repetido.Error.Datos["intentos"].Should().Be(2L);
        repetido.Error.Datos["margen"].Should().Be(0.15m);
        repetido.Error.Datos["ok"].Should().Be(true);
        ((IEnumerable<IReadOnlyDictionary<string, object?>>)repetido.Error.Datos["errores"]!).Single()["codigo"].Should().Be("X");
        _ejecuciones.Should().Be(0);
    }

    [Fact]
    public async Task ElRegistroDeLaCapturaQuedaEnLaTransaccionDelFolio()
    {
        // #191: el folio y su clave se confirman o se deshacen juntos.
        var clave = NuevaClave();
        var ambitoCaptura = $"{clsEnvioCosteoService.OperacionIdempotencia}:{_usuario}";
        var captura = new clsCapturaIdempotente(HuellaA, Guid.NewGuid(), $"PCN-{Guid.NewGuid():N}"[..30], null, Reloj.AhoraUtc);

        await using (var deshecha = NuevoAmbito())
        {
            var contexto = deshecha.ServiceProvider.GetRequiredService<clsCotiflyDbContext>();
            await using var transaccion = await contexto.Database.BeginTransactionAsync(Ct);
            await deshecha.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().RegistrarAsync(ambitoCaptura, clave, captura, Ct);
            await transaccion.RollbackAsync(Ct);
        }

        await using var ambito = NuevoAmbito();
        var registro = ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>();
        (await registro.BuscarAsync(ambitoCaptura, clave, Ct)).Should().BeNull("la transacción del folio se deshizo");

        await using (var confirmada = NuevoAmbito())
        {
            var contexto = confirmada.ServiceProvider.GetRequiredService<clsCotiflyDbContext>();
            await using var transaccion = await contexto.Database.BeginTransactionAsync(Ct);
            await confirmada.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().RegistrarAsync(ambitoCaptura, clave, captura, Ct);
            await transaccion.CommitAsync(Ct);
        }

        (await registro.BuscarAsync(ambitoCaptura, clave, Ct))!.Folio.Should().Be(captura.Folio);
        await registro.RegistrarAsync(ambitoCaptura, clave, captura with { SolicitudNexoId = "sol-191", ScorePrioridad = 62.5m }, Ct);
        var porFolio = await registro.BuscarPorFolioAsync(captura.Folio, Ct);
        porFolio!.SolicitudNexoId.Should().Be("sol-191");
        porFolio.ScorePrioridad.Should().Be(62.5m);
        porFolio.CotizacionId.Should().Be(captura.CotizacionId);

        Reloj.Avanzar(IClsAlmacenIdempotencia.Vigencia + TimeSpan.FromSeconds(1));
        (await registro.BuscarPorFolioAsync(captura.Folio, Ct)).Should().BeNull("el registro vence con la clave");
    }

    [Fact]
    public async Task UnaCapturaQueFallaDespuesDelFolioDejaLaClaveLibreConSuFolio()
    {
        // La captura registra el folio y lanza (el guardado posterior a Nexo falló): la fila queda libre con el folio para el reintento.
        var clave = NuevaClave();
        var ambitoCaptura = $"{clsEnvioCosteoService.OperacionIdempotencia}:{_usuario}";
        var folio = $"PCN-{Guid.NewGuid():N}"[..30];
        var lanza = () => Replica().EjecutarUnaVezAsync<clsCapturaHecha>(ambitoCaptura, clave, HuellaA, async ct =>
        {
            await using var ambito = NuevoAmbito();
            await ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().RegistrarAsync(
                ambitoCaptura, clave, new clsCapturaIdempotente(HuellaA, Guid.NewGuid(), folio, "sol-1", Reloj.AhoraUtc), ct);
            throw new TimeoutException("Execution Timeout Expired");
        }, r => r.EsExitoso, Ct);

        await lanza.Should().ThrowAsync<TimeoutException>();

        (await EstadoAsync(clave, clsEnvioCosteoService.OperacionIdempotencia)).Should().Be("libre");
        await using var lectura = NuevoAmbito();
        (await lectura.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().BuscarAsync(ambitoCaptura, clave, Ct))!
            .SolicitudNexoId.Should().Be("sol-1");
    }

    [Fact]
    public async Task UnaLicitacionCuyaPeticionSeCayoTrasConfirmarElFolioSeRetomaConEseFolio()
    {
        // Crear licitación registra el folio con la clave en la transacción que lo confirma y la petición se corta antes de cerrar la toma.
        var clave = NuevaClave();
        var ambitoLicitacion = $"crear-licitacion:{_usuario}";
        var folio = $"PCN-{Guid.NewGuid():N}"[..30];
        var cortada = new TaskCompletionSource();
        var registrada = new TaskCompletionSource();
        var original = Replica().EjecutarUnaVezAsync<string>(ambitoLicitacion, clave, HuellaA, async ct =>
        {
            await using (var ambito = NuevoAmbito())
            {
                var contexto = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>();
                await using var transaccion = await contexto.Database.BeginTransactionAsync(ct);
                await ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().RegistrarAsync(
                    ambitoLicitacion, clave, new clsCapturaIdempotente(HuellaA, Guid.NewGuid(), folio, null, Reloj.AhoraUtc), ct);
                await transaccion.CommitAsync(ct);
            }

            registrada.SetResult();
            await cortada.Task.WaitAsync(ct);
            return "nunca";
        }, r => r.EsExitoso, Ct);
        await registrada.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var mientras = await Replica().EjecutarUnaVezAsync(ambitoLicitacion, clave, HuellaA, Accion(new clsValorPrueba("F-OTRA", 1m, [])), null, Ct);
        Reloj.Avanzar(clsAlmacenIdempotenciaSql.TiempoMaximoEnCurso + TimeSpan.FromSeconds(1));
        var otroCuerpo = await Replica().EjecutarUnaVezAsync(ambitoLicitacion, clave, HuellaB, Accion(new clsValorPrueba("F-OTRA", 1m, [])), null, Ct);
        string? folioVisto = null;
        var retoma = await Replica().EjecutarUnaVezAsync<string>(ambitoLicitacion, clave, HuellaA, async ct =>
        {
            await using var ambito = NuevoAmbito();
            folioVisto = (await ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().BuscarAsync(ambitoLicitacion, clave, ct))?.Folio;
            return folioVisto ?? "sin-folio";
        }, r => r.EsExitoso, Ct);
        cortada.SetCanceled(Ct);
        await FluentActions.Awaiting(() => original).Should().ThrowAsync<OperationCanceledException>();

        mientras.Error.Detalle.Should().Be(clsCodigosAplicacion.MotivoCapturaEnCurso, "dentro de la toma, el reintento no ejecuta");
        otroCuerpo.Error.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada, "con folio, la clave es de ese cuerpo");
        retoma.Valor.Should().Be(folio, "quien retoma ve el folio registrado y no crea otra licitación");
        _ejecuciones.Should().Be(0);
        (await EstadoAsync(clave, "crear-licitacion")).Should().Be("terminada", "la petición original ya no tenía la toma");
        await using var lectura = NuevoAmbito();
        (await lectura.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().BuscarPorFolioAsync(folio, Ct))
            .Should().BeNull("el reintento del costeo solo vincula registros de la captura");
    }

    [Fact]
    public async Task LaApiRegistraElAlmacenYElRegistroDeSql()
    {
        await using var ambito = NuevoAmbito();

        ambito.ServiceProvider.GetRequiredService<IClsAlmacenIdempotencia>().Should().BeOfType<clsAlmacenIdempotenciaSql>();
        ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>().Should().BeOfType<clsRegistroCapturasIdempotentesSql>();
    }

    private clsAlmacenIdempotenciaSql Replica() => new(
        Options.Create(new clsOpcionesConexiones { Cotifly = BaseDatos.CadenaCotifly }), Reloj, NullLogger<clsAlmacenIdempotenciaSql>.Instance);

    private Func<CancellationToken, Task<clsResultado<clsValorPrueba>>> Accion(clsValorPrueba valor) => _ =>
    {
        Interlocked.Increment(ref _ejecuciones);
        return Task.FromResult<clsResultado<clsValorPrueba>>(valor);
    };

    private Task<string?> EstadoAsync(string clave, string operacion = Operacion) =>
        clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT estado FROM dbo_cotifly.CapturasIdempotentes WHERE usuarioId = '{_usuario}' AND operacion = N'{operacion}' AND clave = N'{clave}'");

    private Task InsertarHuerfanaAsync(string clave, string huella, string? folio) =>
        clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.CapturasIdempotentes (usuarioId, operacion, clave, huella, estado, tomadaPor, enCursoHasta, cotizacionId, folio,
                                                          createdAt, updatedAt, expiraAt)
            VALUES ('{_usuario}', N'{Operacion}', N'{clave}', '{huella}', N'en_curso', NEWID(),
                    '{Reloj.AhoraUtc.Add(clsAlmacenIdempotenciaSql.TiempoMaximoEnCurso):O}',
                    {(folio is null ? "NULL" : "NEWID()")}, {(folio is null ? "NULL" : $"N'{folio}'")},
                    '{Reloj.AhoraUtc:O}', '{Reloj.AhoraUtc:O}', '{Reloj.AhoraUtc.Add(IClsAlmacenIdempotencia.Vigencia):O}');
            """);

    /// <summary>Resultado de prueba (un record, como los DTO que se guardan).</summary>
    public sealed record clsValorPrueba(string Folio, decimal Monto, IReadOnlyList<string> Lista);
}
