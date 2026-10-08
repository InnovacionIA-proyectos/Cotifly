using System.Diagnostics.Metrics;
using System.Text;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Validador de la respuesta final del agente (HU-003.10, issue #16; equivalente .NET de <c>Instructor</c>, ADR-003 "Implementación
/// (#16)"). Reemplaza al validador de paso de #12:
/// <list type="number">
/// <item>Llamada final aparte y <b>sin herramientas</b> (algunos proveedores no combinan herramientas con JSON Schema), con
/// <c>ChatResponseFormat.ForJsonSchema</c> sobre <see cref="clsRespuestaAgenteEstructurada"/> y una instrucción final con las
/// reglas (en el modo sin esquema, también el esquema).</item>
/// <item>Validación manual con <see cref="clsReglasRespuestaAgente"/> (forma, tipos, enums, largos, acciones, montos y folios).</item>
/// <item>Si no valida: <b>un</b> reintento con la respuesta inválida y un mensaje de corrección que enumera los fallos (salvo con
/// <see cref="clsContextoValidacionRespuesta.ReintentarSiFalla"/> en false: el asistente ya armó una propuesta, HU-013.16).</item>
/// <item>Si vuelve a fallar: respaldo <c>{tipo: error, texto: clsTextosAgente.RespuestaNoEstructurada}</c>.</item>
/// </list>
/// Proveedores sin JSON Schema: <c>IA:SoportaJsonSchema=false</c> pide la respuesta sin formato (el esquema va en la instrucción).
/// Si el proveedor rechaza el esquema en ejecución (HTTP 400/422 cuyo mensaje nombra <c>response_format</c>/<c>json_schema</c>, o
/// <see cref="NotSupportedException"/> del adaptador), se repite la llamada sin formato y se sigue así durante
/// <c>IA:MinutosSinJsonSchema</c>; después se vuelve a probar con esquema (re-sonda). Los fallos se registran solo con sus códigos
/// (nunca el texto, que puede traer datos del cliente) y se cuentan en el medidor <see cref="NombreMedidor"/>
/// (<c>dotnet-counters monitor --counters Cotifly.Agente</c>).
/// </summary>
public sealed class clsValidadorRespuestaAgente : IClsValidadorRespuestaAgente
{
    public const string NombreMedidor = "Cotifly.Agente";
    public const string MetricaInvalidas = "cotifly.agente.respuesta_final.invalidas";
    public const string MetricaReintentos = "cotifly.agente.respuesta_final.reintentos";
    public const string MetricaRespaldos = "cotifly.agente.respuesta_final.respaldos";
    public const string MetricaEsquemaRechazado = "cotifly.agente.esquema_rechazado";

    /// <summary>Clave de <c>ChatOptions.AdditionalProperties</c> que Microsoft.Extensions.AI.OpenAI lee como <c>jsonSchemaIsStrict</c>.</summary>
    public const string ClaveEstricto = "strict";

    /// <summary>Largo máximo de la respuesta inválida que se devuelve al modelo en el reintento.</summary>
    private const int LargoMaximoEco = 6000;

    private readonly IOptions<clsOpcionesAgente> _opciones;
    private readonly ILogger<clsValidadorRespuestaAgente> _logger;
    private readonly IClsReloj _reloj;
    private readonly Counter<long> _invalidas;
    private readonly Counter<long> _reintentos;
    private readonly Counter<long> _respaldos;
    private readonly Counter<long> _esquemaRechazado;
    private readonly clsInterruptorJsonSchema _interruptor;

    /// <param name="interruptor">Apagado del esquema compartido con el redactor de textos (HU-013.5); sin él, uno propio.</param>
    public clsValidadorRespuestaAgente(
        IOptions<clsOpcionesAgente> opciones, ILogger<clsValidadorRespuestaAgente> logger, IMeterFactory medidores, IClsReloj reloj,
        clsInterruptorJsonSchema? interruptor = null)
    {
        ArgumentNullException.ThrowIfNull(medidores);
        _opciones = opciones;
        _logger = logger;
        _reloj = reloj;
        _interruptor = interruptor ?? new clsInterruptorJsonSchema();
        Medidor = medidores.Create(NombreMedidor);
        _invalidas = Medidor.CreateCounter<long>(MetricaInvalidas, description: "Respuestas finales del modelo que no pasaron la validación (etiqueta motivo).");
        _reintentos = Medidor.CreateCounter<long>(MetricaReintentos, description: "Reintentos de la respuesta final por validación fallida.");
        _respaldos = Medidor.CreateCounter<long>(MetricaRespaldos, description: "Turnos que terminaron en la respuesta de respaldo del validador.");
        _esquemaRechazado = Medidor.CreateCounter<long>(MetricaEsquemaRechazado, description: "Veces que el proveedor rechazó el JSON Schema de la respuesta final.");
    }

    /// <summary>Medidor de las métricas del validador (creado con <see cref="IMeterFactory"/>; el host lo libera).</summary>
    public Meter Medidor { get; }

    /// <summary>Instrucción final en el modo con JSON Schema (el esquema ya viaja en <c>response_format</c>): solo las reglas.</summary>
    public static string InstruccionConEsquema { get; } = CrearInstruccion(incluirEsquema: false);

    /// <summary>Instrucción final en el modo sin JSON Schema: las reglas y el esquema (sin tildes escapadas).</summary>
    public static string InstruccionSinEsquema { get; } = CrearInstruccion(incluirEsquema: true);

    public async Task<clsRespuestaFinalAgente> ObtenerRespuestaFinalAsync(
        IChatClient chat, IReadOnlyList<ChatMessage> mensajes, clsContextoValidacionRespuesta contexto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(mensajes);
        ArgumentNullException.ThrowIfNull(contexto);

        var correccion = new List<ChatMessage>();
        var motivos = new List<string>();
        var llamadas = 0;
        var intentos = contexto.ReintentarSiFalla ? 2 : 1;
        for (var intento = 1; intento <= intentos; intento++)
        {
            var (crudo, hechas) = await PedirAsync(chat, mensajes, correccion, cancellationToken);
            llamadas += hechas;
            var validacion = clsReglasRespuestaAgente.Validar(crudo, contexto);
            if (validacion.EsValida)
            {
                return new clsRespuestaFinalAgente(validacion.Respuesta!, llamadas, intento - 1, false, motivos);
            }

            var codigos = validacion.Fallos.Select(f => f.Codigo).Distinct(StringComparer.Ordinal).ToList();
            motivos.AddRange(codigos);
            foreach (var codigo in codigos)
            {
                _invalidas.Add(1, new KeyValuePair<string, object?>("motivo", codigo));
            }

            _logger.LogWarning(
                "La respuesta final del agente no pasó la validación (intento {Intento} de {Intentos}): {Motivos}.", intento, intentos, string.Join(", ", codigos));
            if (intento < intentos)
            {
                _reintentos.Add(1);
                correccion.Add(new ChatMessage(ChatRole.Assistant, Eco(crudo)));
                correccion.Add(new ChatMessage(ChatRole.User, Correccion(validacion.Fallos)));
            }
        }

        _respaldos.Add(1);
        _logger.LogWarning("La respuesta final del agente no se pudo validar en {Intentos} intento(s); se responde el error controlado.", intentos);
        return new clsRespuestaFinalAgente(clsRespuestaAgenteEstructurada.Respaldo(clsTextosAgente.RespuestaNoEstructurada), llamadas, intentos - 1, true, motivos);
    }

    /// <summary>Una llamada sin herramientas; con el esquema si el proveedor lo admite. Devuelve el texto y las llamadas hechas.</summary>
    private async Task<(string? Texto, int Llamadas)> PedirAsync(
        IChatClient chat, IReadOnlyList<ChatMessage> mensajes, List<ChatMessage> correccion, CancellationToken cancellationToken)
    {
        var conEsquema = _opciones.Value.SoportaJsonSchema && _interruptor.Habilitado(_reloj.AhoraUtc);
        try
        {
            var respuesta = await chat.GetResponseAsync(Conversacion(mensajes, correccion, conEsquema), Opciones(conEsquema), cancellationToken);
            return (respuesta.Text, 1);
        }
        catch (Exception ex) when (conEsquema && !cancellationToken.IsCancellationRequested && EsEsquemaNoSoportado(ex))
        {
            var minutos = _opciones.Value.MinutosSinJsonSchema;
            _interruptor.Apagar(_reloj.AhoraUtc.AddMinutes(minutos));
            _esquemaRechazado.Add(1);
            _logger.LogWarning(
                "El proveedor de IA rechazó response_format con JSON Schema ({Tipo}, estado {Estado}); la respuesta final se pide sin formato durante {Minutos} min y se valida igual.",
                ex.GetType().Name,
                clsEstadoHttpExcepcion.Estado(ex)?.ToString() ?? "sin estado",
                minutos);
            var respuesta = await chat.GetResponseAsync(Conversacion(mensajes, correccion, conEsquema: false), Opciones(conEsquema: false), cancellationToken);
            return (respuesta.Text, 2);
        }
    }

    private static List<ChatMessage> Conversacion(IReadOnlyList<ChatMessage> mensajes, List<ChatMessage> correccion, bool conEsquema) =>
        [.. mensajes, new ChatMessage(ChatRole.User, conEsquema ? InstruccionConEsquema : InstruccionSinEsquema), .. correccion];

    private ChatOptions Opciones(bool conEsquema)
    {
        var opciones = new ChatOptions { MaxOutputTokens = _opciones.Value.MaxTokens };
        if (!conEsquema)
        {
            return opciones;
        }

        if (_opciones.Value.JsonSchemaEstricto)
        {
            opciones.ResponseFormat = clsReglasRespuestaAgente.FormatoEstricto;
            opciones.AdditionalProperties = new AdditionalPropertiesDictionary { [ClaveEstricto] = true };
        }
        else
        {
            opciones.ResponseFormat = clsReglasRespuestaAgente.Formato;
        }

        return opciones;
    }

    /// <summary>
    /// ¿El error del proveedor es por el formato de respuesta? Solo con estado HTTP 400 o 422 y un mensaje que nombra
    /// <c>response_format</c>, <c>responseFormat</c>, <c>json_schema</c> o "structured output", o una <see cref="NotSupportedException"/>
    /// del adaptador (sin estado HTTP). Un 429, un 5xx o un error de red nunca lo son.
    /// </summary>
    public static bool EsEsquemaNoSoportado(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var estado = clsEstadoHttpExcepcion.Estado(error);
        if (estado is null)
        {
            return Cadena(error).Any(e => e is NotSupportedException);
        }

        return estado is 400 or 422 && Cadena(error).Any(e =>
            e.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("responseformat", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("json_schema", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("structured output", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<Exception> Cadena(Exception error)
    {
        for (var actual = error; actual is not null; actual = actual.InnerException)
        {
            yield return actual;
        }
    }

    private static string Eco(string? crudo)
    {
        if (string.IsNullOrWhiteSpace(crudo))
        {
            return "(respuesta vacía)";
        }

        return crudo.Length > LargoMaximoEco ? crudo[..LargoMaximoEco] + "…" : crudo;
    }

    private static string Correccion(IReadOnlyList<clsFalloValidacion> fallos)
    {
        var texto = new StringBuilder("Tu respuesta anterior no cumple el formato de la respuesta final:").AppendLine();
        for (var i = 0; i < fallos.Count; i++)
        {
            texto.Append(i + 1).Append(". ").AppendLine(fallos[i].Detalle);
        }

        texto.Append("Responde de nuevo SOLO con el objeto JSON corregido (tipo, texto, requiere_confirmacion, acciones_sugeridas), sin texto antes ni después.");
        return texto.ToString();
    }

    private static string CrearInstruccion(bool incluirEsquema)
    {
        var texto = new StringBuilder("Redacta ahora la respuesta final para el comercial. Responde SOLO con un objeto JSON, sin texto antes ni después");
        texto.Append(incluirEsquema ? ", que cumpla este JSON Schema:" + Environment.NewLine + clsReglasRespuestaAgente.EsquemaTexto + Environment.NewLine : ". ");
        texto.Append("Reglas: tipo es ").Append(string.Join(" | ", clsReglasRespuestaAgente.Tipos)).Append("; ")
            .Append($"texto en español, de 1 a {clsRespuestaAgenteEstructurada.LargoMaximoTexto} caracteres; ")
            .Append("requiere_confirmacion es true solo con previsualizacion o confirmacion_requerida; ")
            .Append("previsualizacion solo si calcular_precio ya dio un precio y no hay un origen o destino con municipios por confirmar; ")
            .Append("acciones_sugeridas (").Append(string.Join(", ", clsReglasRespuestaAgente.Acciones))
            .Append(") solo con una previsualización pendiente de confirmar, si no null; ")
            .Append("no escribas montos ni folios que no estén, tal cual, en las salidas de las herramientas o en el estado resuelto.");
        return texto.ToString();
    }
}
