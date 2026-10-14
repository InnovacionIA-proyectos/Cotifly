using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Municipios.Interfaces;

/// <summary>
/// Puerto del catálogo de municipios de <c>GET /web/municipios</c>: origen/destino del formulario de captura. La
/// implementación real (<c>clsMunicipiosNexo</c>) consume <c>GET /api/captura-solicitudes/municipios</c> de Nexo,
/// identificando al comercial ante Nexo con <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c> — de ahí que
/// reciba <paramref name="usuarioId"/>. Sin adaptador real, Infraestructura registra uno que responde
/// <c>MUNICIPIOS_NO_DISPONIBLE</c>.
/// </summary>
public interface IClsMunicipiosProvider
{
    /// <summary>
    /// Catálogo completo de municipios (Nexo no pagina esta respuesta). <paramref name="usuarioId"/> es el
    /// <c>UsuariosWeb.id</c> de Cotifly del comercial autenticado (del token en <c>/web/*</c>); nunca lo decide el
    /// llamante remoto.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
