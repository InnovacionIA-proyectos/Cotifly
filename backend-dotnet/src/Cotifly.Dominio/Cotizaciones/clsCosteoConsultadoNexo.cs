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

/// <summary>
/// Una línea de la solicitud en Nexo (<c>clsLineaCosteoDto</c>): <paramref name="Ruta"/> es <c>"Origen → Destino"</c>.
/// <see cref="GrupoRutaId"/> es el id de la ruta de Cotifly que Nexo guardó y devuelve (<c>grupoRutaId</c>, HU-023.4, #159) y
/// <see cref="IdRecomendacionVehiculo"/> el camión del cubicador; <c>null</c> si la línea viajó sin ellos. No es el <c>rutaId</c> de
/// Nexo (su catálogo de rutas origen-destino), que Cotifly no usa.
/// </summary>
public sealed record clsLineaSolicitudNexo(string Id, string? Ruta, string? Tipologia, decimal? Peso, string? TipoVehiculo, string? Carroceria)
{
    /// <summary>Id de la ruta de Cotifly que Nexo guardó en la línea (<c>grupoRutaId</c>, antes N1 <c>referenciaRuta</c>), o <c>null</c>.</summary>
    public string? GrupoRutaId { get; init; }

    /// <summary>Camión de la recomendación del cubicador que Nexo guardó en la línea (<c>idRecomendacionVehiculo</c>), o <c>null</c>.</summary>
    public string? IdRecomendacionVehiculo { get; init; }
}

/// <summary>
/// Un camión de Cotifly visto por el emparejador (HU-021): la ruta (<c>"Origen → Destino"</c> con los nombres copiados), el id de la ruta
/// (<see cref="RutaId"/>: separa rutas iguales y es el <c>grupoRutaId</c> de Nexo), el camión del cubicador
/// (<see cref="IdRecomendacionVehiculo"/>) y lo que viajó a Nexo en su línea (tipología, vehículo, carrocería y peso).
/// </summary>
public sealed record clsCamionParaEmparejar(Guid Id, string Origen, string Destino, string? Tipologia, string? TipoVehiculo, string? Carroceria, decimal? PesoKg)
{
    /// <summary>Ruta de Cotifly del camión; <c>null</c> si quien arma el camión no la conoce (entonces la ruta es solo el texto).</summary>
    public Guid? RutaId { get; init; }

    /// <summary>Camión de la recomendación del cubicador (HU-023.4); <c>null</c> sin recomendación.</summary>
    public Guid? IdRecomendacionVehiculo { get; init; }

    /// <summary>
    /// HU-023.4 (decisión 6): el peso declarado de la ruta, si es el que viaja a Nexo por este camión y difiere de <see cref="PesoKg"/> (un
    /// solo camión: <see cref="clsLineaSolicitud.PesoLineaNexo"/>). La línea de Nexo puede traer cualquiera de los dos: el que viajó depende
    /// de cuántos camiones contaban al enviar (un camión descartado después cambia la cuenta). El emparejador lo usa en una segunda pasada,
    /// solo para los camiones que no se asociaron con <see cref="PesoKg"/>.
    /// </summary>
    public decimal? PesoAlterno { get; init; }

    /// <summary>El camión <paramref name="camion"/> de <paramref name="ruta"/>, con su peso y, si difiere, el de la ruta (<see cref="PesoAlterno"/>).</summary>
    public static clsCamionParaEmparejar Desde(clsLineaSolicitud ruta, clsVehiculoLineaSolicitud camion)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        ArgumentNullException.ThrowIfNull(camion);
        return new clsCamionParaEmparejar(
            camion.Id, ruta.OrigenNombre ?? string.Empty, ruta.DestinoNombre ?? string.Empty, ruta.Tipologia, camion.TipoVehiculo, camion.Carroceria,
            camion.PesoKg ?? (ruta.Vehiculos.Count == 1 ? ruta.PesoKg : null))
        {
            RutaId = ruta.Id,
            IdRecomendacionVehiculo = camion.IdRecomendacionVehiculo,
            PesoAlterno = ruta.PesoLineaNexo(camion) is { } pesoNexo && pesoNexo != camion.PesoKg ? pesoNexo : null,
        };
    }
}

/// <summary>
/// Asocia los camiones de Cotifly con las líneas de la solicitud de Nexo por CONTENIDO (ADR-017 §3, ADR-019, HU-021.7 #134), no por la
/// posición en la respuesta: Nexo carga las líneas sin <c>OrderBy</c> y sus ids son GUID aleatorios.
/// <list type="number">
/// <item><b>Camión del cubicador (HU-023.4, #159).</b> Si la línea de Nexo trae <c>grupoRutaId</c> e <c>idRecomendacionVehiculo</c>, se
/// asocia directo con el camión de esa ruta y esa recomendación: el par es único por camión, aun entre camiones idénticos, y no depende de
/// que el contenido cuadre (la Mesa pudo cambiar el vehículo). Dos líneas con el mismo par, o un par que no está en Cotifly, no se asocian
/// así: siguen por contenido.</item>
/// <item><b>Llave.</b> La ruta: el <c>grupoRutaId</c> de la ruta de Cotifly si Nexo lo devuelve o, si no, <c>"Origen → Destino"</c>
/// normalizado (mayúsculas, sin tildes ni espacios repetidos); y, cuando ambos lados lo traen, el peso, la tipología, el tipo de vehículo
/// y la carrocería. Un dato que solo trae un lado no contradice (la Mesa pudo ponerle vehículo a una línea que iba vacía).</item>
/// <item><b>Pares seguros.</b> Un camión y una línea que solo se pueden emparejar entre sí se asocian primero.</item>
/// <item><b>Empates.</b> Los camiones IDÉNTICOS de la misma ruta (misma llave) son intercambiables: valen lo mismo en Nexo. Si sus líneas
/// candidatas no las puede reclamar ningún otro camión, se asignan en orden estable: los camiones en el orden de envío (el de la lista
/// <c>camiones</c>) y las líneas por su id de Nexo (ordinal), así dos lecturas iguales dan la misma asignación. Sin el <c>grupoRutaId</c>, solo
/// si hay exactamente una línea candidata por camión: una de más puede ser la huérfana de otra ruta igual y cruzaría el costo. El riesgo que queda (sin
/// <c>grupoRutaId</c>) es que el ESTADO de dos camiones idénticos se cruce; el costo no, porque son iguales.</item>
/// <item><b>Sin asociar.</b> Camiones iguales de rutas DISTINTAS (que pueden costar distinto por datos que no viajan en la línea), datos
/// que se contradicen o un camión sin candidato quedan sin asociar: sin precio y con motivo.</item>
/// </list>
/// </summary>
public static class clsEmparejadorLineasNexo
{
    /// <summary>
    /// Ids de Nexo por id de camión de Cotifly, solo para los que se asocian (pares seguros y empates resueltos). <paramref name="camiones"/>
    /// van en el orden en que viajaron a Nexo (es el orden estable de los empates). <paramref name="yaAsignados"/> son los ids de Nexo que
    /// ya tienen dueño (no se reasignan).
    /// </summary>
    public static IReadOnlyDictionary<Guid, string> Emparejar(
        IReadOnlyList<clsCamionParaEmparejar> camiones, IReadOnlyList<clsLineaSolicitudNexo> nexo, IReadOnlySet<string>? yaAsignados = null) =>
        EmparejarConDetalle(camiones, nexo, yaAsignados).Pares;

    /// <summary>
    /// Como <see cref="Emparejar"/>, con lo que quedó pendiente para avisarlo (#134): las líneas de Nexo sin dueño (huérfanas: de un camión
    /// descartado, sobrantes o de una ruta que no se pudo asociar) y los empates que no se resolvieron porque sus candidatas no cuadran.
    /// </summary>
    public static clsEmparejamiento EmparejarConDetalle(
        IReadOnlyList<clsCamionParaEmparejar> camiones, IReadOnlyList<clsLineaSolicitudNexo> nexo, IReadOnlySet<string>? yaAsignados = null)
    {
        ArgumentNullException.ThrowIfNull(camiones);
        ArgumentNullException.ThrowIfNull(nexo);
        var libres = nexo.Where(n => yaAsignados is null || !yaAsignados.Contains(n.Id)).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        var pendientes = camiones.ToList();
        var resultado = new Dictionary<Guid, string>();
        var empates = new List<string>();
        bool avanzo;
        AsociarPorCamionCubicador(pendientes, libres, resultado);
        do
        {
            // Cada vuelta asocia al menos un par; un empate resuelto puede volver seguros otros pares.
            empates.Clear();
            avanzo = AsociarSeguros(pendientes, libres, resultado) || ResolverEmpate(pendientes, libres, resultado, empates);
        }
        while (avanzo && pendientes.Count > 0 && libres.Count > 0);

        // HU-023.4 (decisión 6): segunda pasada solo para lo que quedó, con el peso de la ruta en vez del del camión (PesoAlterno). Va al
        // final para no volver ambiguo un camión que ya cuadra con su propio peso (si se descartó otro camión después del envío).
        if (pendientes.Any(c => c.PesoAlterno is not null) && libres.Count > 0)
        {
            var segunda = EmparejarConDetalle(
                [.. pendientes.Select(c => c.PesoAlterno is { } alterno ? c with { PesoKg = alterno, PesoAlterno = null } : c)], libres);
            foreach (var (camionId, lineaId) in segunda.Pares)
            {
                Asociar(pendientes.Single(c => c.Id == camionId), libres.Single(l => l.Id == lineaId), pendientes, libres, resultado);
            }

            empates.Clear();
            empates.AddRange(segunda.EmpatesSinResolver);
        }

        return new clsEmparejamiento(resultado, [.. libres.Select(l => l.Id)], [.. pendientes.Select(c => c.Id)], empates);
    }

    /// <summary>
    /// HU-023.4 (#159): asocia las líneas de Nexo que traen <c>grupoRutaId</c> e <c>idRecomendacionVehiculo</c> con el único camión pendiente
    /// de esa ruta y esa recomendación. Un par repetido en Nexo o en Cotifly no se usa (lo resuelve el contenido).
    /// </summary>
    private static void AsociarPorCamionCubicador(List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado)
    {
        var conPar = libres
            .Select(n => (Linea: n, Par: ParCubicador(n)))
            .Where(x => x.Par is not null)
            .GroupBy(x => x.Par!.Value)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .ToList();
        foreach (var (linea, par) in conPar)
        {
            var camiones = pendientes.Where(c => c.RutaId == par!.Value.Ruta && c.IdRecomendacionVehiculo == par.Value.Recomendacion).ToList();
            if (camiones.Count == 1)
            {
                Asociar(camiones[0], linea, pendientes, libres, resultado);
            }
        }
    }

    /// <summary>El par (<c>grupoRutaId</c>, <c>idRecomendacionVehiculo</c>) de la línea de Nexo, o <c>null</c> si le falta alguno.</summary>
    private static (Guid Ruta, Guid Recomendacion)? ParCubicador(clsLineaSolicitudNexo linea) =>
        Guid.TryParse(linea.GrupoRutaId, out var ruta) && Guid.TryParse(linea.IdRecomendacionVehiculo, out var recomendacion)
            ? (ruta, recomendacion)
            : null;

    /// <summary>Asocia los pares únicos en ambos sentidos. Devuelve si asoció alguno.</summary>
    private static bool AsociarSeguros(List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado)
    {
        var avanzo = false;
        foreach (var camion in pendientes.ToList())
        {
            var candidatos = libres.Where(n => Coincide(camion, n)).ToList();
            if (candidatos.Count != 1 || pendientes.Count(c => Coincide(c, candidatos[0])) != 1)
            {
                continue;
            }

            Asociar(camion, candidatos[0], pendientes, libres, resultado);
            avanzo = true;
        }

        return avanzo;
    }

    /// <summary>
    /// Resuelve el primer grupo de camiones idénticos de una misma ruta (en orden de envío) cuyas líneas candidatas no puede reclamar
    /// ningún otro camión: las asigna en orden estable. Sin el <c>grupoRutaId</c> de Nexo en todas las candidatas, solo si hay
    /// EXACTAMENTE una candidata por camión: una candidata de más puede ser la línea huérfana de otra ruta con el mismo origen y destino (un
    /// camión descartado o sobrante) y tomarla cruzaría el costo entre rutas; ese empate queda sin resolver y se avisa en
    /// <paramref name="empates"/>. Devuelve si asoció alguno.
    /// </summary>
    private static bool ResolverEmpate(
        List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres, Dictionary<Guid, string> resultado, List<string> empates)
    {
        foreach (var grupo in pendientes.GroupBy(Firma).Select(g => g.ToList()))
        {
            var candidatos = libres.Where(n => Coincide(grupo[0], n)).ToList();
            if (candidatos.Count == 0 || pendientes.Except(grupo).Any(otro => candidatos.Any(n => Coincide(otro, n))))
            {
                continue;
            }

            var conReferencia = grupo[0].RutaId is { } rutaId
                                && candidatos.All(n => Guid.TryParse(n.GrupoRutaId, out var referencia) && referencia == rutaId);
            if (!conReferencia && candidatos.Count != grupo.Count)
            {
                empates.Add($"{grupo.Count} camiones idénticos de la ruta {grupo[0].Origen} → {grupo[0].Destino} con {candidatos.Count} líneas candidatas en Nexo");
                continue;
            }

            foreach (var (camion, linea) in grupo.Zip(candidatos))
            {
                Asociar(camion, linea, pendientes, libres, resultado);
            }

            return true;
        }

        return false;
    }

    private static void Asociar(
        clsCamionParaEmparejar camion, clsLineaSolicitudNexo linea, List<clsCamionParaEmparejar> pendientes, List<clsLineaSolicitudNexo> libres,
        Dictionary<Guid, string> resultado)
    {
        resultado[camion.Id] = linea.Id;
        pendientes.Remove(camion);
        libres.Remove(linea);
    }

    /// <summary>Lo que hace idénticos a dos camiones: la misma ruta de Cotifly y los mismos datos de su línea en Nexo.</summary>
    private static (string Ruta, string Tipologia, string Vehiculo, string Carroceria, decimal? Peso) Firma(clsCamionParaEmparejar camion) =>
        (camion.RutaId?.ToString("D") ?? Normalizar($"{camion.Origen} → {camion.Destino}"),
         NormalizarOVacio(camion.Tipologia), NormalizarOVacio(camion.TipoVehiculo), NormalizarOVacio(camion.Carroceria),
         camion.PesoKg is { } peso ? Math.Round(peso, 2) : null);

    /// <summary>
    /// ¿La línea de Nexo puede ser este camión de Cotifly? Misma ruta (por el <c>grupoRutaId</c> si ambos lados lo traen, si no por el texto
    /// <c>"Origen → Destino"</c>), el mismo camión del cubicador si ambos lo traen y sin contradicción en los datos que traen ambos.
    /// </summary>
    public static bool Coincide(clsCamionParaEmparejar camion, clsLineaSolicitudNexo nexo)
    {
        ArgumentNullException.ThrowIfNull(camion);
        ArgumentNullException.ThrowIfNull(nexo);
        if (camion.IdRecomendacionVehiculo is { } recomendacion && Guid.TryParse(nexo.IdRecomendacionVehiculo, out var deNexo) && deNexo != recomendacion)
        {
            return false; // HU-023.4: la línea es de otro camión del cubicador
        }

        if (camion.RutaId is { } rutaId && Guid.TryParse(nexo.GrupoRutaId, out var referencia))
        {
            if (referencia != rutaId)
            {
                return false;
            }
        }
        else if (nexo.Ruta is null || Normalizar(nexo.Ruta) != Normalizar($"{camion.Origen} → {camion.Destino}"))
        {
            return false;
        }

        return Compatible(camion.Tipologia, nexo.Tipologia)
               && Compatible(camion.TipoVehiculo, nexo.TipoVehiculo)
               && Compatible(camion.Carroceria, nexo.Carroceria)
               && (camion.PesoKg is not { } peso || nexo.Peso is not { } pesoNexo || Math.Round(peso, 2) == Math.Round(pesoNexo, 2));
    }

    private static bool Compatible(string? cotifly, string? nexo) =>
        string.IsNullOrWhiteSpace(cotifly) || string.IsNullOrWhiteSpace(nexo) || Normalizar(cotifly) == Normalizar(nexo);

    private static string NormalizarOVacio(string? texto) => string.IsNullOrWhiteSpace(texto) ? string.Empty : Normalizar(texto);

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

/// <summary>
/// Resultado del emparejamiento (#134): los pares camión → línea de Nexo, las líneas de Nexo que quedaron sin dueño, los camiones sin
/// asociar y los empates que no se resolvieron (texto para el aviso).
/// </summary>
public sealed record clsEmparejamiento(
    IReadOnlyDictionary<Guid, string> Pares, IReadOnlyList<string> LineasSinDueno, IReadOnlyList<Guid> CamionesSinAsociar,
    IReadOnlyList<string> EmpatesSinResolver);

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
public sealed record clsConexoNexo(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal ValorAplicado)
{
    /// <summary>
    /// Línea de la COTIZACIÓN de Nexo a la que está pegado (<c>cotizacionLineaId</c>): Cotifly lo pega a la línea del primer camión de su
    /// ruta (decisión 3), así que esta línea dice de qué ruta es. <c>null</c> si Nexo no lo trae.
    /// </summary>
    public string? CotizacionLineaId { get; init; }

    /// <summary>
    /// Servicio del catálogo de Nexo (<c>servicioConexoId</c>): el mismo id que guarda Cotifly en
    /// <see cref="clsServicioConexoRuta.IdServicioConexo"/> para los conexos capturados con la ruta. Con él se sabe si un conexo de Cotifly
    /// ya está en la cotización de Nexo (decisión 10, HU-021.9). <c>null</c> si Nexo no lo trae.
    /// </summary>
    public string? ServicioConexoId { get; init; }
}

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

    /// <summary>
    /// Flete y retorno de Nexo de un camión (suma de las líneas de la cotización de Nexo con ese <c>lineaSolicitudId</c>; el retorno 0 si
    /// Nexo no lo trae), o <c>null</c> si no tiene flete. El levante NO sale de Nexo: es la copia de Cotifly (decisión 4).
    /// </summary>
    public static (decimal Flete, decimal Retorno)? CostosCamion(clsCosteoConsultadoNexo foto, string? lineaSolicitudNexoId)
    {
        ArgumentNullException.ThrowIfNull(foto);
        if (string.IsNullOrWhiteSpace(lineaSolicitudNexoId) || foto.Cotizacion is not { } cotizacion)
        {
            return null;
        }

        var lineas = cotizacion.Lineas.Where(l => string.Equals(l.LineaSolicitudId, lineaSolicitudNexoId, StringComparison.OrdinalIgnoreCase)).ToList();
        return lineas.Count == 0 ? null : (lineas.Sum(l => l.ValorFlete), lineas.Sum(l => l.ValorRetornoContenedor ?? 0m));
    }

    /// <summary>¿El polling deja de consultar en este estado? (<c>entregada</c> y <c>descartada</c> son terminales.)</summary>
    public static bool EsTerminal(EstadoCosteo estado) => estado is EstadoCosteo.Entregada or EstadoCosteo.Descartada or EstadoCosteo.CosteoVencido;

    /// <summary>¿El costeo está completo y se puede poner precio? (<c>autocosteo</c> o <c>entregada</c>.)</summary>
    public static bool TieneCosteo(EstadoCosteo estado) => estado is EstadoCosteo.Autocosteo or EstadoCosteo.Entregada;
}
