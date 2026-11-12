using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Agente.Filtros;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Sesiones;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Un turno del asistente IA sobre una cotización.</summary>
public interface IClsAsistenteFolioRunner
{
    /// <summary>
    /// Corre el turno sobre el folio de <paramref name="solicitud"/>. Fallos de negocio: <c>NO_AUTENTICADO</c> (el comercial no existe),
    /// <c>COTIZACION_NO_ENCONTRADA</c> (el folio no existe o no es del comercial: 404, sin llamar al modelo) y <c>NO_ENCONTRADO</c> (la
    /// sesión es de otro comercial). Un fallo del proveedor de IA o <see cref="clsMaxIteracionesExcedidasException"/> se propagan como
    /// excepción. La respuesta trae, si la hubo, la acción propuesta armada por el servidor (<see cref="clsResultadoTurno.AccionPropuesta"/>).
    /// </summary>
    Task<clsResultado<clsResultadoTurno>> EjecutarTurnoAsync(clsSolicitudTurnoFolio solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asistente por folio (ADR-018 §2b), único agente de Cotifly. Loop manual (ADR-003: <see cref="IChatClient"/> con las
/// declaraciones portables y <c>kernel.InvokeAsync</c> por llamada, con el filtro de auditoría; respuesta final aparte con
/// <see cref="IClsValidadorRespuestaAgente"/>), pero:
/// <list type="bullet">
/// <item>Solo sobre la cotización del folio, del comercial del token (otra → 404 antes de tocar la sesión o el modelo). La sesión
/// queda ligada al comercial (<c>spaceId</c>) y al folio (<c>threadId</c> con el folio).</item>
/// <item>Solo el plugin <c>asistente</c> (<see cref="clsHerramientasAsistenteFolio"/>): leer y proponer. Una herramienta fuera del plugin
/// (<c>generar_pdf</c>, <c>calcular_precio</c>...) responde <c>HERRAMIENTA_DESCONOCIDA</c> como cualquier inexistente.</item>
/// <item><b>Nunca escribe datos de negocio:</b> solo guarda el turno (eventos de la conversación y el estado de la sesión). La
/// propuesta (con su <c>resumen</c> con precios) sale en la respuesta y NO queda en el historial que vuelve al modelo.</item>
/// <item>El estado de la conversación es mínimo (folio e id de la cotización): no lleva costo, precio ni TRM, y el validador de la
/// respuesta final rechaza cualquier monto que el modelo escriba sin respaldo (nada de lo que ve lo respalda).</item>
/// </list>
/// </summary>
public sealed class clsAsistenteFolioRunner(
    IChatClient chat,
    IClsSesionRepository sesiones,
    IClsUsuarioWebRepository usuarios,
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsCalendarioNegocio calendario,
    IClsValidadorRespuestaAgente validador,
    IClsAuditoriaService auditoria,
    IClsCandadoSesion candadoSesion,
    clsContextoAsistenteFolio contextoFolio,
    clsHerramientasAsistenteFolio herramientas,
    IOptions<clsOpcionesAgente> opcionesAgente,
    IOptions<clsOpcionesSesiones> opcionesSesiones,
    ILogger<clsAsistenteFolioRunner> logger) : IClsAsistenteFolioRunner
{
    private readonly List<clsEventoConversacion> _pendientes = [];

    public async Task<clsResultado<clsResultadoTurno>> EjecutarTurnoAsync(clsSolicitudTurnoFolio solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitud.Mensaje);
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitud.Folio);

        var medicion = new clsMedicionTurno();
        var usuario = solicitud.UsuarioId == Guid.Empty ? null : await usuarios.ObtenerPorIdAsync(solicitud.UsuarioId, cancellationToken);
        if (usuario is null)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(solicitud.Folio.Trim(), usuario.Id, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        await using var candado = await candadoSesion.IntentarTomarAsync(
            solicitud.SpaceId, solicitud.ThreadId, TimeSpan.FromSeconds(opcionesSesiones.Value.EsperaTurnoSegundos), cancellationToken)
            ?? throw new clsTurnoEnCursoException();

        var sesion = await sesiones.ObtenerOCrearAsync(solicitud.SpaceId, solicitud.ThreadId, usuario.Id, cancellationToken);
        if (!sesion.AsignarUsuario(usuario.Id))
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, clsTextosAgente.SesionNoEncontrada);
        }

        var historial = await sesiones.HistorialAsync(solicitud.SpaceId, solicitud.ThreadId, cancellationToken);
        var estado = clsEstadoConversacion.Vacio with
        {
            CotizacionId = cotizacion.Id,
            FolioEnProgreso = cotizacion.Folio,
            Version = cotizacion.Version,
            Moneda = cotizacion.Moneda,
        };
        var marcas = new clsMarcasTurno(reloj.AhoraUtc, historial.Count > 0 ? historial[^1].CreatedAt : null);

        // El mensaje del comercial se guarda primero: si el proveedor falla, la conversación no pierde la pregunta.
        AgregarEvento(solicitud, clsEventoConversacion.RolUsuario, solicitud.Mensaje, null, usuario.Id, marcas.Siguiente());
        GuardarEventosPendientes();
        sesion.ActualizarEstadoAgente(estado);
        sesion.Tocar(reloj.AhoraUtc);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);

        var mensajesDelComercial = historial
            .Where(e => e.Rol == clsEventoConversacion.RolUsuario && !string.IsNullOrWhiteSpace(e.Contenido))
            .Select(e => e.Contenido!)
            .Append(solicitud.Mensaje)
            .ToList();
        var linea = cotizacion.PrimeraRuta?.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, cancellationToken) : null;
        contextoFolio.Iniciar(usuario.Id, cotizacion, linea?.LineaNegocio, solicitud.ThreadId, linea?.MapeoLineasNexo);

        var mensajes = new List<ChatMessage>
        {
            new(ChatRole.System, clsPromptAsistenteFolio.Construir(usuario.NombreCompleto ?? usuario.NombreUsuario, calendario.Hoy, cotizacion.Folio)),
        };
        mensajes.AddRange(Historial(historial, solicitud.Mensaje));

        var kernel = ConstruirKernel(out var funciones);
        var declaraciones = clsEsquemaPortableHerramientas.Declarar(funciones.Values);
        medicion.Sumar(clsMedicionTurno.FasePreparacion, medicion.Total);
        var turno = new clsDatosTurno(solicitud, kernel, funciones, declaraciones, mensajes, marcas, estado, mensajesDelComercial, medicion);
        try
        {
            var resultado = await CorrerLoopAsync(turno, cancellationToken);
            RegistrarDuracion(cotizacion.Folio, medicion, resultado, null);
            return resultado;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is clsMaxIteracionesExcedidasException)
            {
                logger.LogWarning(
                    "El asistente de {Folio} pidió herramientas sin cerrar el turno: se cortó en {Maximo} iteraciones.", cotizacion.Folio, opcionesAgente.Value.MaxIteraciones);
            }

            try
            {
                using (medicion.Medir(clsMedicionTurno.FaseGuardado))
                {
                    await GuardarAsync(solicitud, estado, cancellationToken);
                }
            }
            catch (Exception guardado) when (guardado is not OperationCanceledException)
            {
                logger.LogWarning(guardado, "No se pudo guardar el turno interrumpido del asistente de {Folio}.", cotizacion.Folio);
            }

            RegistrarDuracion(cotizacion.Folio, medicion, null, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Log de duración por fase del turno: total, preparación, llamadas al modelo con herramientas, herramientas,
    /// respuesta final y guardado, más llamadas, iteraciones, cierre, reintentos del validador, tokens y modelos (un modelo distinto del
    /// principal es un respaldo de <c>IA:Fallbacks</c>). La espera del limitador <c>IA:Rpm</c> va incluida en las llamadas y la registra
    /// Infraestructura aparte. Sin textos del comercial ni del modelo.
    /// </summary>
    private void RegistrarDuracion(string folio, clsMedicionTurno medicion, clsResultadoTurno? turno, string? fallo)
    {
        logger.LogInformation(
            "Turno del asistente de {Folio}: {TotalMs} ms (preparación {PreparacionMs} ms; modelo con herramientas {ModeloMs} ms en {LlamadasModelo} llamadas; herramientas {HerramientasMs} ms; respuesta final {FinalMs} ms en {LlamadasFinal} llamadas; guardado {GuardadoMs} ms). Iteraciones {Iteraciones}, cierre {Cierre}, reintentos del validador {Reintentos}, respaldo {EsRespaldo}, propuesta {Propuesta}, tokens {TokensEntrada}/{TokensSalida}, modelos {Modelos}, fallo {Fallo}.",
            folio,
            Ms(medicion.Total),
            Ms(medicion.Fase(clsMedicionTurno.FasePreparacion)),
            Ms(medicion.Fase(clsMedicionTurno.FaseModelo)),
            medicion.Llamadas(clsMedicionTurno.FaseModelo),
            Ms(medicion.Fase(clsMedicionTurno.FaseHerramientas)),
            Ms(medicion.Fase(clsMedicionTurno.FaseRespuestaFinal)),
            medicion.Llamadas(clsMedicionTurno.FaseRespuestaFinal),
            Ms(medicion.Fase(clsMedicionTurno.FaseGuardado)),
            turno?.Iteraciones,
            turno?.Cierre,
            turno?.ReintentosRespuesta,
            turno?.EsRespaldo,
            turno?.AccionPropuesta?.Tipo,
            medicion.TokensEntrada,
            medicion.TokensSalida,
            string.Join(",", medicion.Modelos),
            fallo);
    }

    private static long Ms(TimeSpan duracion) => (long)Math.Round(duracion.TotalMilliseconds);

    /// <summary>
    /// Loop del turno (menos llamadas sin relajar ninguna regla). Cada ronda pide herramientas al modelo y las corre.
    /// Termina cuando el modelo responde sin herramientas o, con <c>IA:PropuestaDirecta</c>, en cuanto una ronda arma una propuesta.
    /// La respuesta sin herramientas se valida como respuesta final con las mismas reglas (<c>IA:RespuestaFinalEnLoop</c>): si pasa, no
    /// hay llamada final aparte. Si no, o tras una propuesta, la llamada final la hace el validador, sin reintento si ya hay propuesta
    /// (su respaldo es el texto fijo del servidor).
    /// </summary>
    private async Task<clsResultadoTurno> CorrerLoopAsync(clsDatosTurno turno, CancellationToken cancellationToken)
    {
        var opciones = opcionesAgente.Value;
        var maximo = opciones.MaxIteraciones;
        var mensajes = turno.Mensajes;
        var solicitud = turno.Solicitud;
        var chatLoop = turno.Medicion.Cliente(chat, clsMedicionTurno.FaseModelo);
        var invocadas = new List<string>();
        var salidas = new List<clsResultadoHerramienta>();
        var contexto = new clsContextoValidacionRespuesta(turno.Estado, salidas, turno.MensajesDelComercial, turno.Funciones.Keys.ToList());
        string? detenerPor = null;
        clsRespuestaAgenteEstructurada? enLoop = null;
        var iteraciones = 0;

        while (detenerPor is null)
        {
            if (iteraciones == maximo)
            {
                throw clsMaxIteracionesExcedidasException.Con(maximo);
            }

            iteraciones++;
            var respuesta = await chatLoop.GetResponseAsync(mensajes, Opciones(turno.Declaraciones), cancellationToken);
            var llamadas = respuesta.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (llamadas.Count == 0)
            {
                detenerPor = respuesta.FinishReason?.Value ?? "stop";
                enLoop = opciones.RespuestaFinalEnLoop ? RespuestaDelLoop(respuesta.Text, contexto) : null;
                break;
            }

            mensajes.AddRange(respuesta.Messages);
            AgregarEvento(solicitud, clsEventoConversacion.RolAsistente, Vacio(respuesta.Text), new
            {
                invocaciones = llamadas.Select(l => new { nombre = l.Name, input = l.Arguments }).ToList(),
            }, null, turno.Marcas.Siguiente());

            var resultados = new List<AIContent>();
            var traza = new List<object>();
            using (turno.Medicion.Medir(clsMedicionTurno.FaseHerramientas))
            {
                foreach (var llamada in llamadas)
                {
                    var resultado = await EjecutarAsync(turno.Kernel, turno.Funciones, llamada, cancellationToken);
                    salidas.Add(resultado);
                    if (turno.Funciones.ContainsKey(llamada.Name))
                    {
                        invocadas.Add(llamada.Name);
                    }

                    var paraModelo = resultado.ParaModelo();
                    resultados.Add(new FunctionResultContent(llamada.CallId, JsonSerializer.Serialize(paraModelo, clsJsonAplicacion.SnakeCase)));
                    traza.Add(new { nombre = llamada.Name, salida = paraModelo });
                }
            }

            mensajes.Add(new ChatMessage(ChatRole.Tool, resultados));
            AgregarEvento(solicitud, clsEventoConversacion.RolHerramienta, null, new { resultados = traza }, null, turno.Marcas.Siguiente());
            if (opciones.PropuestaDirecta && contextoFolio.Propuesta is not null)
            {
                detenerPor = CierrePropuesta;
            }
        }

        clsRespuestaFinalAgente final;
        string cierre;
        if (enLoop is not null)
        {
            final = new clsRespuestaFinalAgente(enLoop, 0, 0, false, []);
            cierre = CierreLoop;
        }
        else
        {
            using (turno.Medicion.Medir(clsMedicionTurno.FaseRespuestaFinal))
            {
                final = await validador.ObtenerRespuestaFinalAsync(
                    turno.Medicion.Cliente(chat, clsMedicionTurno.FaseRespuestaFinal),
                    mensajes,
                    contexto with { ReintentarSiFalla = contextoFolio.Propuesta is null },
                    cancellationToken);
            }

            cierre = CierreLlamadaFinal;
        }

        iteraciones += final.Llamadas;
        AgregarEvento(solicitud, clsEventoConversacion.RolAsistente, final.Respuesta.Texto, null, null, turno.Marcas.Siguiente());
        using (turno.Medicion.Medir(clsMedicionTurno.FaseGuardado))
        {
            await GuardarAsync(solicitud, turno.Estado, cancellationToken);
        }

        return new clsResultadoTurno(final.Respuesta.Texto, iteraciones, invocadas, detenerPor, salidas)
        {
            Respuesta = Coherente(final.Respuesta, contextoFolio.Propuesta is not null),
            EsRespaldo = final.EsRespaldo,
            ReintentosRespuesta = final.Reintentos,
            AccionPropuesta = contextoFolio.Propuesta,
            Cierre = cierre,
        };
    }

    /// <summary>
    /// El tipo de la respuesta lo decide el servidor, no el modelo: con una propuesta armada siempre es
    /// <c>confirmacion_requerida</c> (el comercial la confirma con el botón) y sin propuesta nunca lo es (no hay nada que confirmar).
    /// </summary>
    private static clsRespuestaAgenteEstructurada Coherente(clsRespuestaAgenteEstructurada respuesta, bool hayPropuesta) =>
        hayPropuesta && respuesta.Tipo != TipoRespuestaAgente.ConfirmacionRequerida
            ? respuesta with { Tipo = TipoRespuestaAgente.ConfirmacionRequerida, RequiereConfirmacion = true, AccionesSugeridas = null }
            : !hayPropuesta && respuesta.Tipo == TipoRespuestaAgente.ConfirmacionRequerida
                ? respuesta with { Tipo = TipoRespuestaAgente.Conversacional, RequiereConfirmacion = false, AccionesSugeridas = null }
                : respuesta;

    /// <summary>Motivo de fin del loop cuando una ronda armó una propuesta (<c>IA:PropuestaDirecta</c>).</summary>
    public const string CierrePropuesta = "propuesta";

    /// <summary>La respuesta final salió de la última llamada del loop (<c>IA:RespuestaFinalEnLoop</c>).</summary>
    public const string CierreLoop = "loop";

    /// <summary>La respuesta final salió de la llamada final aparte del validador.</summary>
    public const string CierreLlamadaFinal = "llamada_final";

    /// <summary>
    /// La respuesta sin herramientas validada como respuesta final, con las MISMAS reglas que la llamada final (forma, coherencia,
    /// montos y folios). Null si no pasa: entonces la pide el validador, con su reintento. Solo registra los códigos de los fallos.
    /// </summary>
    private clsRespuestaAgenteEstructurada? RespuestaDelLoop(string? texto, clsContextoValidacionRespuesta contexto)
    {
        var validacion = clsReglasRespuestaAgente.Validar(texto, contexto);
        if (validacion.EsValida)
        {
            return validacion.Respuesta;
        }

        logger.LogDebug(
            "La respuesta sin herramientas del asistente no sirve como respuesta final ({Motivos}); se pide la llamada final.",
            string.Join(", ", validacion.Fallos.Select(f => f.Codigo).Distinct(StringComparer.Ordinal)));
        return null;
    }

    /// <summary>Lo que el loop necesita del turno (armado en <see cref="EjecutarTurnoAsync"/>).</summary>
    private sealed record clsDatosTurno(
        clsSolicitudTurnoFolio Solicitud,
        Kernel Kernel,
        IReadOnlyDictionary<string, KernelFunction> Funciones,
        IList<AITool> Declaraciones,
        List<ChatMessage> Mensajes,
        clsMarcasTurno Marcas,
        clsEstadoConversacion Estado,
        IReadOnlyList<string> MensajesDelComercial,
        clsMedicionTurno Medicion);

    /// <summary>Kernel del turno con el plugin <c>asistente</c> y el filtro de auditoría (entrada del LLM y salida sin montos).</summary>
    private Kernel ConstruirKernel(out IReadOnlyDictionary<string, KernelFunction> funciones)
    {
        var kernel = new Kernel();
        var plugin = KernelPluginFactory.CreateFromObject(herramientas, clsNombresHerramientas.PluginAsistente);
        kernel.Plugins.Add(plugin);
        kernel.FunctionInvocationFilters.Add(new clsAuditoriaHerramientaFilter(auditoria, contextoFolio));
        funciones = plugin.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return kernel;
    }

    private async Task<clsResultadoHerramienta> EjecutarAsync(
        Kernel kernel, IReadOnlyDictionary<string, KernelFunction> funciones, FunctionCallContent llamada, CancellationToken cancellationToken)
    {
        if (!funciones.TryGetValue(llamada.Name, out var funcion))
        {
            logger.LogWarning("El modelo pidió la herramienta inexistente o retirada '{Herramienta}'.", llamada.Name);
            return clsResultadoHerramienta.Fallo(
                llamada.Name,
                clsCodigosError.HerramientaDesconocida,
                $"La herramienta '{llamada.Name}' no existe. Usa solo: {string.Join(", ", funciones.Keys)}.");
        }

        var argumentos = new KernelArguments();
        foreach (var (clave, valor) in llamada.Arguments ?? new Dictionary<string, object?>())
        {
            argumentos[clave] = valor;
        }

        try
        {
            var resultado = await kernel.InvokeAsync(funcion, argumentos, cancellationToken);
            return resultado.GetValue<object>() as clsResultadoHerramienta
                   ?? clsResultadoHerramienta.Fallo(llamada.Name, clsCodigosError.ErrorInterno, "La herramienta no devolvió un resultado.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Fallo inesperado en la herramienta {Herramienta} del asistente.", llamada.Name);
            var argumentosInvalidos = ex is KernelException or ArgumentException or JsonException or FormatException or InvalidCastException;
            return clsResultadoHerramienta.Fallo(
                llamada.Name,
                clsCodigosError.ErrorInterno,
                argumentosInvalidos
                    ? $"Los argumentos de {llamada.Name} no son válidos: revisa los nombres y tipos del esquema de la herramienta."
                    : "Ocurrió un error interno procesando la solicitud. Reintenta más tarde o contacta a soporte.");
        }
    }

    private ChatOptions Opciones(IList<AITool> declaraciones) => new()
    {
        Tools = declaraciones,
        ToolMode = ChatToolMode.Auto,
        MaxOutputTokens = opcionesAgente.Value.MaxTokens,
    };

    /// <summary>Historial de texto (comercial y asistente) + el mensaje actual, recortado por la ventana de tokens.</summary>
    private IEnumerable<ChatMessage> Historial(IReadOnlyList<clsEventoConversacion> historial, string mensaje)
    {
        var turnos = historial
            .Where(e => (e.Rol == clsEventoConversacion.RolUsuario || e.Rol == clsEventoConversacion.RolAsistente) && !string.IsNullOrWhiteSpace(e.Contenido))
            .Select(e => new clsTurnoTexto(e.Rol, e.Contenido!))
            .Append(new clsTurnoTexto(clsEventoConversacion.RolUsuario, mensaje))
            .ToList();
        return clsVentanaHistorial.Aplicar(turnos, opcionesSesiones.Value.LimiteTokens)
            .Select(t => new ChatMessage(t.Rol == clsEventoConversacion.RolUsuario ? ChatRole.User : ChatRole.Assistant, t.Contenido));
    }

    private void AgregarEvento(clsSolicitudTurnoFolio solicitud, string rol, string? contenido, object? herramientasUsadas, Guid? usuarioId, DateTimeOffset cuando)
    {
        var json = herramientasUsadas is null ? null : JsonSerializer.Serialize(herramientasUsadas, clsJsonAplicacion.SnakeCase);
        var evento = clsEventoConversacion.Crear(solicitud.SpaceId, solicitud.ThreadId, rol, contenido, usuarioId, json, cuando);
        if (evento.EsExitoso)
        {
            _pendientes.Add(evento.Valor);
        }
    }

    private void GuardarEventosPendientes()
    {
        foreach (var evento in _pendientes)
        {
            sesiones.AgregarEvento(evento);
        }

        _pendientes.Clear();
    }

    private async Task GuardarAsync(clsSolicitudTurnoFolio solicitud, clsEstadoConversacion estado, CancellationToken cancellationToken)
    {
        var sesion = await sesiones.ObtenerOCrearAsync(solicitud.SpaceId, solicitud.ThreadId, solicitud.UsuarioId, cancellationToken);
        sesion.ActualizarEstadoAgente(estado);
        GuardarEventosPendientes();
        sesion.Tocar(reloj.AhoraUtc);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
    }

    private static string? Vacio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>Marcas de tiempo estrictamente crecientes para ordenar los eventos del turno después de los anteriores.</summary>
    private sealed class clsMarcasTurno(DateTimeOffset ahora, DateTimeOffset? ultimaAnterior)
    {
        private DateTimeOffset _ultima = ultimaAnterior is { } anterior && anterior >= ahora ? anterior : ahora.AddMilliseconds(-1);

        public DateTimeOffset Siguiente()
        {
            _ultima = _ultima.AddMilliseconds(1);
            return _ultima;
        }
    }
}
