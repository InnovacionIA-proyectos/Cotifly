using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Clientes.Services;

/// <summary>
/// Búsqueda de clientes cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía, ADR-005 §5): responde siempre
/// <c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c> (503 en <c>GET /web/clientes</c>). El agente sigue sin cliente resuelto: la cotización queda con <c>nitCliente = NULL</c>,
/// el nombre del comercial solo como referencia visible y el margen de la política de la línea. Elegido por
/// <c>clsFabricaBuscadorClientes</c> cuando falta configuración; un adaptador registrado antes (pruebas) se respeta igual.
/// </summary>
public sealed class clsBuscadorClientesNoDisponible : IClsBuscadorClientes
{
    public const string Mensaje =
        "La búsqueda de clientes no está disponible: Nexo no está configurado en este ambiente (falta Nexo:UrlBase). "
        + "Ingresa el NIT manualmente o pide a soporte que configure Nexo.";

    public Task<clsResultado<clsResultadoBusquedaClientes>> BuscarAsync(Guid usuarioId, string texto, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsResultadoBusquedaClientes>>(new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, Mensaje));
}
