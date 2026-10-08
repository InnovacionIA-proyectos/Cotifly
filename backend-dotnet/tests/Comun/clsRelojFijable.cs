using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// <see cref="IClsReloj"/> de las APIs de prueba en memoria (#117): da la hora del <see cref="TimeProvider"/> salvo dentro de
/// <see cref="Fijar"/>, que fija una hora explícita solo para el flujo asíncrono que lo llama (<see cref="AsyncLocal{T}"/>): las
/// pruebas que corren en paralelo sobre la misma API no la ven. Así el orden por <c>createdAt</c> no depende del reloj real,
/// que en el contenedor de pruebas llega a saltar hacia atrás.
/// </summary>
public sealed class clsRelojFijable(TimeProvider tiempo) : IClsReloj
{
    private static readonly AsyncLocal<DateTimeOffset?> _fijada = new();

    public DateTimeOffset AhoraUtc => _fijada.Value ?? tiempo.GetUtcNow();

    /// <summary>
    /// Hora base para fijar <c>createdAt</c> explícitos: el minuto actual menos 10 minutos (mismo día de negocio salvo en los primeros
    /// minutos tras la medianoche; las horas que se fijan a partir de ella quedan en el pasado y nunca dependen del reloj después).
    /// </summary>
    public static DateTimeOffset HoraBase()
    {
        var ahora = DateTimeOffset.UtcNow;
        return new DateTimeOffset(ahora.Year, ahora.Month, ahora.Day, ahora.Hour, ahora.Minute, 0, TimeSpan.Zero).AddMinutes(-10);
    }

    /// <summary>Fija <paramref name="hora"/> en este flujo hasta disponer el resultado (se restaura la anterior).</summary>
    public static IDisposable Fijar(DateTimeOffset hora)
    {
        var anterior = _fijada.Value;
        _fijada.Value = hora.ToUniversalTime();
        return new clsRestaurar(anterior);
    }

    private sealed class clsRestaurar(DateTimeOffset? anterior) : IDisposable
    {
        public void Dispose() => _fijada.Value = anterior;
    }
}
