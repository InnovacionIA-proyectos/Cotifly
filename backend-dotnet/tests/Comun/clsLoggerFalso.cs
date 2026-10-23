using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cotifly.Pruebas.Comun;

/// <summary>Logger en memoria para verificar avisos (nivel y mensaje ya formateado, con la excepción si la hay).</summary>
public sealed class clsLoggerFalso<TCategoria> : ILogger<TCategoria>
{
    private readonly ConcurrentQueue<clsRegistroLog> _registros = new();

    /// <summary>Registros recibidos, en orden.</summary>
    public IReadOnlyList<clsRegistroLog> Registros => [.. _registros];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _registros.Enqueue(new clsRegistroLog(logLevel, formatter(state, exception), exception, eventId));
    }
}

/// <summary>Un registro de <see cref="clsLoggerFalso{TCategoria}"/> (con su <see cref="EventId"/>, para los avisos con código estable).</summary>
public sealed record clsRegistroLog(LogLevel Nivel, string Mensaje, Exception? Excepcion, EventId Evento = default);

/// <summary>Proveedor de logs en memoria: captura la categoría de cada registro (para verificar, p. ej., que Polly no avisa).</summary>
public sealed class clsProveedorLogsFalso : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Categoria, clsRegistroLog Registro)> _registros = new();

    /// <summary>Registros recibidos con su categoría, en orden.</summary>
    public IReadOnlyList<(string Categoria, clsRegistroLog Registro)> Registros => [.. _registros];

    public ILogger CreateLogger(string categoryName) => new clsLogger(categoryName, _registros);

    public void Dispose()
    {
    }

    private sealed class clsLogger(string categoria, ConcurrentQueue<(string, clsRegistroLog)> registros) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            registros.Enqueue((categoria, new clsRegistroLog(logLevel, formatter(state, exception), exception)));
        }
    }
}
