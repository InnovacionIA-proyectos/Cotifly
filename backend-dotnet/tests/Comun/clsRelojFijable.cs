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
    /// Hora base para fijar <c>createdAt</c> explícitos: las 12:00 de hoy en Bogotá (UTC-5, sin horario de verano). La hora no depende
    /// del reloj real, así que las horas que se fijan desde ella nunca cruzan la medianoche ni caen en el día anterior; del reloj solo
    /// se toma la fecha de negocio, para que el folio y las métricas "de hoy" sigan siendo de hoy.
    /// </summary>
    public static DateTimeOffset HoraBase()
    {
        var bogota = TimeSpan.FromHours(-5);
        var hoy = DateTimeOffset.UtcNow.ToOffset(bogota);
        return new DateTimeOffset(hoy.Year, hoy.Month, hoy.Day, 12, 0, 0, bogota);
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
