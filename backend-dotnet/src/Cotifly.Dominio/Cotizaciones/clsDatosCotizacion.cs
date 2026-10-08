using Cotifly.Dominio.Clientes;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Datos con los que nace la cabecera de una solicitud. Solo <see cref="Folio"/> es obligatorio; el resto se valida en
/// <see cref="clsCotizacionSolicitud.Crear"/>. La línea de negocio, el costo, el margen y el precio son de cada ruta (HU-021, ADR-019).
/// </summary>
public sealed record clsDatosCotizacion
{
    public required string Folio { get; init; }
    public int Version { get; init; } = 1;
    public string? ClienteNombre { get; init; }
    public string? NitCliente { get; init; }
    public OrigenCliente? OrigenCliente { get; init; }
    public string Moneda { get; init; } = clsCotizacionSolicitud.MonedaPesos;
    public decimal TrmAplicada { get; init; }
    public string? PdfPath { get; init; }
    public EstadoCosteo? EstadoCosteo { get; init; }
    public Guid? UsuarioId { get; init; }
    public string? SpaceId { get; init; }
    public string? ThreadId { get; init; }
    public DateTimeOffset? CosteoListoAt { get; init; }
}
