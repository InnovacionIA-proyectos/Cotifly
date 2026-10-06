namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Línea de la solicitud de Nexo de una cotización (<c>dbo_cotifly.CotizacionLineasSolicitud</c>, script 0005, HU-011.2, ADR-017):
/// los campos de <c>CapturaLineaRequest</c>, el id de la línea en Nexo, su estado de costeo y su precio. Una cotización del
/// formulario de HU-005.5 tiene una; una licitación, N. <see cref="Costo"/> es interno (costo de Nexo): nunca va al comercial.
/// <see cref="CreatedAt"/> y <see cref="UpdatedAt"/> los asigna la persistencia.
/// </summary>
public sealed class clsCotizacionLineaSolicitud
{
    private clsCotizacionLineaSolicitud()
    {
    }

    public Guid Id { get; private set; }
    public Guid CotizacionId { get; private set; }

    /// <summary>1 para la primera línea (único por cotización): el orden en que viajó a Nexo.</summary>
    public int Orden { get; private set; }

    /// <summary>Índice (base 0) de la fila en <c>hojas[h].filas</c> del análisis del Excel; <c>null</c> sin archivo (HU-011.3).</summary>
    public int? FilaOrigen { get; private set; }

    public string Origen { get; private set; } = string.Empty;
    public string Destino { get; private set; } = string.Empty;

    /// <summary>Id de <c>tbl_municipios</c> de Nexo del origen (script 0007, G6 de HU-013.10).</summary>
    public int? OrigenMunicipioId { get; private set; }

    /// <summary>Id de <c>tbl_municipios</c> de Nexo del destino (script 0007).</summary>
    public int? DestinoMunicipioId { get; private set; }
    public string? TipoVehiculo { get; private set; }
    public string? Carroceria { get; private set; }
    public string? Tipologia { get; private set; }
    public string? ClaseViaje { get; private set; }
    public string? ClaseContenedor { get; private set; }
    public bool? Exclusivo { get; private set; }
    public bool? Consolidado { get; private set; }
    public bool? RetornaContenedor { get; private set; }
    public string? CiudadDevolucionContenedor { get; private set; }
    public bool? LevanteContenedorDiferente { get; private set; }
    public string? CiudadLevanteContenedor { get; private set; }
    public decimal? ValorAprovisionamiento { get; private set; }
    public decimal? PesoKg { get; private set; }
    public decimal? ValorMercancia { get; private set; }

    /// <summary>Id de la línea en Nexo (<c>solicitud.lineas[].id</c> de <c>GET /api/solicitudes/{id}</c>).</summary>
    public string? LineaSolicitudNexoId { get; private set; }

    /// <summary>Estado del costeo de esta línea (mismos códigos que la cotización); <c>null</c> mientras no se envía.</summary>
    public EstadoCosteo? EstadoCosteo { get; private set; }

    /// <summary>Costo de Nexo de la línea (flete + aprovisionamiento + retorno). Interno.</summary>
    public decimal? Costo { get; private set; }

    /// <summary>Precio de la línea con el margen de la cotización (HU-005.7).</summary>
    public decimal? PrecioFinal { get; private set; }

    /// <summary>
    /// JSON de servicios conexos propios de la línea. Reservado: Nexo los guarda por cotización y queda pendiente de Gerencia
    /// Comercial si se cotizan por línea (ADR-017); hoy siempre <c>null</c>.
    /// </summary>
    public string? ServiciosConexos { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Líneas de la cotización a partir de las de la solicitud ya validada (<see cref="clsLineaSolicitudCosteo.Errores"/> vacía).
    /// <paramref name="filas"/> trae, en el mismo orden, el índice de la fila del Excel de cada línea (o <c>null</c>).
    /// </summary>
    public static IReadOnlyList<clsCotizacionLineaSolicitud> Desde(
        Guid cotizacionId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, IReadOnlyList<int?>? filas = null)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        if (filas is not null && filas.Count != lineas.Count)
        {
            throw new ArgumentException("Debe haber una fila (o null) por línea.", nameof(filas));
        }

        return [.. lineas.Select((l, i) => new clsCotizacionLineaSolicitud
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            Orden = i + 1,
            FilaOrigen = filas?[i],
            Origen = l.Origen.Trim(),
            Destino = l.Destino.Trim(),
            OrigenMunicipioId = l.OrigenMunicipioId,
            DestinoMunicipioId = l.DestinoMunicipioId,
            TipoVehiculo = Normalizar(l.TipoVehiculo),
            Carroceria = Normalizar(l.Carroceria),
            Tipologia = Normalizar(l.Tipologia),
            ClaseViaje = Normalizar(l.ClaseViaje),
            ClaseContenedor = Normalizar(l.ClaseContenedor),
            Exclusivo = l.Exclusivo,
            Consolidado = l.Consolidado,
            RetornaContenedor = l.RetornaContenedor,
            CiudadDevolucionContenedor = Normalizar(l.CiudadDevolucionContenedor),
            LevanteContenedorDiferente = l.LevanteContenedorDiferente,
            CiudadLevanteContenedor = Normalizar(l.CiudadLevanteContenedor),
            ValorAprovisionamiento = l.ValorAprovisionamiento,
            PesoKg = l.PesoKg,
            ValorMercancia = l.ValorMercancia,
        })];
    }

    /// <summary>La línea como dato de la solicitud (para reenviarla a Nexo), con sus posiciones en orden.</summary>
    public clsLineaSolicitudCosteo ALinea(IEnumerable<clsCotizacionPosicion> posiciones)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        return new clsLineaSolicitudCosteo
        {
            Origen = Origen,
            Destino = Destino,
            OrigenMunicipioId = OrigenMunicipioId,
            DestinoMunicipioId = DestinoMunicipioId,
            TipoVehiculo = TipoVehiculo,
            Carroceria = Carroceria,
            Tipologia = Tipologia,
            ClaseViaje = ClaseViaje,
            ClaseContenedor = ClaseContenedor,
            Exclusivo = Exclusivo,
            Consolidado = Consolidado,
            RetornaContenedor = RetornaContenedor,
            CiudadDevolucionContenedor = CiudadDevolucionContenedor,
            LevanteContenedorDiferente = LevanteContenedorDiferente,
            CiudadLevanteContenedor = CiudadLevanteContenedor,
            ValorAprovisionamiento = ValorAprovisionamiento,
            PesoKg = PesoKg,
            ValorMercancia = ValorMercancia,
            Posiciones = [.. posiciones.OrderBy(p => p.Orden).Select(p => p.APosicion())],
        };
    }

    /// <summary>
    /// Re-solicitud en el mismo folio (HU-013.4): la línea vuelve a viajar a Nexo como una solicitud nueva, con líneas nuevas. Se
    /// olvidan el id de la línea anterior en Nexo, su estado, su costo y su precio.
    /// </summary>
    public void ReiniciarCosteo()
    {
        LineaSolicitudNexoId = null;
        EstadoCosteo = null;
        Costo = null;
        PrecioFinal = null;
    }

    /// <summary>Nexo recibió la solicitud: la línea queda con el mismo estado inicial que la cotización.</summary>
    public void RegistrarEnvio(EstadoCosteo estado) => EstadoCosteo = estado;

    /// <summary>Id de la línea en Nexo, asignado la primera vez que el polling ve la solicitud (se conserva después).</summary>
    public bool AsignarLineaNexo(string lineaSolicitudNexoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineaSolicitudNexoId);
        var id = lineaSolicitudNexoId.Trim();
        if (LineaSolicitudNexoId == id)
        {
            return false;
        }

        LineaSolicitudNexoId = id;
        return true;
    }

    /// <summary>
    /// Estado y costo de la línea en la última consulta del polling (HU-005.6); <paramref name="costo"/> <c>null</c> = sin flete.
    /// Devuelve <c>true</c> si algo cambió. No toca el precio (ver <see cref="RegistrarPrecio"/>).
    /// </summary>
    public bool RegistrarCosteo(EstadoCosteo estado, decimal? costo)
    {
        if (costo is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(costo), "El costo no puede ser negativo.");
        }

        var cambio = EstadoCosteo != estado || Costo != costo;
        EstadoCosteo = estado;
        Costo = costo;
        if (costo is null)
        {
            PrecioFinal = null;
        }

        return cambio;
    }

    /// <summary>Solo el estado (la cotización ya tiene PDF: costo y precio de la versión se conservan). Devuelve <c>true</c> si cambió.</summary>
    public bool RegistrarEstado(EstadoCosteo estado)
    {
        var cambio = EstadoCosteo != estado;
        EstadoCosteo = estado;
        return cambio;
    }

    /// <summary>Precio de la línea con el margen (HU-005.7). Devuelve <c>true</c> si cambió.</summary>
    public bool RegistrarPrecio(decimal? precioFinal)
    {
        if (precioFinal is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(precioFinal), "El precio no puede ser negativo.");
        }

        var cambio = PrecioFinal != precioFinal;
        PrecioFinal = precioFinal;
        return cambio;
    }

    /// <summary>¿La línea ya tiene flete de Nexo? (<c>autocosteo</c> o <c>entregada</c> con costo.)</summary>
    public bool TieneCosteo => Costo is not null && EstadoCosteo is { } estado && clsDerivacionEstadoCosteo.TieneCosteo(estado);

    private static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
