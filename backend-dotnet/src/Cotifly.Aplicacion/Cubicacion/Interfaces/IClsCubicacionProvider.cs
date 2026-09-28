using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// Puerto de la cubicación rápida de <c>POST /web/cubicacion</c>: dado un conjunto de bultos, sugiere el mejor
/// vehículo (y la flota, si hace falta más de uno) sin persistir nada. La implementación real
/// (<c>clsCubicacionNexo</c>) reenvía la solicitud a <c>POST /api/cubicacion</c> de Nexo (que a su vez es la única
/// llamada síncrona de Nexo al servicio Cubicador), identificando al comercial ante Nexo con
/// <c>X-Portal-Usuario-Id: {UsuariosWeb.idUsuarioExterno}</c> — de ahí que reciba <paramref name="usuarioId"/>. Sin
/// adaptador real, Infraestructura registra uno que responde <c>CUBICACION_NO_DISPONIBLE</c>.
/// </summary>
public interface IClsCubicacionProvider
{
    /// <summary>
    /// Cubica <paramref name="items"/> contra todo el catálogo estándar de vehículos (Cotifly nunca restringe por
    /// <c>unidadIds</c>: esa es complejidad que no se expone al comercial). <paramref name="claseViaje"/> es opcional.
    /// <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> de Cotifly del comercial autenticado (del token en
    /// <c>/web/*</c>); nunca lo decide el llamante remoto.
    /// </summary>
    Task<clsResultado<clsResultadoCubicacion>> CubicarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default);
}
