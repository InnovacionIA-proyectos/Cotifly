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
}
