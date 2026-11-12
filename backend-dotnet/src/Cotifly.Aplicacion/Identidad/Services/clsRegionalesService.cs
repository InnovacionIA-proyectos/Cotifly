using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>Respuesta de <c>GET /web/regionales</c> (esquema <c>Regionales</c>).</summary>
/// <param name="Regionales">Regionales visibles para el usuario, sin repetir y ordenadas (el gerente, solo la suya).</param>
public sealed record clsRegionales(IReadOnlyList<string> Regionales);

/// <summary>
/// <c>GET /web/regionales</c>: las regionales del selector del tablero y del listado regional. La regla (director: las de
/// todos los usuarios; gerente: la suya) es de <see cref="IClsRegionalUsuario.ListarRegionalesAsync"/>.
/// </summary>
public interface IClsRegionalesService
{
    /// <summary>
    /// Regionales del usuario autenticado. Errores: <c>NO_AUTENTICADO</c>, <c>SIN_PERMISO</c> (no es <c>gerente_comercial</c> ni
    /// <c>director</c>), <c>REGIONAL_NO_ASIGNADA</c> (gerente sin regional) y <c>REGIONAL_NO_DISPONIBLE</c>.
    /// </summary>
    Task<clsResultado<clsRegionales>> ListarAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsRegionalesService"/>. Revalida el rol (la política del endpoint ya lo filtra).</summary>
public sealed class clsRegionalesService(
    IClsContextoUsuario usuario,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales) : IClsRegionalesService
{
    public async Task<clsResultado<clsRegionales>> ListarAsync(CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is not { } rol)
        {
            return ErrorNoAutenticado();
        }

        var actual = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (actual is null)
        {
            return ErrorNoAutenticado();
        }

        var lista = await regionales.ListarRegionalesAsync(actual, rol, cancellationToken);
        return lista.EsFallo ? lista.Error : new clsRegionales(lista.Valor);
    }

    private static clsErrorDominio ErrorNoAutenticado() => new(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
}
