using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Planificación de camiones mientras no exista el cubicador (HU-021.6, #133): un solo camión por ruta, SIN vehículo ni carrocería
/// (decisión 7: no los digita el comercial; los resuelve la Mesa de Costeo de Nexo) y con el peso total de la ruta: el declarado o, si
/// no hay, la suma de sus mercancías (<see cref="clsLineaSolicitudCosteo.SumaPesoPosiciones"/>).
/// </summary>
public sealed class clsPlanificadorCamionSinVehiculo : IClsPlanificadorCamiones
{
    public Task<clsResultado<IReadOnlyList<clsCamionPlaneado>>> PlanificarAsync(
        Guid usuarioId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        IReadOnlyList<clsCamionPlaneado> camiones = [new clsCamionPlaneado(null, null, PesoTotal(ruta))];
        return Task.FromResult(clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(camiones));
    }

    /// <summary>Peso total de la ruta: el declarado, o la suma de sus mercancías con peso; <c>null</c> si no hay ninguno.</summary>
    public static decimal? PesoTotal(clsLineaSolicitudCosteo ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var suma = ruta.SumaPesoPosiciones;
        return ruta.PesoKg ?? (suma > 0 ? suma : null);
    }
}
