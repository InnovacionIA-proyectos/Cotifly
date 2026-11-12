namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Resumen del estado del costeo (<c>docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md</c> §9.2, ADR-019): la
/// MISMA regla para la ruta (resume a sus camiones) y para la cabecera (resume a sus rutas). Es "el más atrasado":
/// <list type="number">
/// <item>Sin elementos, o todos con estado <c>NULL</c> → <c>NULL</c> (no pasa por Nexo: cotización del chat o todavía sin camiones).</item>
/// <item>Un <c>NULL</c> mezclado con estados cuenta como <see cref="EstadoCosteo.PendienteEnvio"/> si la solicitud todavía no se envió a
/// Nexo, y como <see cref="EstadoCosteo.PendienteCosteo"/> si ya se envió (tiene <c>solicitudNexoId</c>): un camión sin estado dentro de
/// una solicitud enviada no corta el polling ni permite reenviarla (ADR-019).</item>
/// <item>Los <see cref="EstadoCosteo.Descartada"/> y los <see cref="EstadoCosteo.Rechazada"/> se ignoran. Si no queda
/// ninguno: <c>rechazada</c> si hay al menos uno rechazado (una ruta con todos sus camiones rechazados, o rechazados y descartados), si no
/// <c>descartada</c>. La cabecera no usa ese <c>rechazada</c> tal cual: ver <see cref="clsCotizacionSolicitud.EstadoCabecera"/>.</item>
/// <item>Si todos los que quedan tienen el mismo estado, se toma ese.</item>
/// <item>Si no, el primero en este orden (del más atrasado o problemático al más avanzado): <c>error_envio</c> →
/// <c>costeo_vencido</c> → <c>pendiente_envio</c> → <c>pendiente_costeo</c> → <c>entregada</c> → <c>autocosteo</c>.</item>
/// </list>
/// Así, una ruta con un camión autocosteado y otro en la Mesa queda <c>pendiente_costeo</c>; con uno <c>autocosteo</c> y otro
/// <c>entregada</c>, <c>entregada</c>.
/// </summary>
public static class clsResumenEstadoCosteo
{
    /// <summary>Prioridad de cada estado en el resumen: menor = más atrasado (gana).</summary>
    private static readonly EstadoCosteo[] _orden =
    [
        EstadoCosteo.ErrorEnvio,
        EstadoCosteo.CosteoVencido,
        EstadoCosteo.PendienteEnvio,
        EstadoCosteo.PendienteCosteo,
        EstadoCosteo.Entregada,
        EstadoCosteo.Autocosteo,
    ];

    /// <summary>Estado que resume <paramref name="estados"/> con la regla de la clase.</summary>
    public static EstadoCosteo? Resumir(IEnumerable<EstadoCosteo?> estados, bool solicitudEnviada = false)
    {
        ArgumentNullException.ThrowIfNull(estados);
        var lista = estados.ToList();
        if (lista.All(e => e is null))
        {
            return null;
        }

        var sinEstado = solicitudEnviada ? EstadoCosteo.PendienteCosteo : EstadoCosteo.PendienteEnvio;
        var conEstado = lista.Select(e => e ?? sinEstado).ToList();
        var vigentes = conEstado.Where(e => e is not (EstadoCosteo.Descartada or EstadoCosteo.Rechazada)).ToList();
        if (vigentes.Count == 0)
        {
            return conEstado.Contains(EstadoCosteo.Rechazada) ? EstadoCosteo.Rechazada : EstadoCosteo.Descartada;
        }

        if (vigentes.Distinct().Count() == 1)
        {
            return vigentes[0];
        }

        return _orden.First(vigentes.Contains);
    }
}
