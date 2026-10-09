using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Respaldo de la planificación de camiones (HU-021.6; HU-021.5, #133: lo usa <see cref="clsPlanificadorCamionesCubicador"/> cuando el
/// cubicador no está configurado o falla): un solo camión por ruta, SIN vehículo ni carrocería
/// (decisión 7: no los digita el comercial; los resuelve la Mesa de Costeo de Nexo) y con el peso total de la ruta: el declarado o, si
/// no hay, la suma de sus mercancías (<see cref="clsLineaSolicitudCosteo.SumaPesoPosiciones"/>).
/// </summary>
public sealed class clsPlanificadorCamionSinVehiculo : IClsPlanificadorCamiones
{
    public Task<clsResultado<IReadOnlyList<clsCamionPlaneado>>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return Task.FromResult(clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(Camiones(ruta)));
    }

    /// <summary>El único camión del respaldo: sin vehículo ni carrocería y con <see cref="PesoTotal"/>.</summary>
    public static IReadOnlyList<clsCamionPlaneado> Camiones(clsLineaSolicitudCosteo ruta) => [new clsCamionPlaneado(null, null, PesoTotal(ruta))];

    /// <summary>Peso total de la ruta: el declarado, o la suma de sus mercancías con peso; <c>null</c> si no hay ninguno.</summary>
    public static decimal? PesoTotal(clsLineaSolicitudCosteo ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var suma = ruta.SumaPesoPosiciones;
        return ruta.PesoKg ?? (suma > 0 ? suma : null);
    }
}
