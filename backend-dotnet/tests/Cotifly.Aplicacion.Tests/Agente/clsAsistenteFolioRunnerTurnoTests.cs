using System.Collections.Concurrent;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Tests.Costeo;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Sesiones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// HU-013.16 (#126): forma del turno del asistente (menos llamadas al modelo sin relajar ninguna regla), turnos concurrentes en la
/// misma sesión, el corte por <c>IA:MaxIteraciones</c> y el log de duración por fase. Con el <see cref="IChatClient"/> guionizado.
/// </summary>
public sealed partial class clsAsistenteFolioRunnerTests
{
    // ---------- Menos llamadas por turno ----------

    [Fact]
    public async Task UnaRespuestaSinHerramientasQueYaEsElJsonValidoCierraElTurnoEnUnaSolaLlamada()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.CerrarEnLoop("Hola, ¿qué revisamos de esta cotización?");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Hola"), Ct);

        _modelo.Llamadas.Should().ContainSingle();
        _modelo.Llamadas[0].Herramientas.Should().NotBeEmpty("es la llamada del loop, no una llamada final aparte");
        resultado.Valor.Texto.Should().Be("Hola, ¿qué revisamos de esta cotización?");
        resultado.Valor.Cierre.Should().Be(clsAsistenteFolioRunner.CierreLoop);
        resultado.Valor.Iteraciones.Should().Be(1);
    }

    [Fact]
    public async Task ConsultarYResponderSonDosLlamadas()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("consultar_cotizacion", new { }).CerrarEnLoop("La cotización está en autocosteo y todavía no se envía.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿En qué estado está?"), Ct);

        _modelo.Llamadas.Should().HaveCount(2);
        resultado.Valor.HerramientasInvocadas.Should().Equal("consultar_cotizacion");
        resultado.Valor.Cierre.Should().Be(clsAsistenteFolioRunner.CierreLoop);
        _sesiones.Eventos.Last().Contenido.Should().Be("La cotización está en autocosteo y todavía no se envía.");
    }

    // La respuesta del loop pasa por las MISMAS reglas: un monto inventado no sale; se pide la llamada final como antes.
    [Fact]
    public async Task UnaRespuestaDelLoopConUnMontoInventadoNoSeUsaYSePideLaLlamadaFinal()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("consultar_cotizacion", new { })
            .CerrarEnLoop("El precio actual es $3.000.000.")
            .ResponderFinal("La cotización está costeada; los valores los ves en el detalle.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿Cuánto vale?"), Ct);

        _modelo.Llamadas.Should().HaveCount(3);
        _modelo.Llamadas[2].Herramientas.Should().BeEmpty();
        resultado.Valor.Texto.Should().Be("La cotización está costeada; los valores los ves en el detalle.");
        resultado.Valor.Cierre.Should().Be(clsAsistenteFolioRunner.CierreLlamadaFinal);
    }

    [Fact]
    public async Task UnaRespuestaDelLoopEnTextoLibreSigueConLaLlamadaFinal()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Cerrar("Está costeada.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿En qué va?"), Ct);

        _modelo.Llamadas.Should().HaveCount(2);
        resultado.Valor.Texto.Should().Be("Está costeada.");
        resultado.Valor.Cierre.Should().Be(clsAsistenteFolioRunner.CierreLlamadaFinal);
    }

    [Fact]
    public async Task SinLosAtajosElTurnoHaceLasLlamadasDeAntes()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen = 0.18 })
            .CerrarEnLoop("Te propongo subir el margen al 18 %.", "confirmacion_requerida", true)
            .Proponer("Te propongo subir el margen al 18 %. Confírmalo con el botón.");
        var opciones = new clsOpcionesAgente { PropuestaDirecta = false, RespuestaFinalEnLoop = false };

        var resultado = await Runner(opciones).EjecutarTurnoAsync(Solicitud("Súbele el margen al 18 %"), Ct);

        _modelo.Llamadas.Should().HaveCount(3, "herramienta, respuesta sin herramientas y llamada final");
        resultado.Valor.Texto.Should().Be("Te propongo subir el margen al 18 %. Confírmalo con el botón.");
        resultado.Valor.AccionPropuesta.Should().NotBeNull();
    }

    // ---------- Lo que ve el modelo (auditoría de #126) ----------

    // Las salidas de consultar_cotizacion y buscar_servicio_conexo no llevan descripciones, precios, costos, valores, totales ni TRM:
    // ninguna clave con esos nombres (la búsqueda solo trae "total", el número de resultados) y ningún monto de la cotización.
    [Fact]
    public async Task LasSalidasQueVeElModeloNoTraenDescripcionesNiPrecios()
    {
        Registrar(ConCotizacionNexo(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio)));
        _conexos.BuscarAsync(_ana.Id, "cargue", null, 1, Cotifly.Aplicacion.Agente.Herramientas.clsHerramientasAsistenteFolio.MaximoServicios, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaServiciosConexos>.Exito(new clsPaginaServiciosConexos(1, 1, 10,
                [new clsServicioConexoCatalogo("CX-9", "CAR-01", "Cargue", "logistico", "Unidad", "Buenaventura", "Bogotá", "BUN", "Ruta 1", "Grúa", null)])));
        _modelo.Llamar("consultar_cotizacion", new { }).Llamar("buscar_servicio_conexo", new { texto = "cargue" }).CerrarEnLoop("Listo.");

        await Runner().EjecutarTurnoAsync(Solicitud("Revisa la cotización y busca cargue"), Ct);

        var consulta = _modelo.Llamadas[2].ResultadosHerramientas[0];
        var busqueda = _modelo.Llamadas[2].ResultadosHerramientas[1];
        foreach (var salida in new[] { consulta, busqueda })
        {
            using var documento = JsonDocument.Parse(salida);
            Claves(documento.RootElement).Should().NotContain(c => ClaveProhibida().IsMatch(c), "ninguna clave de dinero ni descripción llega al modelo");
            salida.Should().NotContain("2550000").And.NotContain("3000000").And.NotContain("100000").And.NotContain("85000");
        }

        using var servicios = JsonDocument.Parse(busqueda);
        servicios.RootElement.GetProperty("servicios")[0].EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("id", "nombre", "codigo", "categoria", "unidad");
    }

    private static IEnumerable<string> Claves(JsonElement elemento) => elemento.ValueKind switch
    {
        JsonValueKind.Object => elemento.EnumerateObject().SelectMany(p => Claves(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => elemento.EnumerateArray().SelectMany(Claves),
        _ => [],
    };

    [System.Text.RegularExpressions.GeneratedRegex("descripci|precio|costo|valor|_total|total_|trm|tarifa|utilidad", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ClaveProhibida();

    [Fact]
    public async Task ConPropuestaElTipoEsConfirmacionRequeridaYSinPropuestaNunca()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen = 0.18 }).ResponderFinal("Listo, ajusté la propuesta del margen.");

        var conPropuesta = await Runner().EjecutarTurnoAsync(Solicitud("Súbelo al 18 %"), Ct);

        conPropuesta.Valor.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.ConfirmacionRequerida, "el modelo dijo conversacional, pero hay propuesta");
        conPropuesta.Valor.Respuesta.RequiereConfirmacion.Should().BeTrue();

        _modelo.CerrarEnLoop("¿Confirmas?", "confirmacion_requerida", true);
        var sinPropuesta = await Runner().EjecutarTurnoAsync(Solicitud("¿Algo más?"), Ct);

        sinPropuesta.Valor.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.Conversacional, "sin propuesta no hay nada que confirmar");
        sinPropuesta.Valor.Respuesta.RequiereConfirmacion.Should().BeFalse();
    }

    // ---------- Turnos concurrentes en la misma sesión ----------

    [Fact]
    public async Task ConLaSesionOcupadaElTurnoEsTurnoEnCursoSinLlamarAlModeloNiGuardarNada()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        var candado = new clsCandadoSesionFalso { Ocupado = true };

        var turno = async () => await Runner(candado: candado).EjecutarTurnoAsync(Solicitud("¿En qué va?"), Ct);

        var error = (await turno.Should().ThrowAsync<clsTurnoEnCursoException>()).Which;
        clsTextosAgente.ParaFallo(error).Should().Be(clsTextosAgente.TurnoEnCurso);
        _modelo.Llamadas.Should().BeEmpty();
        _sesiones.Eventos.Should().BeEmpty("el mensaje del turno rechazado no entra a la conversación");
    }

    [Fact]
    public async Task DosTurnosConcurrentesEnLaMismaSesionElSegundoRecibeElErrorControlado()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.CerrarEnLoop("Primera respuesta.").CerrarEnLoop("Tercera respuesta.");
        var compuerta = new clsChatConCompuerta(_modelo);
        var candado = new clsCandadoExclusivo();
        var sinEspera = new clsOpcionesSesiones { EsperaTurnoSegundos = 0 };

        var primero = Runner(candado: candado, modelo: compuerta, opcionesSesiones: sinEspera).EjecutarTurnoAsync(Solicitud("Primero"), Ct);
        await compuerta.Dentro.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var segundo = async () => await Runner(candado: candado, modelo: compuerta, opcionesSesiones: sinEspera).EjecutarTurnoAsync(Solicitud("Segundo"), Ct);

        var error = (await segundo.Should().ThrowAsync<clsTurnoEnCursoException>()).Which;
        clsTextosAgente.ParaFallo(error).Should().Be(clsTextosAgente.TurnoEnCurso);
        compuerta.Soltar.SetResult();
        (await primero).Valor.Texto.Should().Be("Primera respuesta.");

        var tercero = await Runner(candado: candado, modelo: compuerta, opcionesSesiones: sinEspera).EjecutarTurnoAsync(Solicitud("Tercero"), Ct);
        tercero.Valor.Texto.Should().Be("Tercera respuesta.", "el primer turno liberó el candado al terminar");
        _sesiones.Eventos.Where(e => e.Rol == clsEventoConversacion.RolUsuario).Select(e => e.Contenido).Should().Equal("Primero", "Tercero");
    }

    // ---------- Tope de iteraciones ----------

    [Fact]
    public async Task UnModeloQuePideHerramientasSinPararSeCortaEnElTopeConUnMensajeClaro()
    {
        var cotizacion = Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.RepetirSiempre("consultar_cotizacion", new { });
        var registro = new clsLoggerFalso<clsAsistenteFolioRunner>();

        var turno = async () => await Runner(new clsOpcionesAgente { MaxIteraciones = 3 }, logger: registro).EjecutarTurnoAsync(Solicitud("Revisa todo"), Ct);

        var error = (await turno.Should().ThrowAsync<clsMaxIteracionesExcedidasException>()).Which;
        error.Message.Should().Be("El agente no cerró el turno en 3 iteraciones.");
        clsTextosAgente.ParaFallo(error).Should().Be(clsTextosAgente.MaxIteraciones);
        _modelo.Llamadas.Should().HaveCount(3);
        _sesiones.Eventos.Should().HaveCount(7, "la pregunta y las tres rondas (llamada y resultado) quedan en la conversación");
        _sesiones.Eventos[0].Contenido.Should().Be("Revisa todo");
        registro.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("se cortó en 3 iteraciones", StringComparison.Ordinal));
        registro.Registros.Should().Contain(r => r.Mensaje.Contains("fallo clsMaxIteracionesExcedidasException", StringComparison.Ordinal));
        SinCambios(cotizacion);
    }

    [Fact]
    public async Task UnModeloQueRepiteLaPropuestaNoLlegaAlTopePorqueLaPropuestaCierraLaRonda()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.RepetirSiempre("proponer_ajuste_margen", new { margen = 0.18 });

        var resultado = await Runner(new clsOpcionesAgente { MaxIteraciones = 3 }).EjecutarTurnoAsync(Solicitud("Súbele el margen"), Ct);

        resultado.Valor.AccionPropuesta.Should().NotBeNull();
        resultado.Valor.EsRespaldo.Should().BeTrue("la llamada final volvió a pedir la herramienta y no dio un texto válido");
        clsRespuestaChat.DesdeAsistente(resultado.Valor, clsTextosAgente.PropuestaSinTexto).Texto.Should().Be(clsTextosAgente.PropuestaSinTexto);
        _modelo.Llamadas.Should().HaveCount(2);
    }

    // ---------- Log de duración por fase ----------

    [Fact]
    public async Task ElTurnoRegistraSuDuracionPorFaseSinTextos()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen = 0.18 }).Proponer("Te propongo subir el margen al 18 %.");
        var registro = new clsLoggerFalso<clsAsistenteFolioRunner>();

        await Runner(logger: registro).EjecutarTurnoAsync(Solicitud("Súbele el margen al 18 % porque el cliente aceptó"), Ct);

        var linea = registro.Registros.Should().ContainSingle(r => r.Mensaje.StartsWith("Turno del asistente de", StringComparison.Ordinal)).Which;
        linea.Nivel.Should().Be(LogLevel.Information);
        linea.Mensaje.Should().Contain(Folio)
            .And.Contain("modelo con herramientas")
            .And.Contain("en 1 llamadas")
            .And.Contain("Iteraciones 2, cierre llamada_final, reintentos del validador 0, respaldo False, propuesta ajustar_margen")
            .And.Contain("tokens 0/0")
            .And.NotContain("cliente aceptó", "el log no lleva el texto del comercial")
            .And.NotContain("18 %", "ni el del modelo");
    }

    [Fact]
    public async Task LaMedicionSumaLlamadasTokensYModelosPorFase()
    {
        var medicion = new clsMedicionTurno();
        var modelo = new clsChatClientGuionizado().Responder("uno").Responder("dos").Responder("tres");
        var medido = medicion.Cliente(modelo, clsMedicionTurno.FaseModelo);

        await medido.GetResponseAsync([new ChatMessage(ChatRole.User, "a")], cancellationToken: Ct);
        await medido.GetResponseAsync([new ChatMessage(ChatRole.User, "b")], cancellationToken: Ct);
        using (medicion.Medir(clsMedicionTurno.FaseHerramientas))
        {
            await Task.Delay(5, Ct);
        }

        medicion.Llamadas(clsMedicionTurno.FaseModelo).Should().Be(2);
        medicion.Llamadas(clsMedicionTurno.FaseRespuestaFinal).Should().Be(0);
        medicion.Fase(clsMedicionTurno.FaseHerramientas).Should().BePositive();
        medicion.Total.Should().BeGreaterThanOrEqualTo(medicion.Fase(clsMedicionTurno.FaseHerramientas));
        medicion.TokensEntrada.Should().Be(0, "el guion no informa uso");
        medicion.Modelos.Should().BeEmpty();

        medido.Dispose();
        (await modelo.GetResponseAsync([new ChatMessage(ChatRole.User, "c")], cancellationToken: Ct)).Text
            .Should().Be("tres", "desechar el cliente medido del turno no desecha el cliente compartido");
    }

    [Fact]
    public void LasExcepcionesDelTurnoConservanSuMensajeYSuCausa()
    {
        var causa = new InvalidOperationException("causa");

        new clsTurnoEnCursoException().Message.Should().Be("Hay otro turno en curso en esta sesión.");
        new clsTurnoEnCursoException("otro").Message.Should().Be("otro");
        new clsTurnoEnCursoException("otro", causa).InnerException.Should().BeSameAs(causa);
        new clsMaxIteracionesExcedidasException().Message.Should().NotBeNullOrEmpty();
        new clsMaxIteracionesExcedidasException("tope").Message.Should().Be("tope");
        new clsMaxIteracionesExcedidasException("tope", causa).InnerException.Should().BeSameAs(causa);
        clsTextosAgente.ParaFallo(new clsMaxIteracionesExcedidasException("tope", new HttpRequestException("429")))
            .Should().Be(clsTextosAgente.MaxIteraciones, "el corte por iteraciones gana sobre la heurística del 429");
    }

    /// <summary>Deja pasar la primera llamada al modelo solo cuando la prueba suelta la compuerta (un turno "en curso").</summary>
    private sealed class clsChatConCompuerta(IChatClient interno) : DelegatingChatClient(interno)
    {
        private int _llamadas;

        public TaskCompletionSource Dentro { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Soltar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _llamadas) == 1)
            {
                Dentro.SetResult();
                await Soltar.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    /// <summary>Candado por sesión exclusivo de verdad (un semáforo por hilo), como el de SQL Server en un solo proceso.</summary>
    private sealed class clsCandadoExclusivo : IClsCandadoSesion
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaforos = new(StringComparer.Ordinal);

        public async Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default)
        {
            var semaforo = _semaforos.GetOrAdd($"{spaceId}|{threadId}", _ => new SemaphoreSlim(1, 1));
            return await semaforo.WaitAsync(espera, cancellationToken) ? new clsLiberar(semaforo) : null;
        }

        private sealed class clsLiberar(SemaphoreSlim semaforo) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                semaforo.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
