namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Opciones de la captura de solicitudes (sección <c>Captura</c>, HU-021.11, #138; ADR-019, decisión 7).
/// </summary>
public sealed class clsOpcionesCaptura
{
    public const string Seccion = "Captura";

    /// <summary>
    /// HU-032 (#197, ADR-022): <c>Captura__AceptarVehiculoCliente</c> es el INTERRUPTOR DE REVERSA del vehículo elegido. Encendida (por
    /// defecto) rige HU-032: el vehículo elegido de cada ruta se usa cuando la ruta no se puede cubicar (y entonces es obligatorio salvo en
    /// Contenedor: <c>VEHICULO_REQUERIDO</c>) y como respaldo del cubicador (sin selección, caída u ocupado), con sus avisos; la edición y
    /// la re-solicitud lo conservan. Apagada vuelve el comportamiento anterior a HU-032: el elegido se ignora con un aviso en el log, no se
    /// exige y el respaldo va sin vehículo. Se mantiene (y no se retira) para poder volver atrás en un ambiente sin desplegar si Nexo o la
    /// Mesa de Costeo reportan un problema con los vehículos elegidos.
    /// <para>Historia: <c>Captura__AceptarVehiculoCliente</c>: ¿el vehículo y la carrocería que manda el frontend van en el camión del respaldo? HU-014 (#188,
    /// decisión del usuario del 2026-10-19): por defecto <c>true</c>. El cubicador real ya está conectado, así que la bandera solo decide el
    /// camión del respaldo (<see cref="clsPlanificadorCamionSinVehiculo"/>: sin cubicador configurado, ruta sin mercancías tipadas o falla del
    /// cubicador con <c>Cubicador:RespaldoSiFalla</c>), que con el cubicador lento (#185, <c>CUBICADOR_OCUPADO</c>) es más frecuente.
    /// Encendida, <c>tipo_vehiculo</c> y <c>carroceria</c> de la ruta (formulario y licitación) se conservan, se validan contra los catálogos
    /// de Nexo como cualquier código (G2; HU-014 #188, M1: deben ser códigos que Nexo conozca, ya no basta el catálogo del cubicador) y van SOLO en ese camión; la Mesa de Costeo los puede cambiar (D2: lo que trae Nexo manda). Si el
    /// cubicador recomienda, manda el cubicador. Apagada se aplica la decisión 7: se ignoran con un aviso en el log y el camión del respaldo
    /// va sin vehículo. La re-solicitud (<c>datos_carga</c>) los sigue ignorando: los camiones de la versión nueva salen del cubicador o se
    /// copian.</para>
    /// </summary>
    public bool AceptarVehiculoCliente { get; set; } = true;
}
