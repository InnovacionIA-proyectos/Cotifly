namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Cómo se aplica la edición de una solicitud según su estado (<see cref="clsCotizacionSolicitud.ModoEdicion"/>).
/// Códigos (ADR-021): <c>en_sitio</c> | <c>reemplazo</c> | <c>nueva_version</c>.
/// <list type="bullet">
/// <item><see cref="EnSitio"/>: nunca llegó a Nexo (<c>error_envio</c> o <c>pendiente_envio</c> abandonado). Se edita la misma
/// cotización y se envía.</item>
/// <item><see cref="Reemplazo"/>: ya está en Nexo, sin entregar al cliente y sin resultado. Se rechaza la solicitud anterior en Nexo y se
/// captura de nuevo en el mismo folio; el PDF queda invalidado.</item>
/// <item><see cref="NuevaVersion"/>: ya entregada al cliente, con resultado o finalizada. Se crea la versión <c>-vN</c> y la
/// original no cambia.</item>
/// </list>
/// </summary>
public enum ModoEdicionSolicitud
{
    EnSitio,
    Reemplazo,
    NuevaVersion,
}

/// <summary>
/// Una ruta de la edición: <paramref name="RutaExistenteId"/> es la ruta guardada que se edita (conserva su id, su línea de
/// negocio y su margen negociado) o <c>null</c> si es una ruta nueva. <paramref name="Ruta"/> trae la línea validada, la línea de negocio,
/// los camiones (vueltos a planificar o los vigentes) y los conexos con el costo vigente del catálogo. <paramref name="Replanificada"/>
/// dice si sus camiones salieron del planificador (cambió la carga o la clase de viaje, o es nueva) o son los vigentes.
/// <paramref name="ReincluirRechazados"/> (<c>reincluir_rechazados</c>): en una ruta guardada que conserva sus camiones, los
/// que rechazó la Mesa de Costeo vuelven a viajar sin el rechazo; sin él (por defecto) siguen rechazados y fuera del envío.
/// </summary>
public sealed record clsRutaEditada(Guid? RutaExistenteId, clsRutaCapturada Ruta, bool Replanificada, bool ReincluirRechazados = false);

/// <summary>
/// El contenido editado de una solicitud: el encabezado (el <c>tipo</c> no cambia), las rutas en su orden nuevo y, si cambia la
/// moneda, la moneda nueva con la TRM del día (<paramref name="MonedaNueva"/> <c>null</c> = la misma moneda y la TRM fijada).
/// </summary>
public sealed record clsEdicionSolicitud(
    clsEncabezadoSolicitudCosteo Encabezado,
    IReadOnlyList<clsRutaEditada> Rutas,
    string? MonedaNueva = null,
    decimal TrmNueva = 0m)
{
    /// <summary>
    /// La tasa vigente del seguro de la mercancía (<c>TarifasSeguro</c>), con la que se recalcula el seguro de
    /// una ruta que lo incluye si la edición cambia su valor de la mercancía; <c>null</c> = se conserva la tasa que ya tenía la ruta.
    /// </summary>
    public decimal? TasaSeguroVigente { get; init; }
}

/// <summary>Lo que hizo <see cref="clsCotizacionSolicitud.AplicarEdicion"/>: las rutas que se quitaron y el orden de las nuevas.</summary>
public sealed record clsResultadoEdicion(IReadOnlyList<clsLineaSolicitud> RutasQuitadas, IReadOnlyList<int> RutasAgregadas);

/// <summary>
/// ¿Hay que volver a pedir los camiones de una ruta al cubicador? Sí si cambia lo que
/// recibe el cubicador: el peso declarado, la clase de viaje (viaja como preferencia, <c>clsPlanificadorCamionesCubicador</c>) o las
/// mercancías (código, cantidad, medidas o peso, en orden; el nombre es una copia del catálogo y no cuenta). Origen, destino, ciudades,
/// horas, valores, tipología, exclusivo y consolidado no se cubican: los camiones vigentes se conservan.
/// </summary>
public static class clsReglaRecubicacion
{
    /// <summary>
    /// <see cref="Exige(clsLineaSolicitudCosteo, clsLineaSolicitudCosteo)"/> y, con <paramref name="vehiculoCuenta"/> (los
    /// camiones de la ruta salen del vehículo elegido: no los asignó el cubicador), también un cambio del vehículo o la carrocería elegidos
    /// (sin mayúsculas, tildes ni separadores): en una ruta sin mercancías cubicables cambiar solo el vehículo es un cambio que viaja.
    /// </summary>
    public static bool Exige(clsLineaSolicitudCosteo anterior, clsLineaSolicitudCosteo nueva, bool vehiculoCuenta) =>
        Exige(anterior, nueva) || (vehiculoCuenta && !MismoVehiculo(anterior, nueva));

    /// <summary>¿El mismo vehículo y la misma carrocería elegidos? (<see cref="clsVehiculoLineaSolicitud.MismoCodigo"/>).</summary>
    public static bool MismoVehiculo(clsLineaSolicitudCosteo a, clsLineaSolicitudCosteo b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return MismoCodigo(a.TipoVehiculo, b.TipoVehiculo) && MismoCodigo(a.Carroceria, b.Carroceria);
    }

    private static bool MismoCodigo(string? a, string? b) =>
        (Normalizar(a), Normalizar(b)) switch
        {
            (null, null) => true,
            ({ } x, { } y) => clsVehiculoLineaSolicitud.MismoCodigo(x, y),
            _ => false,
        };

    public static bool Exige(clsLineaSolicitudCosteo anterior, clsLineaSolicitudCosteo nueva)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        ArgumentNullException.ThrowIfNull(nueva);
        return anterior.PesoKg != nueva.PesoKg
               || !string.Equals(Normalizar(anterior.ClaseViaje), Normalizar(nueva.ClaseViaje), StringComparison.OrdinalIgnoreCase)
               || !MismasMercancias(anterior.Posiciones, nueva.Posiciones);
    }

    private static bool MismasMercancias(IReadOnlyList<clsPosicionCarga> anteriores, IReadOnlyList<clsPosicionCarga> nuevas) =>
        anteriores.Count == nuevas.Count && anteriores.Zip(nuevas).All(p => MismaMercancia(p.First, p.Second));

    private static bool MismaMercancia(clsPosicionCarga? a, clsPosicionCarga? b) =>
        a is not null && b is not null
        && string.Equals(Normalizar(a.CodigoMercancia), Normalizar(b.CodigoMercancia), StringComparison.OrdinalIgnoreCase)
        && a.Cantidad == b.Cantidad && a.LargoCm == b.LargoCm && a.AnchoCm == b.AnchoCm && a.AltoCm == b.AltoCm && a.PesoKg == b.PesoKg;

    private static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
