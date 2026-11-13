namespace Cotifly.Pruebas.Comun;

/// <summary>
/// <see cref="TimeProvider"/> manual para las pruebas: el tiempo solo avanza con <see cref="Avanzar"/>, que dispara los temporizadores
/// vencidos (los de <c>CancellationTokenSource(TimeSpan, TimeProvider)</c> y <c>Task.WaitAsync(TimeSpan, TimeProvider)</c>). Así una
/// prueba del tiempo máximo del redactor no espera los 15 s reales.
/// </summary>
public sealed class clsTiempoManual : TimeProvider
{
    private readonly object _candado = new();
    private readonly List<clsTemporizador> _temporizadores = [];
    private DateTimeOffset _ahora = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_candado)
        {
            return _ahora;
        }
    }

    /// <summary>Temporizadores pendientes (no disparados ni descartados).</summary>
    public int Pendientes
    {
        get
        {
            lock (_candado)
            {
                return _temporizadores.Count;
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var temporizador = new clsTemporizador(this, callback, state);
        temporizador.Change(dueTime, period);
        return temporizador;
    }

    /// <summary>Adelanta el reloj y dispara (fuera del candado) los temporizadores que vencieron.</summary>
    public void Avanzar(TimeSpan lapso)
    {
        List<clsTemporizador> vencidos;
        lock (_candado)
        {
            _ahora += lapso;
            vencidos = [.. _temporizadores.Where(t => t.Vence <= _ahora)];
            _temporizadores.RemoveAll(vencidos.Contains);
        }

        foreach (var temporizador in vencidos)
        {
            temporizador.Disparar();
        }
    }

    private void Programar(clsTemporizador temporizador, TimeSpan dueTime)
    {
        lock (_candado)
        {
            _temporizadores.Remove(temporizador);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                temporizador.Vence = _ahora + dueTime;
                _temporizadores.Add(temporizador);
            }
        }
    }

    private void Quitar(clsTemporizador temporizador)
    {
        lock (_candado)
        {
            _temporizadores.Remove(temporizador);
        }
    }

    private sealed class clsTemporizador(clsTiempoManual tiempo, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Vence { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            tiempo.Programar(this, dueTime);
            return true;
        }

        public void Disparar() => callback(state);

        public void Dispose() => tiempo.Quitar(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
