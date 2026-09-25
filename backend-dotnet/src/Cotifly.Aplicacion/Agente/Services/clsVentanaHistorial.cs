namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Turno de texto del historial (rol <c>user</c> o <c>assistant</c>).</summary>
public sealed record clsTurnoTexto(string Rol, string Contenido);

/// <summary>
/// Ventana deslizante del historial por tokens (port de <c>GestorSesiones.historial</c> y <c>estimar_tokens</c> de
/// <c>orquestador/sesiones.py</c>): conserva siempre el primer mensaje (la solicitud original del comercial) y llena con los más
/// recientes que quepan en <c>Sesiones:LimiteTokens</c>.
/// </summary>
/// <remarks>
/// Diferencia con el Python: el último turno (el mensaje actual del comercial) también se conserva siempre, aunque no quepa; el
/// Python podía descartarlo y el modelo respondía sin ver la pregunta.
/// </remarks>
public static class clsVentanaHistorial
{
    /// <summary>Estimación barata de tokens (~4 caracteres por token); 0 si no hay texto, mínimo 1 si hay.</summary>
    public static int EstimarTokens(string? texto) => string.IsNullOrEmpty(texto) ? 0 : Math.Max(1, texto.Length / 4);

    public static IReadOnlyList<clsTurnoTexto> Aplicar(IReadOnlyList<clsTurnoTexto> turnos, int limiteTokens, Func<string?, int>? estimador = null)
    {
        ArgumentNullException.ThrowIfNull(turnos);
        if (turnos.Count == 0)
        {
            return [];
        }

        var estimar = estimador ?? EstimarTokens;
        var primero = turnos[0];
        if (turnos.Count == 1)
        {
            return [primero];
        }

        var ultimo = turnos[^1];
        var presupuesto = limiteTokens - estimar(primero.Contenido) - estimar(ultimo.Contenido);
        var recientes = new List<clsTurnoTexto>();
        for (var i = turnos.Count - 2; i >= 1; i--)
        {
            var costo = estimar(turnos[i].Contenido);
            if (presupuesto - costo < 0)
            {
                break;
            }

            presupuesto -= costo;
            recientes.Add(turnos[i]);
        }

        recientes.Reverse();
        return [primero, .. recientes, ultimo];
    }
}
