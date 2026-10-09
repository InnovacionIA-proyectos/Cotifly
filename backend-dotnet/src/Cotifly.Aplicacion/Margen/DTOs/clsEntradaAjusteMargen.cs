namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Margen manual que el comercial negocia sobre el sugerido (HU-003.4; herramienta <c>registrar_ajuste_margen</c>
/// y control editable de HU-004.4).
/// </summary>
/// <param name="MargenPolitica">Margen sugerido (el que resolvió <c>obtener_margen</c>).</param>
/// <param name="MargenAplicado">Margen que el comercial decide aplicar.</param>
/// <param name="UsuarioId">Comercial que negocia (sale del token o del usuario resuelto, nunca del LLM).</param>
/// <param name="MetodoCalculo"><c>margen</c> (exige m &lt; 1) o <c>markup</c> (admite m ≥ 1).</param>
/// <param name="Motivo">Motivo del ajuste; si no viene se registra un texto por defecto.</param>
/// <param name="Costo">Si viene, el precio se calcula con el margen negociado (no se recalcula la política).</param>
/// <param name="CotizacionId">Cotización a la que pertenece el ajuste (debe existir y ser del comercial).</param>
/// <param name="Folio">Folio de la cotización, si ya existe.</param>
/// <param name="TrqType">TrqType de la línea, solo informativo para la salida.</param>
/// <param name="ThreadId">Hilo de la conversación, solo para la auditoría (#11).</param>
/// <param name="LineaSolicitudId">
/// Ruta de la cotización a la que aplica el ajuste (HU-021.8, #135): se guarda en <c>MargenAjustes.lineaSolicitudId</c>. Sin ella, la
/// única ruta de la cotización; con varias, <c>RUTA_REQUERIDA</c>.
/// </param>
public sealed record clsEntradaAjusteMargen(
    decimal MargenPolitica,
    decimal MargenAplicado,
    Guid UsuarioId,
    string? MetodoCalculo = "margen",
    string? Motivo = null,
    decimal? Costo = null,
    Guid? CotizacionId = null,
    string? Folio = null,
    string? TrqType = null,
    string? ThreadId = null,
    Guid? LineaSolicitudId = null);
