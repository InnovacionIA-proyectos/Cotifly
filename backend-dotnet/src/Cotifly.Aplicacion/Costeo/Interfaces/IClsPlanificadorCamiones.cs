using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Punto de extensión de la captura (HU-021.6): qué camiones se crean con cada ruta. Lo implementa
/// <c>clsPlanificadorCamionesCubicador</c> (HU-021.5, #133): un camión por fila de la recomendación del cubicador y, si el cubicador no
/// está configurado o falla (con <c>Cubicador:RespaldoSiFalla</c>), el respaldo <c>clsPlanificadorCamionSinVehiculo</c> (un camión sin
/// vehículo con el peso total de la ruta, que resuelve la Mesa de Costeo de Nexo). Si no puede planificar, devuelve el error y la
/// captura no guarda nada.
/// </summary>
public interface IClsPlanificadorCamiones
{
    /// <summary>
    /// Los camiones de la ruta (al menos uno) y, si se llamó al cubicador, su resultado (HU-023.3: se guarda en la ruta), o el error que
    /// impide crearla. <paramref name="rutaId"/> es el id que tendrá la ruta (<c>idLineaSolicitud</c> del cubicador).
    /// </summary>
    Task<clsResultado<clsPlanRuta>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default);
}
