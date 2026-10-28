using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Repositories;

/// <summary>Puerto de los comerciales de Cotifly (<c>UsuariosWeb</c>).</summary>
public interface IClsUsuarioWebRepository
{
    void Agregar(clsUsuarioWeb usuario);

    Task<clsUsuarioWeb?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usuario por <c>nombreUsuario</c>, la clave real del aprovisionamiento JIT (índice único filtrado;
    /// la collation no distingue mayúsculas).
    /// </summary>
    Task<clsUsuarioWeb?> ObtenerPorNombreUsuarioAsync(string nombreUsuario, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usuario vinculado a la fila <paramref name="idUsuarioExterno"/> del almacén corporativo (índice único filtrado).
    /// No se usa para identificar en el login: solo para detectar un <c>NOMBREUSUARIO</c> cambiado (ADR-009).
    /// </summary>
    Task<clsUsuarioWeb?> ObtenerPorIdUsuarioExternoAsync(long idUsuarioExterno, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usuarios con <c>regional</c> igual a <paramref name="regional"/> (recortada, sin distinguir mayúsculas) o cuyo
    /// <c>nombreUsuario</c> está en <paramref name="nombresUsuario"/> (candidatos a miembros de una regional, HU-010.1).
    /// </summary>
    Task<IReadOnlyList<clsUsuarioWeb>> ListarPorRegionalONombresAsync(
        string regional, IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-037.3 (#211): todos los usuarios de <c>UsuariosWeb</c> (solo lectura), el alcance del director sin regional elegida. La tabla
    /// tiene a las personas que entraron a Cotifly (decenas o cientos), así que no se pagina.
    /// </summary>
    Task<IReadOnlyList<clsUsuarioWeb>> ListarTodosAsync(CancellationToken cancellationToken = default);
}
