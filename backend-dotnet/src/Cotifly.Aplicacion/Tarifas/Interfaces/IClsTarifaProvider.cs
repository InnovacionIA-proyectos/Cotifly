using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Tarifas.Interfaces;

/// <summary>
/// Puerto del costeo del flete (herramienta <c>buscar_tarifa</c>). El tarifario plano de Google Drive
/// (<c>DriveFlatFileProvider</c>) se retiró el 2026-09-23 y no se porta: el costeo viene de Nexo (HU-005).
/// Hasta entonces Infraestructura registra un proveedor que responde <c>TARIFA_FUENTE_NO_DISPONIBLE</c>,
/// para que el agente tenga un contrato estable y pida el costo al comercial.
/// </summary>
public interface IClsTarifaProvider
{
    /// <summary>Costo de la ruta o un error de negocio (<c>TARIFA_FUENTE_NO_DISPONIBLE</c>, <c>TARIFA_CONFIG_INVALIDA</c>).</summary>
    Task<clsResultado<clsSalidaTarifa>> BuscarAsync(clsEntradaBuscarTarifa entrada, CancellationToken cancellationToken = default);
}
