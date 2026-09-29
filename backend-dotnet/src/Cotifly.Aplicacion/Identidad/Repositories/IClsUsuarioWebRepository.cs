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
    /// Usuario por <c>email</c> (sin distinguir mayúsculas): identidad de Google Chat (ADR-011 §2). <c>null</c> si no existe o si
    /// el correo está repetido (no se adivina a cuál comercial pertenece).
    /// </summary>
    Task<clsUsuarioWeb?> ObtenerPorEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usuarios con <c>regional</c> igual a <paramref name="regional"/> (recortada, sin distinguir mayúsculas) o cuyo
    /// <c>nombreUsuario</c> está en <paramref name="nombresUsuario"/> (candidatos a miembros de una regional, HU-010.1).
    /// </summary>
    Task<IReadOnlyList<clsUsuarioWeb>> ListarPorRegionalONombresAsync(
        string regional, IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default);
}
