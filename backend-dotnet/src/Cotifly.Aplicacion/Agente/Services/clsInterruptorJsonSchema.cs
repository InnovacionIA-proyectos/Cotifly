namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Apagado temporal de <c>response_format</c> con JSON Schema, compartido por todas las llamadas al mismo <c>IChatClient</c> (singleton):
/// la respuesta final del agente (<see cref="clsValidadorRespuestaAgente"/>) y el redactor de textos del PDF. Cuando el
/// proveedor rechaza el esquema (400/422), quien lo detecta lo apaga <c>IA:MinutosSinJsonSchema</c> y los demás tampoco lo envían, así no
/// se duplican las llamadas rechazadas. Pasado ese tiempo se vuelve a probar con esquema.
/// </summary>
public sealed class clsInterruptorJsonSchema
{
    private long _apagadoHastaTicks;

    /// <summary>¿Se puede enviar el esquema en <paramref name="ahoraUtc"/>?</summary>
    public bool Habilitado(DateTimeOffset ahoraUtc) => ahoraUtc.UtcTicks >= Interlocked.Read(ref _apagadoHastaTicks);

    /// <summary>Apaga el esquema hasta <paramref name="hastaUtc"/>.</summary>
    public void Apagar(DateTimeOffset hastaUtc) => Interlocked.Exchange(ref _apagadoHastaTicks, hastaUtc.UtcTicks);
}
