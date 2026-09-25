using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Usuario autenticado de la petición en curso, leído de los claims del access token (<c>sub</c>, <c>username</c>,
/// <c>rol</c>). Lo implementa la Api; los casos de uso de <c>/web/*</c> lo usan para el aislamiento por dueño.
/// </summary>
public interface IClsContextoUsuario
{
    /// <summary><c>true</c> si la petición trae un access token válido y no revocado.</summary>
    bool EstaAutenticado { get; }

    /// <summary>Id del usuario en <c>UsuariosWeb</c> (claim <c>sub</c>), o <c>null</c>.</summary>
    Guid? UsuarioId { get; }

    /// <summary>Nombre de usuario (claim <c>username</c>), o <c>null</c>.</summary>
    string? NombreUsuario { get; }

    /// <summary>Rol interno (claim <c>rol</c>), o <c>null</c> si falta o es desconocido.</summary>
    RolInterno? Rol { get; }
}
