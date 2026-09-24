using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Sesiones;

/// <summary>
/// Turno de una conversación (<c>dbo_cotifly.EventosConversacion</c>), FK compuesta a <c>Sesiones</c>.
/// <see cref="Herramientas"/> es JSON (CHECK <c>ISJSON</c>) con los tool-calls del turno.
/// </summary>
public sealed class clsEventoConversacion
{
    public const string CodigoError = "EVENTO_CONVERSACION_INVALIDO";
    public const string RolUsuario = "user";
    public const string RolAsistente = "assistant";

    private clsEventoConversacion()
    {
    }

    public Guid Id { get; private set; }
    public string? SpaceId { get; private set; }
    public string? ThreadId { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public string Rol { get; private set; } = string.Empty;
    public string? Contenido { get; private set; }
    public string? Herramientas { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <param name="spaceId">Space de la sesión.</param>
    /// <param name="threadId">Thread de la sesión.</param>
    /// <param name="rol">Rol del turno (<c>user</c>, <c>assistant</c>...).</param>
    /// <param name="contenido">Texto del turno.</param>
    /// <param name="usuarioId">Usuario que habló, si se conoce.</param>
    /// <param name="herramientasJson">Tool-calls del turno en JSON.</param>
    /// <param name="cuando">Fija <see cref="CreatedAt"/> para un orden cronológico fiable; si es <c>null</c> lo asigna la persistencia.</param>
    public static clsResultado<clsEventoConversacion> Crear(
        string spaceId,
        string threadId,
        string rol,
        string? contenido,
        Guid? usuarioId = null,
        string? herramientasJson = null,
        DateTimeOffset? cuando = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "spaceId", spaceId, clsSesion.LargoMaximoId),
            clsValidacion.Requerido(CodigoError, "threadId", threadId, clsSesion.LargoMaximoId),
            clsValidacion.Requerido(CodigoError, "rol", rol));

        if (error is not null)
        {
            return error;
        }

        return new clsEventoConversacion
        {
            Id = Guid.NewGuid(),
            SpaceId = spaceId.Trim(),
            ThreadId = threadId.Trim(),
            Rol = rol.Trim(),
            Contenido = contenido,
            UsuarioId = usuarioId,
            Herramientas = clsValidacion.Normalizar(herramientasJson),
            CreatedAt = cuando ?? default,
        };
    }
}
