using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Regional de un usuario y miembros de una regional (ADR-016 §6). Regla: la regional efectiva de un usuario es la
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

    /// <summary>
    /// <c>true</c> si <paramref name="usuarioId"/> está entre los miembros de la regional efectiva de <paramref name="gerente"/>
    /// (la misma regla que <c>GET /web/cotizaciones/regional</c>: <see cref="ResolverAsync"/> y luego
    /// <see cref="ListarMiembrosAsync"/>). Errores: los mismos de esos dos métodos; quien llama debe negar el acceso ante
    /// cualquiera de ellos (fail-closed).
    /// </summary>
    Task<clsResultado<bool>> EsMiembroDeMiRegionalAsync(clsUsuarioWeb gerente, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// El alcance de regionales de <paramref name="usuario"/> con su <paramref name="rol"/>, la única regla que usan el
    /// tablero y el listado regional (ADR-016 §11):
    /// <list type="bullet">
    /// <item><c>director</c>: sin <paramref name="regional"/>, todos los usuarios de <c>UsuariosWeb</c> (<see cref="clsAlcanceRegional.Todas"/>);
    /// con ella, los miembros de esa regional (<see cref="ListarMiembrosAsync"/>).</item>
    /// <item><c>gerente_comercial</c>: los miembros de su regional efectiva (<see cref="ResolverAsync"/>). Si pide otra,
    /// <c>REGIONAL_NO_PERMITIDA</c>; la suya (sin mayúsculas ni espacios sobrantes) se acepta.</item>
    /// <item>cualquier otro rol: <c>SIN_PERMISO</c>.</item>
    /// </list>
    /// Una <paramref name="regional"/> vacía o con solo espacios equivale a no enviarla. Errores además: <c>REGIONAL_NO_ASIGNADA</c> y
    /// <c>REGIONAL_NO_DISPONIBLE</c>, como <see cref="ResolverAsync"/> y <see cref="ListarMiembrosAsync"/>.
    /// </summary>
    Task<clsResultado<clsAlcanceRegional>> ResolverAlcanceAsync(
        clsUsuarioWeb usuario, RolInterno rol, string? regional = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>true</c> si <paramref name="usuario"/> con <paramref name="rol"/> puede consultar lo de <paramref name="duenoId"/>
    /// (detalle y PDF de una cotización ajena): el <c>director</c> siempre; el <c>gerente_comercial</c> si el dueño es miembro de su
    /// regional (<see cref="EsMiembroDeMiRegionalAsync"/>); cualquier otro rol, nunca. Ante un error, quien llama niega (fail-closed).
    /// </summary>
    Task<clsResultado<bool>> PuedeVerDeAsync(clsUsuarioWeb usuario, RolInterno rol, Guid duenoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Las regionales (<c>GET /web/regionales</c>) que puede pedir <paramref name="usuario"/>. <c>director</c>: las
    /// regionales efectivas de todos los usuarios de <c>UsuariosWeb</c> (la de <c>DimEjecutivo</c> o, si no aparece, la guardada), sin
    /// repetir y ordenadas. <c>gerente_comercial</c>: solo la suya. Otro rol: <c>SIN_PERMISO</c>. Errores además:
    /// <c>REGIONAL_NO_ASIGNADA</c> (gerente) y <c>REGIONAL_NO_DISPONIBLE</c>.
    /// </summary>
    Task<clsResultado<IReadOnlyList<string>>> ListarRegionalesAsync(clsUsuarioWeb usuario, RolInterno rol, CancellationToken cancellationToken = default);
}

/// <summary>
/// Alcance de regionales resuelto por <see cref="IClsRegionalUsuario.ResolverAlcanceAsync"/>.
/// </summary>
/// <param name="Regional">La regional del alcance (la del gerente o la que pidió el director); <c>null</c> = todas (solo el director).</param>
/// <param name="Fuente">De dónde salió la regional del gerente (<see cref="clsRegionalResuelta.FuenteDimEjecutivo"/> o
/// <see cref="clsRegionalResuelta.FuenteUsuariosWeb"/>); <c>null</c> para el director, cuya regional no se resuelve de su usuario.</param>
/// <param name="Miembros">Los usuarios del alcance (sin repetir).</param>
public sealed record clsAlcanceRegional(string? Regional, string? Fuente, IReadOnlyList<clsUsuarioWeb> Miembros)
{
    /// <summary><c>true</c> si el alcance son todas las regionales (director sin regional elegida).</summary>
    public bool Todas => Regional is null;
}

/// <summary>Regional resuelta de un usuario.</summary>
/// <param name="Fuente"><see cref="FuenteDimEjecutivo"/> o <see cref="FuenteUsuariosWeb"/> (valores del contrato).</param>
public sealed record clsRegionalResuelta(string Regional, string Fuente)
{
    public const string FuenteDimEjecutivo = "dim_ejecutivo";
    public const string FuenteUsuariosWeb = "usuarios_web";
}
