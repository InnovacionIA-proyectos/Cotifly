namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Espaciado del polling (HU-005.6, ADR-015 §4). Determinista y sin contador de consultas: la espera es una fracción de la edad de
/// la solicitud (<c>clamp(edad × FactorEdad, IntervaloInicial, IntervaloMaximo)</c>), así se pregunta cada pocos segundos al
/// principio (como el frontend de Nexo) y cada vez menos cuando una licitación tarda horas. Tras fallos seguidos se multiplica por
/// <c>2^min(fallos, 5)</c>, sin pasar del máximo.
/// </summary>
public static class clsAgendaPollingCosteo
{
    public const int ExponenteMaximoFallos = 5;

    /// <summary>¿La solicitud ya superó la edad máxima del polling?</summary>
    public static bool Vencida(DateTimeOffset ahora, DateTimeOffset enviadaAt, clsOpcionesCosteoNexo opciones)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        return ahora - enviadaAt >= opciones.EdadMaxima;
    }

    /// <summary>Espera hasta la siguiente consulta.</summary>
    public static TimeSpan Espera(DateTimeOffset ahora, DateTimeOffset enviadaAt, int fallosSeguidos, clsOpcionesCosteoNexo opciones)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        var edad = ahora - enviadaAt;
        if (edad < TimeSpan.Zero)
        {
            edad = TimeSpan.Zero;
        }

        var espera = TimeSpan.FromTicks((long)(edad.Ticks * opciones.FactorEdad));
        if (fallosSeguidos > 0)
        {
            espera = TimeSpan.FromTicks(Math.Max(espera.Ticks, opciones.IntervaloInicial.Ticks) << Math.Min(fallosSeguidos, ExponenteMaximoFallos));
        }

        return espera < opciones.IntervaloInicial ? opciones.IntervaloInicial
            : espera > opciones.IntervaloMaximo ? opciones.IntervaloMaximo
            : espera;
    }

    /// <summary>Siguiente consulta, o <c>null</c> si la solicitud ya superó la edad máxima.</summary>
    public static DateTimeOffset? Siguiente(DateTimeOffset ahora, DateTimeOffset enviadaAt, int fallosSeguidos, clsOpcionesCosteoNexo opciones) =>
        Vencida(ahora, enviadaAt, opciones) ? null : ahora + Espera(ahora, enviadaAt, fallosSeguidos, opciones);
}
