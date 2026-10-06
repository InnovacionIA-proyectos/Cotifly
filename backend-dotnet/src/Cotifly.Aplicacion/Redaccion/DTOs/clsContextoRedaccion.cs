namespace Cotifly.Aplicacion.Redaccion.DTOs;

/// <summary>
/// Lo ÚNICO que recibe el redactor de la IA (HU-013.5, ADR-018 §2a): nombres y textos de presentación. Por diseño no tiene ningún
/// campo numérico: nunca lleva costos, margen, precios, totales ni la TRM (con el margen y un precio, el modelo podría deducir el
/// costo de Nexo). Tampoco lleva el folio, el NIT ni el correo del comercial.
/// </summary>
/// <param name="Cliente">Nombre del cliente (<c>Cotizaciones.clienteNombre</c>).</param>
/// <param name="LineaNegocio">Línea de negocio (ej. <c>Nacional</c>).</param>
/// <param name="Rutas">Origen y destino de cada ruta (una en una cotización spot; las del pliego, hasta <see cref="MaximoRutas"/>).</param>
/// <param name="TipoVehiculo">Tipo de vehículo (o los distintos del pliego, separados por coma).</param>
/// <param name="Conexos">Nombres de los servicios conexos incluidos (sin cantidades ni valores).</param>
/// <param name="Moneda">Moneda de la cotización (<c>COP</c> o <c>USD</c>).</param>
/// <param name="Comercial">Nombre del comercial que firma.</param>
public sealed record clsContextoRedaccion(
    string? Cliente,
    string? LineaNegocio,
    IReadOnlyList<clsRutaRedaccion> Rutas,
    string? TipoVehiculo,
    IReadOnlyList<string> Conexos,
    string Moneda,
    string? Comercial)
{
    /// <summary>Rutas que se envían al modelo como máximo (un pliego puede tener hasta 1000).</summary>
    public const int MaximoRutas = 30;
}

/// <summary>Origen y destino de una ruta (solo los nombres de las ciudades).</summary>
public sealed record clsRutaRedaccion(string Origen, string Destino);

/// <summary>Respuesta cruda del redactor: el texto del modelo (sin validar) y el modelo que respondió, si el proveedor lo informa.</summary>
public sealed record clsRespuestaRedactor(string? Texto, string? Modelo);

/// <summary>Proveedor y modelo configurados del redactor (para la auditoría).</summary>
public sealed record clsIdentidadRedactor(string Proveedor, string Modelo);

/// <summary>
/// Resultado de la redacción (HU-013.5): los textos (de la IA validados, o los estándar si no) y lo que se audita: proveedor,
/// modelo, duración y si se usó el respaldo con su motivo.
/// </summary>
public sealed record clsResultadoRedaccion(
    string Asunto,
    string Introduccion,
    string Fuente,
    string Proveedor,
    string Modelo,
    int DuracionMs,
    bool Respaldo,
    string? MotivoRespaldo);
