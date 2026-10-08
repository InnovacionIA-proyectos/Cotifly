namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Lo que una lectura de Nexo dice de un camión (<see cref="clsCotizacionSolicitud.AplicarFotoNexo"/>): su estado y, si
/// <paramref name="ConCostos"/>, el flete y el retorno (<c>null</c> = sin flete) y el vehículo que puso la Mesa de Costeo. Sin costos solo
/// cambia el estado (la solicitud ya tiene PDF: el precio de la versión se conserva).
/// </summary>
public sealed record clsCosteoCamionNexo(
    Guid CamionId, EstadoCosteo Estado, bool ConCostos, decimal? Flete = null, decimal? Retorno = null, string? TipoVehiculo = null, string? Carroceria = null);
