using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Punto de extensión de la captura: qué camiones se crean con cada ruta. Lo implementa
/// <c>clsPlanificadorCamionesCubicador</c>: un camión por fila de la recomendación del cubicador y, si el cubicador no
/// está configurado o falla (con <c>Cubicador:RespaldoSiFalla</c>), el respaldo <c>clsPlanificadorCamionSinVehiculo</c> (un camión sin
/// vehículo con el peso total de la ruta, que resuelve la Mesa de Costeo de Nexo). Si no puede planificar, devuelve el error y la
/// captura no guarda nada.
/// </summary>
public interface IClsPlanificadorCamiones
{
    /// <summary>
    /// Los camiones de la ruta (al menos uno) y, si se llamó al cubicador, su resultado (se guarda en la ruta), o el error que
    /// impide crearla. <paramref name="rutaId"/> es el id que tendrá la ruta (<c>idLineaSolicitud</c> del cubicador).
    /// </summary>
    Task<clsResultado<clsPlanRuta>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="PlanificarAsync"/> para quien procesa en segundo plano (la licitación): un
    /// <c>CUBICADOR_OCUPADO</c> (el cupo global del cubicador siguió tomado, por ejemplo por un "Calcular cubicaje" largo) vuelve
    /// como error, nunca como respaldo ni como <c>CUBICADOR_NO_DISPONIBLE</c>: no es una caída, así que la ruta no debe quedar con el
    /// camión sin vehículo ni gastar un intento; quien llama la devuelve a la cola. Por defecto, <see cref="PlanificarAsync"/>.
    /// </summary>
    Task<clsResultado<clsPlanRuta>> PlanificarEnSegundoPlanoAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default) =>
        PlanificarAsync(usuarioId, rutaId, ruta, cancellationToken);

    /// <summary>
    /// El RESPALDO (ADR-022) de una ruta que iba al cubicador y este no respondió, SIN llamar al cubicador:
    /// lo que aplica <c>POST /web/licitaciones/{folio}/cubicacion/reintentar</c> con <c>usar_respaldo = true</c>, igual que el camino en
    /// segundo plano. Un camión con el vehículo elegido de <paramref name="ruta"/> validado contra Nexo y el aviso
    /// <c>VEHICULO_ELEGIDO_USADO</c> (o, si no es un código de Nexo, sin vehículo con <c>VEHICULO_ELEGIDO_INVALIDO</c>); sin elegido, sin
    /// vehículo. Por defecto, el camión sin vehículo con el peso de la ruta.
    /// </summary>
    Task<clsResultado<clsPlanRuta>> PlanificarRespaldoAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default) =>
        Task.FromResult(clsResultado<clsPlanRuta>.Exito(
            new clsPlanRuta([new clsCamionPlaneado(null, null, Services.clsPlanificadorCamionSinVehiculo.PesoTotal(ruta))])));
}
