using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Sesiones;

/// <summary>
/// Sesión de conversación (<c>dbo_cotifly.Sesiones</c>), PK compuesta (<see cref="SpaceId"/>, <see cref="ThreadId"/>).
/// En la consola web se usa <c>spaceId = threadId = "web/{sesionId}"</c> (ADR-004).
/// </summary>
public sealed class clsSesion
{
    public const string CodigoError = "SESION_INVALIDA";
    public const int LargoMaximoId = 128;
    public const string PrefijoWeb = "web/";

    private clsSesion()
    {
    }

    public string SpaceId { get; private set; } = string.Empty;
    public string ThreadId { get; private set; } = string.Empty;
    public EstadoSesion? Estado { get; private set; }
    public DateTimeOffset UltimaActividad { get; private set; }

    /// <summary>Estado estructurado del agente (script 0002, ADR-004). <c>null</c> hasta el primer turno.</summary>
    public clsEstadoConversacion? EstadoAgente { get; private set; }

    /// <summary>Dueño de la sesión (script 0002). Otro usuario recibe 404.</summary>
    public Guid? UsuarioId { get; private set; }

    public static clsResultado<clsSesion> Crear(string spaceId, string threadId, DateTimeOffset ahora, Guid? usuarioId = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "spaceId", spaceId, LargoMaximoId),
            clsValidacion.Requerido(CodigoError, "threadId", threadId, LargoMaximoId));

        if (error is not null)
        {
            return error;
        }

        return new clsSesion
        {
            SpaceId = spaceId.Trim(),
            ThreadId = threadId.Trim(),
            Estado = EstadoSesion.Activa,
            UltimaActividad = ahora,
            UsuarioId = usuarioId,
        };
    }

    /// <summary>Identificador <c>spaceId</c>/<c>threadId</c> de una sesión de la consola web.</summary>
    public static string IdWeb(Guid sesionId) => $"{PrefijoWeb}{sesionId:D}";

    /// <summary>
    /// Identificador de una sesión de la consola web a partir del <c>sesion_id</c> que genera el cliente
    /// (<c>^[A-Za-z0-9_-]{8,64}$</c>, contrato <c>MensajeWeb</c>; lo valida la Api): <c>web/{sesion_id}</c>.
    /// </summary>
    public static string IdWeb(string sesionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sesionId);
        return PrefijoWeb + sesionId.Trim();
    }

    /// <summary>
    /// Hilo del asistente por folio: <c>web/{sesion_id}/{folio}</c>. La sesión queda ligada al comercial y a ESA
    /// cotización: el mismo <c>sesion_id</c> con otro folio es otro hilo, con su propio historial. Cabe en los 128 caracteres de
    /// <c>Sesiones.threadId</c> (64 del <c>sesion_id</c> + 50 del folio).
    /// </summary>
    public static string IdWebFolio(string sesionId, string folio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        return $"{IdWeb(sesionId)}/{folio.Trim().ToUpperInvariant()}";
    }

    public bool EsDe(Guid usuarioId) => UsuarioId == usuarioId;

    /// <summary>Registra actividad; una sesión archivada que vuelve a usarse queda activa.</summary>
    public void Tocar(DateTimeOffset cuando)
    {
        UltimaActividad = cuando;
        Estado = EstadoSesion.Activa;
    }

    public void Archivar() => Estado = EstadoSesion.Archivada;

    public void ActualizarEstadoAgente(clsEstadoConversacion estado)
    {
        ArgumentNullException.ThrowIfNull(estado);
        EstadoAgente = estado;
    }

    /// <summary>Liga la sesión a su usuario si aún no tiene dueño. Devuelve <c>false</c> si ya es de otro.</summary>
    public bool AsignarUsuario(Guid usuarioId)
    {
        if (UsuarioId is { } actual && actual != usuarioId)
        {
            return false;
        }

        UsuarioId = usuarioId;
        return true;
    }
}
