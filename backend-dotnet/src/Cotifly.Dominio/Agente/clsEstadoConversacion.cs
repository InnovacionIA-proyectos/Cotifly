using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Agente;

/// <summary>
/// Estado estructurado de la conversación (slot-filling, ADR-004). Uno por sesión; se persiste como
/// JSON en <c>Sesiones.estadoAgente</c> (script 0002). Es inmutable: cada cambio produce una copia
/// (<c>with</c>). Solo lo modifica <c>clsEstadoConversacionService</c> (#12); el LLM nunca lo escribe.
/// </summary>
public sealed record clsEstadoConversacion
{
    /// <summary>Estado inicial de una sesión nueva.</summary>
    public static clsEstadoConversacion Vacio { get; } = new();

    /// <summary>Valor de <see cref="FuenteCosto"/> cuando el costo lo escribió el comercial en el chat.</summary>
    public const string FuenteCostoManual = "manual";

    // Cliente
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }
    public OrigenCliente? OrigenCliente { get; init; }

    /// <summary>BP de SAP del cliente. Solo vive en el estado; nunca se persiste en <c>Cotizaciones</c>.</summary>
    public string? BpCliente { get; init; }
    public bool ClienteConfirmado { get; init; }
    public IReadOnlyList<clsCandidatoCliente> CandidatosCliente { get; init; } = [];

    // Solicitud
    public int? LineaNegocioId { get; init; }
    public string? LineaNegocio { get; init; }
    public string? Origen { get; init; }
    public string? Destino { get; init; }
    public string? TipoVehiculo { get; init; }
    public string? TipoCarga { get; init; }
    public decimal? PesoKg { get; init; }

    /// <summary>Municipio del catálogo de Nexo que resolvió <c>resolver_municipio</c> para el origen (HU-005.13); nulo si es texto libre.</summary>
    public clsMunicipioResuelto? OrigenMunicipio { get; init; }

    /// <summary>Municipio del catálogo de Nexo que resolvió <c>resolver_municipio</c> para el destino (HU-005.13); nulo si es texto libre.</summary>
    public clsMunicipioResuelto? DestinoMunicipio { get; init; }

    /// <summary>Municipios candidatos para el origen, pendientes de que el comercial elija uno (texto ambiguo, HU-005.13).</summary>
    public IReadOnlyList<clsMunicipioResuelto> CandidatosOrigen { get; init; } = [];

    /// <summary>Municipios candidatos para el destino, pendientes de que el comercial elija uno (texto ambiguo, HU-005.13).</summary>
    public IReadOnlyList<clsMunicipioResuelto> CandidatosDestino { get; init; } = [];

    // Resultado
    public decimal? Costo { get; init; }

    /// <summary>
    /// De dónde salió <see cref="Costo"/>: la fuente de <c>buscar_tarifa</c> (ej. <c>nexo</c>) o <see cref="FuenteCostoManual"/>
    /// si lo escribió el comercial (#12). Solo un costo de la fuente se impone sobre el que manda el LLM.
    /// </summary>
    public string? FuenteCosto { get; init; }
    public string? CosteoId { get; init; }

    /// <summary>Margen de la política de la línea (<c>MargenesPolitica</c>), referencia de la trazabilidad.</summary>
    public decimal? MargenPolitica { get; init; }

    /// <summary>Margen que propuso <c>obtener_margen</c> (SAP TM o política): referencia de un ajuste negociado (#12).</summary>
    public decimal? MargenSugerido { get; init; }
    public decimal? MargenAplicado { get; init; }
    public OrigenMargen? OrigenMargen { get; init; }
    public string? MetodoCalculo { get; init; }
    public decimal? PrecioFinal { get; init; }

    /// <summary>
    /// Moneda de la cotización (<c>COP</c> por defecto, o <c>USD</c>, HU-012.2): la elige el comercial (selector del formulario web o su
    /// mensaje); el costo siempre está en COP y <see cref="PrecioFinal"/> en esta moneda.
    /// </summary>
    public string? Moneda { get; init; }

    /// <summary>TRM oficial con que se convirtió <see cref="PrecioFinal"/> a USD (la de <c>calcular_precio</c>); nula o 0 en COP.</summary>
    public decimal? TrmAplicada { get; init; }

    /// <summary>Precio en pesos antes de convertir a USD (respaldo del equivalente en COP que puede citar el agente); nulo en COP.</summary>
    public decimal? PrecioCop { get; init; }

    /// <summary>Rango de vigencia de <see cref="TrmAplicada"/> (Superintendencia Financiera); nulo en COP.</summary>
    public DateOnly? TrmVigenciaDesde { get; init; }

    /// <inheritdoc cref="TrmVigenciaDesde"/>
    public DateOnly? TrmVigenciaHasta { get; init; }

    // Documento
    public string? FolioEnProgreso { get; init; }
    public Guid? CotizacionId { get; init; }
    public int? Version { get; init; }
    public bool ConfirmacionPendiente { get; init; }

    /// <summary>
    /// ¿El precio en dólares se calculó con una TRM que ya no está vigente en <paramref name="hoy"/> (America/Bogota)? Entonces no se emite
    /// el PDF: se recalcula con la TRM vigente y se vuelve a previsualizar (HU-012.2). Sin rango conocido (ej. una nueva versión sembrada
    /// desde una cotización guardada) también cuenta como vencida. En COP o sin precio, nunca.
    /// </summary>
    public bool TrmVencidaEn(DateOnly hoy) =>
        PrecioFinal is not null
        && !Precio.clsConversorMoneda.EsPesos(Moneda)
        && (TrmVigenciaHasta is not { } hasta || hoy > hasta || (TrmVigenciaDesde is { } desde && hoy < desde));

    /// <summary>
    /// True si el origen o el destino quedaron ambiguos en el catálogo de municipios y el comercial todavía no eligió: el orquestador
    /// no deja buscar la tarifa, calcular el precio ni generar el PDF hasta que elija (HU-005.13). Es un método, no una propiedad,
    /// para que no viaje en el JSON de <c>Sesiones.estadoAgente</c>.
    /// </summary>
    public bool TieneMunicipiosPorConfirmar() => CandidatosOrigen.Count > 0 || CandidatosDestino.Count > 0;

    /// <summary>Igualdad por valor, incluidas las listas de candidatos (un record compara listas por referencia).</summary>
    public bool Equals(clsEstadoConversacion? other) =>
        other is not null
        && NitCliente == other.NitCliente
        && ClienteNombre == other.ClienteNombre
        && OrigenCliente == other.OrigenCliente
        && BpCliente == other.BpCliente
        && ClienteConfirmado == other.ClienteConfirmado
        && CandidatosCliente.SequenceEqual(other.CandidatosCliente)
        && LineaNegocioId == other.LineaNegocioId
        && LineaNegocio == other.LineaNegocio
        && Origen == other.Origen
        && Destino == other.Destino
        && TipoVehiculo == other.TipoVehiculo
        && TipoCarga == other.TipoCarga
        && PesoKg == other.PesoKg
        && OrigenMunicipio == other.OrigenMunicipio
        && DestinoMunicipio == other.DestinoMunicipio
        && CandidatosOrigen.SequenceEqual(other.CandidatosOrigen)
        && CandidatosDestino.SequenceEqual(other.CandidatosDestino)
        && Costo == other.Costo
        && FuenteCosto == other.FuenteCosto
        && CosteoId == other.CosteoId
        && MargenPolitica == other.MargenPolitica
        && MargenSugerido == other.MargenSugerido
        && MargenAplicado == other.MargenAplicado
        && OrigenMargen == other.OrigenMargen
        && MetodoCalculo == other.MetodoCalculo
        && PrecioFinal == other.PrecioFinal
        && Moneda == other.Moneda
        && TrmAplicada == other.TrmAplicada
        && PrecioCop == other.PrecioCop
        && TrmVigenciaDesde == other.TrmVigenciaDesde
        && TrmVigenciaHasta == other.TrmVigenciaHasta
        && FolioEnProgreso == other.FolioEnProgreso
        && CotizacionId == other.CotizacionId
        && Version == other.Version
        && ConfirmacionPendiente == other.ConfirmacionPendiente;

    public override int GetHashCode() =>
        HashCode.Combine(NitCliente, LineaNegocioId, CotizacionId, FolioEnProgreso, PrecioFinal, CandidatosCliente.Count);
}
