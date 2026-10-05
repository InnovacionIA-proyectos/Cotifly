namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Respuesta que lleva la versión de la fila de la cotización (ADR-018 §8): la API la publica también en el encabezado <c>ETag</c>
/// de toda respuesta 200 o 201 (filtro <c>clsEtagVersionFilter</c>), así el frontend la reenvía en <c>If-Match</c>.
/// </summary>
public interface IClsConVersionFila
{
    /// <summary><c>Cotizaciones.rowVersion</c> en base64, o <c>null</c> si la cotización todavía no tiene versión.</summary>
    string? RowVersion { get; }
}
