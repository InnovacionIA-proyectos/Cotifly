namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Foto del costeo de una solicitud en Nexo (HU-005.6, ADR-015): el estado de la solicitud (<c>Pendiente</c>, <c>EnCosteo</c>,
/// <c>Entregada</c>, <c>Descartada</c>), los ids de sus líneas y la cotización de Nexo (nula si la solicitud está descartada).
/// </summary>
public sealed record clsCosteoConsultadoNexo(string EstadoSolicitud, IReadOnlyList<string> LineasSolicitud, clsCotizacionNexo? Cotizacion)
{
    public const string SolicitudEntregada = "Entregada";
    public const string SolicitudDescartada = "Descartada";
}

/// <summary><c>CotizacionDto</c> de Nexo reducido a lo que usa Cotifly.</summary>
/// <param name="Estado"><c>Borrador</c> | <c>Entregada</c>.</param>
/// <param name="Resultado"><c>SinDefinir</c> | <c>Ganada</c> | <c>Perdida</c>.</param>
public sealed record clsCotizacionNexo(
    string Id, string Estado, string Resultado, IReadOnlyList<clsLineaCosteoNexo> Lineas, IReadOnlyList<clsConexoNexo> Conexos)
{
    public const string EstadoEntregada = "Entregada";
    public const string EstadoBorrador = "Borrador";
}

/// <summary>Línea con flete de la cotización de Nexo (<c>CotizacionLineaDto</c>).</summary>
public sealed record clsLineaCosteoNexo(string? LineaSolicitudId, decimal ValorFlete, decimal? ValorAprovisionamiento, decimal? ValorRetornoContenedor)
{
    /// <summary>Costo de la línea: flete + aprovisionamiento + retorno del contenedor (lo que suma Nexo en su total).</summary>
    public decimal Costo => ValorFlete + (ValorAprovisionamiento ?? 0m) + (ValorRetornoContenedor ?? 0m);
}

/// <summary>Servicio conexo de la cotización de Nexo (<c>CotizacionConexoDto</c>): <paramref name="ValorAplicado"/> es un costo.</summary>
public sealed record clsConexoNexo(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal ValorAplicado);

/// <summary>
/// Derivación de <c>estado_costeo</c> (HU-005.7, ADR-015 §4), la misma regla de la bandeja de Nexo
/// (<c>EfSolicitudesCatalogoRepository</c>): cruzar las líneas de la solicitud contra las líneas de la cotización por
/// <c>LineaSolicitudId</c>.
/// </summary>
public static class clsDerivacionEstadoCosteo
{
    /// <summary>
    /// <c>Descartada</c> → <see cref="EstadoCosteo.Descartada"/>; cotización (o solicitud) <c>Entregada</c> →
    /// <see cref="EstadoCosteo.Entregada"/>; toda línea de la solicitud con flete → <see cref="EstadoCosteo.Autocosteo"/>; si no,
    /// <see cref="EstadoCosteo.PendienteCosteo"/>. Una solicitud sin líneas nunca es autocosteo.
    /// </summary>
    public static EstadoCosteo Derivar(clsCosteoConsultadoNexo foto)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.Equals(foto.EstadoSolicitud, clsCosteoConsultadoNexo.SolicitudDescartada, StringComparison.OrdinalIgnoreCase))
        {
            return EstadoCosteo.Descartada;
        }

        if (string.Equals(foto.Cotizacion?.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase)
            || string.Equals(foto.EstadoSolicitud, clsCosteoConsultadoNexo.SolicitudEntregada, StringComparison.OrdinalIgnoreCase))
        {
            return EstadoCosteo.Entregada;
        }

        var costeadas = (foto.Cotizacion?.Lineas ?? []).Select(l => l.LineaSolicitudId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return foto.LineasSolicitud.Count > 0 && foto.LineasSolicitud.All(costeadas.Contains)
            ? EstadoCosteo.Autocosteo
            : EstadoCosteo.PendienteCosteo;
    }

    /// <summary>
    /// Estado de UNA línea (HU-011.2, ADR-017), con la misma regla: <c>Descartada</c> → <see cref="EstadoCosteo.Descartada"/>;
    /// cotización (o solicitud) <c>Entregada</c> → <see cref="EstadoCosteo.Entregada"/>; la línea con flete →
    /// <see cref="EstadoCosteo.Autocosteo"/>; si no, <see cref="EstadoCosteo.PendienteCosteo"/>. Una entregada sin flete para la
    /// línea queda <c>entregada</c> sin costo (Nexo la entregó sin esa línea): <see cref="CostoLinea"/> lo distingue.
    /// </summary>
    public static EstadoCosteo DerivarLinea(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        var cotizacion = Derivar(foto);
        if (cotizacion is EstadoCosteo.Descartada or EstadoCosteo.Entregada)
        {
            return cotizacion;
        }

        return CostoLinea(foto, lineaSolicitudNexoId) is null ? EstadoCosteo.PendienteCosteo : EstadoCosteo.Autocosteo;
    }

    /// <summary>
    /// Costo de Nexo de una línea (suma de las líneas de la cotización de Nexo con ese <c>lineaSolicitudId</c>: flete +
    /// aprovisionamiento + retorno), o <c>null</c> si la línea no tiene flete.
    /// </summary>
    public static decimal? CostoLinea(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.IsNullOrWhiteSpace(lineaSolicitudNexoId) || foto.Cotizacion is not { } cotizacion)
        {
            return null;
        }

        var lineas = cotizacion.Lineas.Where(l => string.Equals(l.LineaSolicitudId, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase)).ToList();
        return lineas.Count == 0 ? null : lineas.Sum(l => l.Costo);
    }

    /// <summary>¿El polling deja de consultar en este estado? (<c>entregada</c> y <c>descartada</c> son terminales.)</summary>
    public static bool EsTerminal(EstadoCosteo estado) => estado is EstadoCosteo.Entregada or EstadoCosteo.Descartada or EstadoCosteo.CosteoVencido;

    /// <summary>¿El costeo está completo y se puede poner precio? (<c>autocosteo</c> o <c>entregada</c>.)</summary>
    public static bool TieneCosteo(EstadoCosteo estado) => estado is EstadoCosteo.Autocosteo or EstadoCosteo.Entregada;
}
