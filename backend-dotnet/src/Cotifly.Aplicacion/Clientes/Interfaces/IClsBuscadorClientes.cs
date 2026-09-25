using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Clientes.Interfaces;

/// <summary>
/// Puerto de la búsqueda de clientes del agente (<c>buscar_cliente</c>, ADR-005). La implementación real consume el mismo endpoint
/// de Nexo que el buscador del formulario (HU-005.2). Hasta entonces Infraestructura registra un adaptador que responde
/// <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c>: el agente sigue sin cliente resuelto (<c>nitCliente = NULL</c>, margen de la política).
/// </summary>
public interface IClsBuscadorClientes
{
    /// <summary>
    /// Busca por el texto del comercial (nombre o NIT). El texto es solo un término de búsqueda: el NIT, el origen y el BP salen
    /// únicamente del catálogo (ADR-005 §4).
    /// </summary>
    Task<clsResultado<clsResultadoBusquedaClientes>> BuscarAsync(string texto, CancellationToken cancellationToken = default);
}
