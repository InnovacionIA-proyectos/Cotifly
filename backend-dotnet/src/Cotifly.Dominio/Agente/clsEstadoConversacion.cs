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

    // Documento
    public string? FolioEnProgreso { get; init; }
    public Guid? CotizacionId { get; init; }
    public int? Version { get; init; }
    public bool ConfirmacionPendiente { get; init; }

    /// <summary>Igualdad por valor, incluida la lista de candidatos (un record compara listas por referencia).</summary>
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
        && FolioEnProgreso == other.FolioEnProgreso
        && CotizacionId == other.CotizacionId
        && Version == other.Version
        && ConfirmacionPendiente == other.ConfirmacionPendiente;

    public override int GetHashCode() =>
        HashCode.Combine(NitCliente, LineaNegocioId, CotizacionId, FolioEnProgreso, PrecioFinal, CandidatosCliente.Count);
}
