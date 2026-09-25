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
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
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

/// <summary>Un turno del agente cotizador (consola web y Google Chat).</summary>
public interface IClsAgenteRunner
{
    /// <summary>
    /// Corre el turno: registra el mensaje, arma el contexto, ejecuta el loop de herramientas y pide la respuesta final. Fallos de
    /// negocio: <c>NO_AUTENTICADO</c> (el comercial no existe) y <c>NO_ENCONTRADO</c> (la sesión es de otro usuario, 404 del contrato).
    /// Un fallo del proveedor de IA, <see cref="clsMaxIteracionesExcedidasException"/> o <see cref="clsRespuestaAgenteInvalidaException"/>
    /// se propagan como excepción: el canal responde el error controlado.
    /// </summary>
    Task<clsResultado<clsResultadoTurno>> EjecutarTurnoAsync(clsSolicitudTurno solicitud, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loop agéntico (port de <c>AgentRunner</c> de <c>orquestador/agente.py</c>, ADR-003) sobre <see cref="IChatClient"/> y un
/// <see cref="Kernel"/> de Semantic Kernel con el plugin <c>cotizador</c>:
/// <list type="number">
/// <item>Sesión <c>spaceId</c>/<c>threadId</c> ligada al comercial (otro usuario → <c>NO_ENCONTRADO</c>); el mensaje se registra
/// primero (<c>EventosConversacion</c>) y se reconoce una confirmación explícita o la elección de un cliente candidato.</item>
/// <item>Contexto: prompt de sistema (skill + "Contexto del comercial" + "Estado resuelto") e historial de texto con ventana por
/// tokens que conserva el primer mensaje (<c>Sesiones:LimiteTokens</c>).</item>
/// <item>Loop hasta <c>IA:MaxIteraciones</c>: el modelo recibe solo las declaraciones de las herramientas; cada llamada se ejecuta
/// a mano con <c>kernel.InvokeAsync</c> (sin <c>UseFunctionInvocation()</c>), así corren los filtros de auditoría e inyección de
/// estado. Una herramienta inexistente responde <c>HERRAMIENTA_DESCONOCIDA</c> como resultado; el error estructurado de una
/// herramienta llega al modelo tal cual. Tras cada herramienta, <c>clsEstadoConversacionService</c> actualiza el estado.</item>
/// <item>Respuesta final en una llamada aparte sin herramientas (<see cref="IClsValidadorRespuestaAgente.FormatoRespuesta"/>),
/// validada con <see cref="IClsValidadorRespuestaAgente"/>; se persisten el turno y el estado (<c>Sesiones.estadoAgente</c>).</item>
/// </list>
/// </summary>
public sealed class clsAgenteRunner(
    IChatClient chat,
    IClsSesionRepository sesiones,
    IClsUsuarioWebRepository usuarios,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsCalendarioNegocio calendario,
    IClsEstadoConversacionService estadoService,
    IClsValidadorRespuestaAgente validador,
    IClsAuditoriaService auditoria,
    clsContextoTurnoAgente contexto,
    clsHerramientasCotizador herramientas,
    IOptions<clsOpcionesAgente> opcionesAgente,
    IOptions<clsOpcionesSesiones> opcionesSesiones,
    IOptions<clsOpcionesCorreo> opcionesCorreo,
    ILoggerFactory registros) : IClsAgenteRunner
{
    public const string RolHerramienta = "herramienta";

    private readonly ILogger _logger = registros.CreateLogger<clsAgenteRunner>();
    private readonly List<clsEventoConversacion> _pendientes = [];

    public async Task<clsResultado<clsResultadoTurno>> EjecutarTurnoAsync(clsSolicitudTurno solicitud, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitud.Mensaje);

        var usuario = solicitud.UsuarioId == Guid.Empty ? null : await usuarios.ObtenerPorIdAsync(solicitud.UsuarioId, cancellationToken);
        if (usuario is null)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        var sesion = await sesiones.ObtenerOCrearAsync(solicitud.SpaceId, solicitud.ThreadId, usuario.Id, cancellationToken);
        if (!sesion.AsignarUsuario(usuario.Id))
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, clsTextosAgente.SesionNoEncontrada);
        }

        var historial = await sesiones.HistorialAsync(solicitud.SpaceId, solicitud.ThreadId, cancellationToken);
        var marcas = new clsMarcasTurno(reloj.AhoraUtc, historial.Count > 0 ? historial[^1].CreatedAt : null);
        var reconocido = estadoService.ReconocerMensaje(sesion.EstadoAgente ?? clsEstadoConversacion.Vacio, solicitud.Mensaje);
        var estado = reconocido.Estado;

        // El mensaje del comercial se guarda primero: si el proveedor falla, la conversación no pierde la pregunta (como el Python).
        AgregarEvento(solicitud, clsEventoConversacion.RolUsuario, solicitud.Mensaje, null, usuario.Id, marcas.Siguiente());
        GuardarEventosPendientes();
        sesion.ActualizarEstadoAgente(estado);
        sesion.Tocar(reloj.AhoraUtc);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);

        contexto.Iniciar(usuario.Id, solicitud.SpaceId, solicitud.ThreadId, solicitud.Mensaje, estado, reconocido.Confirmado);
        var mensajes = new List<ChatMessage>
        {
            new(ChatRole.System, clsPromptSistema.Construir(usuario.NombreCompleto ?? usuario.NombreUsuario, usuario.Email, calendario.Hoy, estado)),
        };
        mensajes.AddRange(Historial(historial, solicitud.Mensaje));

        var kernel = ConstruirKernel(out var funciones);
        var declaraciones = funciones.Values.Select(f => (AITool)AIFunctionFactory.CreateDeclaration(f.Name, f.Description, f.JsonSchema)).ToList();
        try
        {
            return await CorrerLoopAsync(solicitud, kernel, funciones, declaraciones, mensajes, marcas, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lo que alcanzó a pasar en el turno (herramientas, estado) queda guardado aunque el proveedor falle.
            try
            {
                await GuardarAsync(solicitud, cancellationToken);
            }
            catch (Exception guardado) when (guardado is not OperationCanceledException)
            {
                _logger.LogWarning(guardado, "No se pudo guardar el turno interrumpido de {SpaceId}/{ThreadId}.", solicitud.SpaceId, solicitud.ThreadId);
            }

            throw;
        }
    }

    private async Task<clsResultado<clsResultadoTurno>> CorrerLoopAsync(
        clsSolicitudTurno solicitud,
        Kernel kernel,
        IReadOnlyDictionary<string, KernelFunction> funciones,
        IList<AITool> declaraciones,
        List<ChatMessage> mensajes,
        clsMarcasTurno marcas,
        CancellationToken cancellationToken)
    {
        var maximo = opcionesAgente.Value.MaxIteraciones;
        var invocadas = new List<string>();
        var salidas = new List<clsResultadoHerramienta>();
        string? detenerPor = null;
        var iteraciones = 0;

        while (detenerPor is null)
        {
            if (iteraciones == maximo)
            {
                throw clsMaxIteracionesExcedidasException.Con(maximo);
            }

            iteraciones++;
            var respuesta = await chat.GetResponseAsync(mensajes, OpcionesConHerramientas(declaraciones), cancellationToken);
            var llamadas = respuesta.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (llamadas.Count == 0)
            {
                detenerPor = respuesta.FinishReason?.Value ?? "stop";
                break;
            }

            mensajes.AddRange(respuesta.Messages);
            AgregarEvento(solicitud, clsEventoConversacion.RolAsistente, Vacio(respuesta.Text), new
            {
                invocaciones = llamadas.Select(l => new { nombre = l.Name, input = l.Arguments }).ToList(),
            }, null, marcas.Siguiente());

            var resultados = new List<AIContent>();
            var traza = new List<object>();
            foreach (var llamada in llamadas)
            {
                var (resultado, argumentos) = await EjecutarAsync(kernel, funciones, llamada, cancellationToken);
                salidas.Add(resultado);
                if (funciones.ContainsKey(llamada.Name))
                {
                    invocadas.Add(llamada.Name);
                }

                contexto.ActualizarEstado(estadoService.Aplicar(contexto.Estado, resultado, argumentos));
                var paraModelo = resultado.ParaModelo();
                resultados.Add(new FunctionResultContent(llamada.CallId, JsonSerializer.Serialize(paraModelo, clsJsonAplicacion.SnakeCase)));
                traza.Add(new { nombre = llamada.Name, salida = paraModelo });
            }

            mensajes.Add(new ChatMessage(ChatRole.Tool, resultados));
            AgregarEvento(solicitud, RolHerramienta, null, new { resultados = traza }, null, marcas.Siguiente());
        }

        // Respuesta final en una llamada aparte sin herramientas (ADR-003): mismo camino de validación para todos los turnos.
        iteraciones++;
        var final = await chat.GetResponseAsync(mensajes, OpcionesFinales(), cancellationToken);
        var validada = await validador.ValidarAsync(final.Text, cancellationToken);
        if (validada.EsFallo)
        {
            throw new clsRespuestaAgenteInvalidaException($"{validada.Error.Codigo}: {validada.Error.Mensaje}");
        }

        AgregarEvento(solicitud, clsEventoConversacion.RolAsistente, validada.Valor, null, null, marcas.Siguiente());
        await GuardarAsync(solicitud, cancellationToken);
        return new clsResultadoTurno(validada.Valor, iteraciones, invocadas, detenerPor, salidas);
    }

    /// <summary>
    /// Kernel del turno con el plugin <c>cotizador</c> y los filtros: auditoría por fuera (ve también los rechazos) e inyección de
    /// estado por dentro. <c>enviar_correo</c> no se declara con <c>Correo:Habilitado=false</c> (como el Python).
    /// </summary>
    private Kernel ConstruirKernel(out IReadOnlyDictionary<string, KernelFunction> funciones)
    {
        var kernel = new Kernel();
        var plugin = KernelPluginFactory.CreateFromObject(herramientas, clsNombresHerramientas.Plugin);
        kernel.Plugins.Add(plugin);
        kernel.FunctionInvocationFilters.Add(new clsAuditoriaHerramientaFilter(auditoria, contexto));
        kernel.FunctionInvocationFilters.Add(new clsInyeccionEstadoFilter(contexto, registros.CreateLogger<clsInyeccionEstadoFilter>()));

        var correoHabilitado = opcionesCorreo.Value.Habilitado;
        funciones = plugin
            .Where(f => correoHabilitado || f.Name != clsNombresHerramientas.EnviarCorreo)
            .ToDictionary(f => f.Name, StringComparer.Ordinal);
        return kernel;
    }

    private async Task<(clsResultadoHerramienta Resultado, IReadOnlyDictionary<string, object?> Argumentos)> EjecutarAsync(
        Kernel kernel, IReadOnlyDictionary<string, KernelFunction> funciones, FunctionCallContent llamada, CancellationToken cancellationToken)
    {
        var argumentos = new KernelArguments();
        foreach (var (clave, valor) in llamada.Arguments ?? new Dictionary<string, object?>())
        {
            argumentos[clave] = valor;
        }

        if (!funciones.TryGetValue(llamada.Name, out var funcion))
        {
            _logger.LogWarning("El modelo pidió la herramienta inexistente '{Herramienta}'.", llamada.Name);
            return (clsResultadoHerramienta.Fallo(
                llamada.Name,
                clsCodigosError.HerramientaDesconocida,
                $"La herramienta '{llamada.Name}' no existe. Usa solo: {string.Join(", ", funciones.Keys)}."), argumentos);
        }

        try
        {
            var resultado = await kernel.InvokeAsync(funcion, argumentos, cancellationToken);
            return (resultado.GetValue<object>() as clsResultadoHerramienta
                    ?? clsResultadoHerramienta.Fallo(llamada.Name, clsCodigosError.ErrorInterno, "La herramienta no devolvió un resultado."), argumentos);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Red de seguridad (como _tool_segura del MCP): el modelo recibe siempre el contrato de error, nunca la excepción.
            _logger.LogError(ex, "Fallo inesperado en la herramienta {Herramienta}.", llamada.Name);
            var argumentosInvalidos = ex is KernelException or ArgumentException or JsonException or FormatException or InvalidCastException;
            return (clsResultadoHerramienta.Fallo(
                llamada.Name,
                clsCodigosError.ErrorInterno,
                argumentosInvalidos
                    ? $"Los argumentos de {llamada.Name} no son válidos: revisa los nombres y tipos del esquema de la herramienta."
                    : "Ocurrió un error interno procesando la solicitud. Reintenta más tarde o contacta a soporte."), argumentos);
        }
    }

    private ChatOptions OpcionesConHerramientas(IList<AITool> declaraciones) => new()
    {
        Tools = declaraciones,
        ToolMode = ChatToolMode.Auto,
        MaxOutputTokens = opcionesAgente.Value.MaxTokens,
    };

    private ChatOptions OpcionesFinales() => new()
    {
        MaxOutputTokens = opcionesAgente.Value.MaxTokens,
        ResponseFormat = validador.FormatoRespuesta,
    };

    /// <summary>Historial de texto (user/assistant con contenido) + el mensaje actual, recortado por la ventana de tokens.</summary>
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

    /// <summary>
    /// Acumula el evento del turno; se agregan al repositorio al guardar. No se agregan antes porque una herramienta puede deshacer
    /// su transacción y limpiar el seguimiento de cambios (candado del folio): se perderían los eventos pendientes.
    /// </summary>
    private void AgregarEvento(clsSolicitudTurno solicitud, string rol, string? contenido, object? herramientasUsadas, Guid? usuarioId, DateTimeOffset cuando)
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

    /// <summary>
    /// Guarda el estado y los eventos del turno. La sesión se vuelve a leer: una herramienta pudo deshacer su transacción y limpiar el
    /// seguimiento de cambios (candado del folio), y con él la entidad cargada al empezar.
    /// </summary>
    private async Task GuardarAsync(clsSolicitudTurno solicitud, CancellationToken cancellationToken)
    {
        var sesion = await sesiones.ObtenerOCrearAsync(solicitud.SpaceId, solicitud.ThreadId, solicitud.UsuarioId, cancellationToken);
        sesion.ActualizarEstadoAgente(contexto.Estado);
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
