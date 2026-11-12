using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Duración por fase de un turno del asistente: preparación (base de datos y prompt), llamadas al modelo con
/// herramientas, ejecución de las herramientas, respuesta final y guardado; más las llamadas al modelo, los tokens que informa el
/// proveedor y los modelos que respondieron (un modelo distinto del principal es un respaldo de <c>IA:Fallbacks</c>). El tiempo de
/// cada llamada incluye la espera del limitador <c>IA:Rpm</c>, que Infraestructura registra aparte. No guarda ningún texto.
/// </summary>
public sealed class clsMedicionTurno
{
    public const string FasePreparacion = "preparacion";
    public const string FaseModelo = "modelo";
    public const string FaseHerramientas = "herramientas";
    public const string FaseRespuestaFinal = "respuesta_final";
    public const string FaseGuardado = "guardado";

    private readonly long _inicio = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, TimeSpan> _fases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _llamadas = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modelos = new(StringComparer.Ordinal);
    private readonly object _candado = new();

    /// <summary>Tiempo desde que empezó el turno.</summary>
    public TimeSpan Total => Stopwatch.GetElapsedTime(_inicio);

    public long TokensEntrada { get; private set; }

    public long TokensSalida { get; private set; }

    /// <summary>Modelos que respondieron en el turno (el <c>ModelId</c> de cada respuesta), en orden alfabético.</summary>
    public IReadOnlyList<string> Modelos
    {
        get
        {
            lock (_candado)
            {
                return _modelos.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Duración acumulada de una fase (cero si no corrió).</summary>
    public TimeSpan Fase(string fase)
    {
        lock (_candado)
        {
            return _fases.GetValueOrDefault(fase);
        }
    }

    /// <summary>Llamadas al modelo hechas en una fase.</summary>
    public int Llamadas(string fase)
    {
        lock (_candado)
        {
            return _llamadas.GetValueOrDefault(fase);
        }
    }

    /// <summary>Mide un bloque: la duración se suma a <paramref name="fase"/> al desechar el resultado.</summary>
    public IDisposable Medir(string fase) => new clsBloque(this, fase, Stopwatch.GetTimestamp());

    /// <summary>Cliente que suma a <paramref name="fase"/> la duración, los tokens y el modelo de cada llamada.</summary>
    public IChatClient Cliente(IChatClient chat, string fase) => new clsChatClientMedido(chat, this, fase);

    /// <summary>Suma <paramref name="duracion"/> a <paramref name="fase"/>.</summary>
    public void Sumar(string fase, TimeSpan duracion)
    {
        lock (_candado)
        {
            _fases[fase] = _fases.GetValueOrDefault(fase) + duracion;
        }
    }

    private void Registrar(string fase, TimeSpan duracion, ChatResponse? respuesta)
    {
        lock (_candado)
        {
            _fases[fase] = _fases.GetValueOrDefault(fase) + duracion;
            _llamadas[fase] = _llamadas.GetValueOrDefault(fase) + 1;
            TokensEntrada += respuesta?.Usage?.InputTokenCount ?? 0;
            TokensSalida += respuesta?.Usage?.OutputTokenCount ?? 0;
            if (!string.IsNullOrWhiteSpace(respuesta?.ModelId))
            {
                _modelos.Add(respuesta.ModelId);
            }
        }
    }

    private sealed class clsBloque(clsMedicionTurno medicion, string fase, long inicio) : IDisposable
    {
        public void Dispose() => medicion.Sumar(fase, Stopwatch.GetElapsedTime(inicio));
    }

    private sealed class clsChatClientMedido(IChatClient interno, clsMedicionTurno medicion, string fase) : DelegatingChatClient(interno)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inicio = Stopwatch.GetTimestamp();
            ChatResponse? respuesta = null;
            try
            {
                respuesta = await base.GetResponseAsync(messages, options, cancellationToken);
                return respuesta;
            }
            finally
            {
                medicion.Registrar(fase, Stopwatch.GetElapsedTime(inicio), respuesta);
            }
        }

        // El chat medido es de un solo turno: no desecha el cliente compartido (singleton) que envuelve.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
