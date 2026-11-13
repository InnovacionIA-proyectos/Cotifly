using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Dominio.Sesiones;

namespace Cotifly.Aplicacion.Tests.Agente;

// Dobles de prueba del asistente por folio.

/// <summary><see cref="System.Diagnostics.Metrics.IMeterFactory"/> mínimo para pruebas: un medidor nuevo por llamada.</summary>
internal sealed class clsFabricaMedidoresPrueba : System.Diagnostics.Metrics.IMeterFactory
{
    private readonly List<System.Diagnostics.Metrics.Meter> _medidores = [];

    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
    {
        var medidor = new System.Diagnostics.Metrics.Meter(options);
        _medidores.Add(medidor);
        return medidor;
    }

    public void Dispose()
    {
        foreach (var medidor in _medidores)
        {
            medidor.Dispose();
        }
    }
}

/// <summary>Candado por sesión en memoria: registra las tomas y, con <see cref="Ocupado"/>, simula otro turno en curso.</summary>
internal sealed class clsCandadoSesionFalso : IClsCandadoSesion
{
    public bool Ocupado { get; set; }

    public List<string> Tomados { get; } = [];

    public int Liberados { get; private set; }

    public Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default)
    {
        if (Ocupado)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        Tomados.Add($"{spaceId}|{threadId}");
        return Task.FromResult<IAsyncDisposable?>(new clsLiberacion(this));
    }

    private sealed class clsLiberacion(clsCandadoSesionFalso candado) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            candado.Liberados++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary><see cref="IClsSesionRepository"/> en memoria (los eventos quedan al agregarse).</summary>
internal sealed class clsSesionRepositoryMemoria : IClsSesionRepository
{
    private readonly Dictionary<(string, string), clsSesion> _sesiones = [];

    public List<clsEventoConversacion> Eventos { get; } = [];

    public clsSesion? Sesion(string hilo) => _sesiones.GetValueOrDefault((hilo, hilo));

    public void Agregar(clsSesion sesion) => _sesiones[(sesion.SpaceId, sesion.ThreadId)] = sesion;

    public Task<clsSesion?> ObtenerAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sesiones.GetValueOrDefault((spaceId, threadId)));

    public Task<clsSesion> ObtenerOCrearAsync(string spaceId, string threadId, Guid? usuarioId = null, CancellationToken cancellationToken = default)
    {
        if (!_sesiones.TryGetValue((spaceId, threadId), out var sesion))
        {
            sesion = clsSesion.Crear(spaceId, threadId, DateTimeOffset.UtcNow, usuarioId).Valor;
            _sesiones[(spaceId, threadId)] = sesion;
        }

        return Task.FromResult(sesion);
    }

    public Task<clsSesion> TocarAsync(string spaceId, string threadId, DateTimeOffset? cuando = null, CancellationToken cancellationToken = default) =>
        ObtenerOCrearAsync(spaceId, threadId, null, cancellationToken);

    public Task<bool> ArchivarAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<clsSesion>> ListarInactivasAsync(DateTimeOffset umbral, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsSesion>>([]);

    public Task<IReadOnlyList<clsSesion>> ListarPorSpaceAsync(string spaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsSesion>>([]);

    public void AgregarEvento(clsEventoConversacion evento) => Eventos.Add(evento);

    public Task<IReadOnlyList<clsEventoConversacion>> HistorialAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsEventoConversacion>>(Eventos.Where(e => e.SpaceId == spaceId && e.ThreadId == threadId).OrderBy(e => e.CreatedAt).ToList());
}
