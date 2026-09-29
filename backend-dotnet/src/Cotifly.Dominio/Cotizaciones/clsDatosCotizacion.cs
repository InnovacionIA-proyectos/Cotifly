using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Datos con los que nace una cotización. Solo <see cref="Folio"/> es obligatorio;
/// el resto se valida en <see cref="clsCotizacion.Crear"/>.
/// </summary>
public sealed record clsDatosCotizacion
{
    public required string Folio { get; init; }
    public int Version { get; init; } = 1;
    public int? LineaNegocioId { get; init; }
    public string? ClienteNombre { get; init; }
    public string? NitCliente { get; init; }
    public OrigenCliente? OrigenCliente { get; init; }
    public string? CosteoId { get; init; }
    public decimal? Costo { get; init; }
    public decimal? MargenPolitica { get; init; }
    public decimal? MargenAplicado { get; init; }
    public OrigenMargen? OrigenMargen { get; init; }
    public string? MetodoCalculo { get; init; }
    public decimal? PrecioFinal { get; init; }
    public string Moneda { get; init; } = clsCotizacion.MonedaPesos;
    public decimal TrmAplicada { get; init; }
    public string? PdfPath { get; init; }
    public EstadoCosteo? EstadoCosteo { get; init; }
    public Guid? UsuarioId { get; init; }
    public string? SpaceId { get; init; }
    public string? ThreadId { get; init; }
    public DateTimeOffset? CosteoListoAt { get; init; }
}
