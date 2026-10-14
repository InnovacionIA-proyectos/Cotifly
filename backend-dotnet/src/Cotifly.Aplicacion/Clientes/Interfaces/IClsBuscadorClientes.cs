using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Clientes.Interfaces;

/// <summary>
/// Puerto de la búsqueda de clientes, compartido por el agente (<c>buscar_cliente</c>, ADR-005) y por
/// <c>GET /web/clientes?q=</c> (HU-005.2). La implementación real (<c>clsBuscadorClientesNexo</c>) consume
/// <c>GET /api/captura-solicitudes/clientes</c> de Nexo, identificando al comercial ante Nexo con
/// <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c> — de ahí que reciba <paramref name="usuarioId"/>
/// además del texto. Sin adaptador real, Infraestructura registra uno que responde
/// <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c>: el agente sigue sin cliente resuelto (<c>nitCliente = NULL</c>, margen de la política).
/// </summary>
public interface IClsBuscadorClientes
{
    /// <summary>
    /// Busca por el texto del comercial (nombre o NIT). El texto es solo un término de búsqueda: el NIT, el origen y el BP salen
    /// únicamente del catálogo (ADR-005 §4). <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> de Cotifly del comercial
    /// autenticado (del token en <c>/web/*</c>, o del estado de la conversación en el agente); nunca lo decide el llamante remoto.
    /// </summary>
    Task<clsResultado<clsResultadoBusquedaClientes>> BuscarAsync(Guid usuarioId, string texto, CancellationToken cancellationToken = default);
}
