namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Opciones de la captura de solicitudes (sección <c>Captura</c>, HU-021.11, #138; ADR-019, decisión 7).
/// </summary>
public sealed class clsOpcionesCaptura
{
    public const string Seccion = "Captura";

    /// <summary>
    /// <c>Captura__AceptarVehiculoCliente</c> (por defecto <c>false</c>): bandera de respaldo del frontend (HU-021-FE.2) mientras el
    /// cubicador no exista. Apagada se aplica la decisión 7: <c>tipo_vehiculo</c> y <c>carroceria</c> de la ruta (formulario y licitación) se
    /// ignoran con un aviso en el log. Encendida se conservan, se validan contra los catálogos de Nexo como cualquier código (G2) y van
    /// SOLO en el camión del respaldo (<see cref="clsPlanificadorCamionSinVehiculo"/>: sin cubicador configurado, ruta sin mercancías
    /// tipadas o falla del cubicador con <c>Cubicador:RespaldoSiFalla</c>). Si el cubicador recomienda, manda el cubicador. La re-solicitud
    /// (<c>datos_carga</c>) los sigue ignorando: los camiones de la versión nueva salen del cubicador o se copian.
    /// </summary>
    public bool AceptarVehiculoCliente { get; set; }
}
