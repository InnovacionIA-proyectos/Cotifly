using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Metricas.Services;

/// <summary>
/// Métricas del tablero (<c>GET /web/metricas/actividad</c> y <c>/ajustes-margen</c>, <b>borrador</b> hasta HU-010.1).
/// <list type="bullet">
/// <item><c>comercial</c>: solo su propia fila.</item>
/// <item><c>gerente_comercial</c>: las filas de los comerciales de su regional (<c>UsuariosWeb.regional</c>). Si su regional es
/// nula (aún no se cruza con DimEjecutivo) solo ve su propia fila: nunca se muestra información de otra regional por no poder
/// comprobarla.</item>
/// </list>
/// El alcance se decide con el rol del token, revalidado aquí.
/// </summary>
public interface IClsMetricasService
{
    Task<clsResultado<IReadOnlyList<clsActividadComercial>>> ObtenerActividadAsync(CancellationToken cancellationToken = default);

    Task<clsResultado<IReadOnlyList<clsAjustesMargenComercial>>> ObtenerAjustesMargenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsMetricasService"/>.</summary>
public sealed class clsMetricasService(
    IClsContextoUsuario usuario,
    IClsUsuarioWebRepository usuarios,
    IClsMetricasRepository metricas) : IClsMetricasService
{
    public async Task<clsResultado<IReadOnlyList<clsActividadComercial>>> ObtenerActividadAsync(CancellationToken cancellationToken = default)
    {
        var alcance = await ResolverAlcanceAsync(cancellationToken);
        return alcance.EsFallo ? alcance.Error : clsResultado<IReadOnlyList<clsActividadComercial>>.Exito(await metricas.ListarActividadAsync(alcance.Valor, cancellationToken));
    }

    public async Task<clsResultado<IReadOnlyList<clsAjustesMargenComercial>>> ObtenerAjustesMargenAsync(CancellationToken cancellationToken = default)
    {
        var alcance = await ResolverAlcanceAsync(cancellationToken);
        return alcance.EsFallo ? alcance.Error : clsResultado<IReadOnlyList<clsAjustesMargenComercial>>.Exito(await metricas.ListarAjustesMargenAsync(alcance.Valor, cancellationToken));
    }

    /// <summary>Alcance según el rol: el propio usuario, o la regional del gerente si la tiene.</summary>
    public async Task<clsResultado<clsAlcanceMetricas>> ResolverAlcanceAsync(CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is not { } rol)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        if (rol != RolInterno.GerenteComercial)
        {
            return clsAlcanceMetricas.DelUsuario(usuarioId);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        return string.IsNullOrWhiteSpace(gerente?.Regional)
            ? clsAlcanceMetricas.DelUsuario(usuarioId)
            : clsAlcanceMetricas.DeLaRegional(gerente.Regional);
    }
}
