using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Punto de extensión de la captura (HU-021.6): qué camiones se crean con cada ruta. Hoy lo implementa
/// <c>clsPlanificadorCamionSinVehiculo</c> (un camión sin vehículo con el peso total de la ruta, que resuelve la Mesa de Costeo de Nexo);
/// la integración con el cubicador (#133, <c>POST /recomendaciones</c>) lo reemplaza registrando otra implementación: un camión por
/// fila de la recomendación. Si no puede planificar (cubicador caído), devuelve el error y la captura no guarda nada.
/// </summary>
public interface IClsPlanificadorCamiones
{
    /// <summary>Los camiones de la ruta (al menos uno), o el error que impide crearla.</summary>
    Task<clsResultado<IReadOnlyList<clsCamionPlaneado>>> PlanificarAsync(
        Guid usuarioId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default);
}
