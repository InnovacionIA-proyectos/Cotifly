using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Regional de un usuario y miembros de una regional (HU-010.1, ADR-016 §6). Regla: la regional efectiva de un usuario es la
/// de <c>DimEjecutivo</c> (si está configurada y el usuario aparece) o, si no, <c>UsuariosWeb.regional</c> (la fija el DBA).
/// Errores: <c>REGIONAL_NO_ASIGNADA</c> (sin regional efectiva) y <c>REGIONAL_NO_DISPONIBLE</c> (<c>DimEjecutivo</c>
/// configurada pero caída).
/// </summary>
public interface IClsRegionalUsuario
{
    /// <summary>Regional efectiva de <paramref name="usuario"/> y de qué fuente salió.</summary>
    Task<clsResultado<clsRegionalResuelta>> ResolverAsync(clsUsuarioWeb usuario, CancellationToken cancellationToken = default);

    /// <summary>Usuarios de <c>UsuariosWeb</c> cuya regional efectiva es <paramref name="regional"/>.</summary>
    Task<clsResultado<IReadOnlyList<clsUsuarioWeb>>> ListarMiembrosAsync(string regional, CancellationToken cancellationToken = default);
}

/// <summary>Regional resuelta de un usuario.</summary>
/// <param name="Fuente"><see cref="FuenteDimEjecutivo"/> o <see cref="FuenteUsuariosWeb"/> (valores del contrato).</param>
public sealed record clsRegionalResuelta(string Regional, string Fuente)
{
    public const string FuenteDimEjecutivo = "dim_ejecutivo";
    public const string FuenteUsuariosWeb = "usuarios_web";
}
