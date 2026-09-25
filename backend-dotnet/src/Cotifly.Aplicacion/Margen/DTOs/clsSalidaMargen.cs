namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Salida de <c>obtener_margen</c> (esquema <c>SalidaMargen</c> de <c>specs/openapi.yaml</c>:
/// <c>{margen, origen, trq_type, anomalo, detalle}</c>).
/// </summary>
/// <param name="Margen">Fracción decimal (0.135 = 13,5 %), 6 decimales como máximo.</param>
/// <param name="Origen">Código del origen: <c>sap_tm</c> | <c>politica_linea</c> | <c>negociado</c>.</param>
/// <param name="TrqType">TrqType de SAP TM de la línea, o <c>null</c> si no tiene mapeo.</param>
/// <param name="Anomalo">True si el margen cae fuera de [0, 60 %]: validar antes de aplicar.</param>
/// <param name="Detalle">Explicación legible del origen (nunca nulo).</param>
public sealed record clsSalidaMargen(decimal Margen, string Origen, string? TrqType, bool Anomalo, string Detalle);
