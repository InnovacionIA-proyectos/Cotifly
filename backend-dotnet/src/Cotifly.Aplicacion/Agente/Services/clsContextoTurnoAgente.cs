using Cotifly.Dominio.Agente;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Contexto del turno en curso (uno por ámbito de DI: la petición de <c>/web/chat</c> o el evento de Google Chat). Lo llena el
/// runner al empezar el turno y lo leen las herramientas y los filtros: el comercial, el hilo y el estado resuelto. El estado solo
/// lo cambia el runner con <c>clsEstadoConversacionService</c> (ADR-004 §3).
/// </summary>
public sealed class clsContextoTurnoAgente
{
    public Guid UsuarioId { get; private set; }

    public string SpaceId { get; private set; } = string.Empty;

    public string ThreadId { get; private set; } = string.Empty;

    /// <summary>Mensaje del comercial en este turno (para exigir que los destinatarios de un correo salgan de él).</summary>
    public string MensajeUsuario { get; private set; } = string.Empty;

    public clsEstadoConversacion Estado { get; private set; } = clsEstadoConversacion.Vacio;

    /// <summary>
    /// True si el comercial confirmó de forma explícita en este turno la previsualización mostrada (ADR-003 §5): habilita
    /// <c>generar_pdf</c> y <c>enviar_correo</c>. No se persiste: una confirmación vale para su turno.
    /// </summary>
    public bool ConfirmadoEnEsteTurno { get; private set; }

    /// <summary>
    /// Mensajes del comercial en la conversación (historial + el actual): con una confirmación, los destinatarios de un correo deben
    /// salir de aquí, no de datos externos (auditoría de #12, riesgo 5).
    /// </summary>
    public IReadOnlyList<string> MensajesDelComercial { get; private set; } = [];

    public bool Iniciado => UsuarioId != Guid.Empty;

    /// <summary>Estado después de aplicar la salida de una herramienta (lo llama el runner).</summary>
    public void ActualizarEstado(clsEstadoConversacion estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        Estado = estado;
    }

    public void Iniciar(
        Guid usuarioId, string spaceId, string threadId, string mensaje, clsEstadoConversacion estado, bool confirmado, IReadOnlyList<string>? mensajesDelComercial = null)
    {
        MensajesDelComercial = mensajesDelComercial ?? [mensaje ?? string.Empty];
        ArgumentNullException.ThrowIfNull(estado);
        UsuarioId = usuarioId;
        SpaceId = spaceId;
        ThreadId = threadId;
        MensajeUsuario = mensaje ?? string.Empty;
        Estado = estado;
        ConfirmadoEnEsteTurno = confirmado;
    }
}
