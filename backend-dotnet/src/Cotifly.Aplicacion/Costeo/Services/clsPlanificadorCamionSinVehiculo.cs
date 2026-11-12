using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Camión de una ruta que no usa los vehículos del cubicador: un solo camión con el VEHÍCULO
/// ELEGIDO de la ruta (<c>tipo_vehiculo</c>/<c>carroceria</c>; sin vehículo si no trae, y entonces lo resuelve la Mesa de Costeo de Nexo) y
/// el peso total de la ruta: el declarado o, si no hay, la suma de sus mercancías (<see cref="clsLineaSolicitudCosteo.SumaPesoPosiciones"/>).
/// Lo usa <see cref="clsPlanificadorCamionesCubicador"/> para las rutas sin mercancías cubicables. Con
/// <see cref="clsOpcionesCaptura.AceptarVehiculoCliente"/> apagada la captura borra el elegido antes y el camión va sin vehículo.
/// </summary>
public sealed class clsPlanificadorCamionSinVehiculo : IClsPlanificadorCamiones
{
    public Task<clsResultado<clsPlanRuta>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return Task.FromResult(clsResultado<clsPlanRuta>.Exito(new clsPlanRuta(Camiones(ruta))));
    }

    /// <summary>
    /// El único camión, con <see cref="PesoTotal"/> y el vehículo elegido de la ruta tal cual (validado en la captura); sin vehículo
    /// ni carrocería si la ruta no los trae.
    /// </summary>
    public static IReadOnlyList<clsCamionPlaneado> Camiones(clsLineaSolicitudCosteo ruta) =>
        [new clsCamionPlaneado(Texto(ruta.TipoVehiculo), Texto(ruta.Carroceria), PesoTotal(ruta))];

    private static string? Texto(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>Peso total de la ruta: el declarado, o la suma de sus mercancías con peso; <c>null</c> si no hay ninguno.</summary>
    public static decimal? PesoTotal(clsLineaSolicitudCosteo ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var suma = ruta.SumaPesoPosiciones;
        return ruta.PesoKg ?? (suma > 0 ? suma : null);
    }
}
