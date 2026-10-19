namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Opciones de la captura de solicitudes (sección <c>Captura</c>, HU-021.11, #138; ADR-019, decisión 7).
/// </summary>
public sealed class clsOpcionesCaptura
{
    public const string Seccion = "Captura";

    /// <summary>
    /// <c>Captura__AceptarVehiculoCliente</c>: ¿el vehículo y la carrocería que manda el frontend van en el camión del respaldo? HU-014 (#188,
    /// decisión del usuario del 2026-10-19): por defecto <c>true</c>. El cubicador real ya está conectado, así que la bandera solo decide el
    /// camión del respaldo (<see cref="clsPlanificadorCamionSinVehiculo"/>: sin cubicador configurado, ruta sin mercancías tipadas o falla del
    /// cubicador con <c>Cubicador:RespaldoSiFalla</c>), que con el cubicador lento (#185, <c>CUBICADOR_OCUPADO</c>) es más frecuente.
    /// Encendida, <c>tipo_vehiculo</c> y <c>carroceria</c> de la ruta (formulario y licitación) se conservan, se validan contra los catálogos
    /// de Nexo como cualquier código (G2) y van SOLO en ese camión; la Mesa de Costeo los puede cambiar (D2: lo que trae Nexo manda). Si el
    /// cubicador recomienda, manda el cubicador. Apagada se aplica la decisión 7: se ignoran con un aviso en el log y el camión del respaldo
    /// va sin vehículo. La re-solicitud (<c>datos_carga</c>) los sigue ignorando: los camiones de la versión nueva salen del cubicador o se
    /// copian.
    /// </summary>
    public bool AceptarVehiculoCliente { get; set; } = true;
}
