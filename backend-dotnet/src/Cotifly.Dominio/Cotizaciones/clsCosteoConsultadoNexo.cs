namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Foto del costeo de una solicitud en Nexo (HU-005.6, ADR-015): el estado de la solicitud (<c>Pendiente</c>, <c>EnCosteo</c>,
/// <c>Entregada</c>, <c>Descartada</c>), los ids de sus líneas y la cotización de Nexo (nula si la solicitud está descartada).
/// </summary>
public sealed record clsCosteoConsultadoNexo(string EstadoSolicitud, IReadOnlyList<string> LineasSolicitud, clsCotizacionNexo? Cotizacion)
{
    public const string SolicitudEntregada = "Entregada";
    public const string SolicitudDescartada = "Descartada";

    /// <summary>
    /// Contenido de cada línea en Nexo (<c>lineas[]</c> de primer nivel de <c>SolicitudCosteoDto</c>: ruta, tipología, peso, vehículo y
    /// carrocería). Sirve para asociar las líneas de Cotifly con las de Nexo por contenido: Nexo no garantiza el orden de
    /// <c>solicitud.lineas[]</c> (ADR-017 §3). Vacío si Nexo no lo trae.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitudNexo> Detalle { get; init; } = [];
}

/// <summary>Una línea de la solicitud en Nexo (<c>LineaCosteoDto</c>): <paramref name="Ruta"/> es <c>"Origen → Destino"</c>.</summary>
public sealed record clsLineaSolicitudNexo(string Id, string? Ruta, string? Tipologia, decimal? Peso, string? TipoVehiculo, string? Carroceria);

/// <summary>
/// Asocia las líneas de Cotifly con las de Nexo por CONTENIDO (ADR-017 §3), no por orden: Nexo carga las líneas sin <c>OrderBy</c> y
/// sus ids son GUID aleatorios, así que <c>lineas[]</c> llega en cualquier orden. Clave: la ruta <c>"Origen → Destino"</c> normalizada
/// (mayúsculas, sin tildes ni espacios repetidos) y, cuando ambos lados lo traen, el peso, la tipología, el tipo de vehículo y la
/// carrocería. Solo se asocia un par cuando es único en ambos sentidos; dos líneas idénticas, o una sin candidato, quedan sin asociar
/// (sin precio, con motivo). Con una sola línea en cada lado se asocian directamente (HU-005.5, siempre fue así).
/// </summary>
public static class clsEmparejadorLineasNexo
{
    /// <summary>
    /// Ids de Nexo por id de línea de Cotifly, solo para las que se asocian con certeza. <paramref name="yaAsignados"/> son los ids de
    /// Nexo que ya tienen dueño (no se reasignan).
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Emparejar(
        IReadOnlyList<clsCotizacionLineaSolicitud> lineas, IReadOnlyList<clsLineaSolicitudNexo> nexo, IReadOnlySet<string>? yaAsignados = null)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(nexo);
        var libres = nexo.Where(n => yaAsignados is null || !yaAsignados.Contains(n.Id)).ToList();
        var pendientes = lineas.ToList();
        var resultado = new Dictionary<Guid, string>();
        bool avanzo;
        do
        {
            avanzo = false;
            foreach (var linea in pendientes.ToList())
            {
                var candidatos = libres.Where(n => Coincide(linea, n)).ToList();
                if (candidatos.Count != 1)
                {
                    continue;
                }

                var candidato = candidatos[0];
                if (pendientes.Count(l => Coincide(l, candidato)) != 1)
                {
                    continue;
                }

                resultado[linea.Id] = candidato.Id;
                pendientes.Remove(linea);
                libres.Remove(candidato);
                avanzo = true;
            }
        }
        while (avanzo && pendientes.Count > 0);

        return resultado;
    }

    /// <summary>¿La línea de Nexo puede ser esta línea de Cotifly? Misma ruta y sin contradicción en los datos que traen ambos.</summary>
    public static bool Coincide(clsCotizacionLineaSolicitud linea, clsLineaSolicitudNexo nexo)
    {
        ArgumentNullException.ThrowIfNull(linea);
        ArgumentNullException.ThrowIfNull(nexo);
        if (nexo.Ruta is null || Normalizar(nexo.Ruta) != Normalizar($"{linea.Origen} → {linea.Destino}"))
        {
            return false;
        }

        return Compatible(linea.Tipologia, nexo.Tipologia)
               && Compatible(linea.TipoVehiculo, nexo.TipoVehiculo)
               && Compatible(linea.Carroceria, nexo.Carroceria)
               && (linea.PesoKg is not { } peso || nexo.Peso is not { } pesoNexo || Math.Round(peso, 2) == Math.Round(pesoNexo, 2));
    }

    private static bool Compatible(string? cotifly, string? nexo) =>
        string.IsNullOrWhiteSpace(cotifly) || string.IsNullOrWhiteSpace(nexo) || Normalizar(cotifly) == Normalizar(nexo);

    /// <summary>Mayúsculas, sin tildes, la flecha y los espacios normalizados.</summary>
    public static string Normalizar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var sinTildes = new string(texto.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        return string.Join(' ', sinTildes.Replace("->", "→", StringComparison.Ordinal).ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
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

/// <summary>
/// Línea con flete de la cotización de Nexo (<c>CotizacionLineaDto</c>). <paramref name="Id"/> es el id de la línea de la COTIZACIÓN de
/// Nexo (<c>clsCotizacionLineaDto.id</c>), distinto de <paramref name="LineaSolicitudId"/> (la línea de la solicitud): es el
/// <c>lineaId</c> que pide <c>POST /api/cotizaciones/{id}/conexos</c> (HU-013.3, ADR-018 §4). Se lee cada vez que se lee la
/// cotización de Nexo; <c>null</c> si Nexo no lo trae.
/// </summary>
public sealed record clsLineaCosteoNexo(
    string? LineaSolicitudId, decimal ValorFlete, decimal? ValorAprovisionamiento, decimal? ValorRetornoContenedor, string? Id = null)
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
