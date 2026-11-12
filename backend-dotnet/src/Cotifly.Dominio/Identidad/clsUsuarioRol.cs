using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Rol interno asignado a un usuario (<c>dbo_cotifly.UsuariosRoles</c>, script 0002, ADR-009). Se crea en el primer
/// login (JIT) con el rol que resulta de su rol externo; en los siguientes el rol externo actual debe coincidir con la
/// asignación activa, y si no, el acceso se rechaza hasta que un gerente o administrador la revise
/// (<see cref="RevisadoPor"/>, <see cref="RevisadoAt"/>). La asignación nunca se re-sincroniza sola.
/// HU-040.1 (#229, script 16): un usuario puede tener varias asignaciones activas, una por rol (multi-rol); el mismo rol no se
/// asigna dos veces (<c>uq_usuariosRoles_usuarioId_rolId</c>).
/// </summary>
public sealed class clsUsuarioRol
{
    public const string CodigoError = "USUARIO_ROL_INVALIDO";

    private clsUsuarioRol()
    {
    }

    public int Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public int RolId { get; private set; }
    public DateTimeOffset AsignadoAt { get; private set; }
    public bool Activo { get; private set; }
    public Guid? RevisadoPor { get; private set; }
    public DateTimeOffset? RevisadoAt { get; private set; }

    /// <summary>Asignación activa del rol <paramref name="rolId"/> al usuario.</summary>
    public static clsResultado<clsUsuarioRol> Asignar(Guid usuarioId, int rolId, DateTimeOffset ahora)
    {
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(CodigoError, "La asignación debe pertenecer a un usuario.");
        }

        if (rolId <= 0)
        {
            return new clsErrorDominio(CodigoError, "La asignación debe referirse a un rol existente.");
        }

        return new clsUsuarioRol
        {
            UsuarioId = usuarioId,
            RolId = rolId,
            AsignadoAt = ahora,
            Activo = true,
        };
    }

    /// <summary>Retira la asignación tras la revisión de <paramref name="revisor"/> (conserva el historial).</summary>
    public void Retirar(Guid revisor, DateTimeOffset ahora)
    {
        Activo = false;
        RegistrarRevision(revisor, ahora);
    }

    /// <summary>
    /// HU-040.3 (#258): la reconciliación del login DESACTIVA la asignación cuyo rol externo ya no la respalda (auto-revocar, fail-safe,
    /// aplica a todos los roles). No hay revisor humano: <see cref="RevisadoPor"/> queda NULL y <see cref="RevisadoAt"/> marca el instante,
    /// así se distingue de una revisión manual (<see cref="Retirar"/>, con revisor). Idempotente: sobre una fila ya inactiva no hace nada.
    /// </summary>
    public void Desactivar(DateTimeOffset ahora)
    {
        if (!Activo)
        {
            return;
        }

        Activo = false;
        RevisadoPor = null;
        RevisadoAt = ahora;
    }

    /// <summary>
    /// HU-040.3 (#258): la reconciliación del login REACTIVA una fila existente (la unicidad <c>uq_usuariosRoles_usuarioId_rolId</c> impide
    /// insertar el mismo rol dos veces) para auto-otorgar un rol ORDINARIO que volvió a tener respaldo externo. Sin revisor humano
    /// (<see cref="RevisadoPor"/> NULL, <see cref="RevisadoAt"/> con el instante). Idempotente: sobre una fila ya activa no hace nada.
    /// </summary>
    public void Reactivar(DateTimeOffset ahora)
    {
        if (Activo)
        {
            return;
        }

        Activo = true;
        AsignadoAt = ahora;
        RevisadoPor = null;
        RevisadoAt = ahora;
    }

    /// <summary>Deja constancia de quién revisó la asignación y cuándo.</summary>
    public void RegistrarRevision(Guid revisor, DateTimeOffset ahora)
    {
        if (revisor == Guid.Empty)
        {
            throw new ArgumentException("El revisor es obligatorio.", nameof(revisor));
        }

        RevisadoPor = revisor;
        RevisadoAt = ahora;
    }
}
