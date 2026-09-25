using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Aplicacion.Tarifas.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Tarifas.Services;

/// <summary>
/// Proveedor de tarifas por defecto: el tarifario plano de Google Drive se retiró (decisión del 2026-09-23) y el
/// costeo de Nexo llega con HU-005. Responde siempre <c>TARIFA_FUENTE_NO_DISPONIBLE</c> con un mensaje que le indica
/// al agente pedir el costo al comercial; así <c>buscar_tarifa</c> conserva un contrato estable.
/// </summary>
public sealed class clsTarifaProviderNoDisponible : IClsTarifaProvider
{
    public const string Mensaje =
        "No hay fuente de tarifas disponible: el costeo viene de Nexo (HU-005); mientras tanto el comercial ingresa el costo.";

    public Task<clsResultado<clsSalidaTarifa>> BuscarAsync(clsEntradaBuscarTarifa entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return Task.FromResult<clsResultado<clsSalidaTarifa>>(new clsErrorDominio(clsCodigosError.TarifaFuenteNoDisponible, Mensaje));
    }
}
