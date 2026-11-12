namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Lo que una lectura de Nexo dice de un camión (<see cref="clsCotizacionSolicitud.AplicarFotoNexo"/>): su estado y, si
/// <paramref name="ConCostos"/>, el flete y el retorno (<c>null</c> = sin flete) y el vehículo que puso la Mesa de Costeo. Sin costos solo
/// cambia el estado (la solicitud ya tiene PDF: el precio de la versión se conserva). Con <paramref name="Estado"/> =
/// <c>rechazada</c>, <paramref name="MotivoRechazo"/> es el motivo que mandó Nexo. <paramref name="LineaNoRechazada"/>: el detalle de la foto
/// trae el estado de la línea y NO es <c>rechazada</c> (la Mesa deshizo el rechazo): deshace el de Cotifly aunque la lectura ya diga
/// <c>entregada</c> (la Mesa pudo deshacerlo, costear y entregar entre dos consultas del polling).
/// </summary>
public sealed record clsCosteoCamionNexo(
    Guid CamionId, EstadoCosteo Estado, bool ConCostos, decimal? Flete = null, decimal? Retorno = null, string? TipoVehiculo = null, string? Carroceria = null,
    string? MotivoRechazo = null, bool LineaNoRechazada = false);
