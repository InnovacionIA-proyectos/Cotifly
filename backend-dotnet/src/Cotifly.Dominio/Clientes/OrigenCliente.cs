namespace Cotifly.Dominio.Clientes;

/// <summary>
/// Origen del cliente de la cotización (<c>Cotizaciones.origenCliente</c>, ADR-004):
/// <c>cliente</c> (DimBPSolicitante) | <c>prospecto</c> (tblProspectos), según Nexo.
/// </summary>
public enum OrigenCliente
{
    Cliente,
    Prospecto,
}
