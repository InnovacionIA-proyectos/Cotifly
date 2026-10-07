namespace Cotifly.Aplicacion.Pdf.DTOs;

/// <summary>
/// Entrada de <c>generar_pdf</c>. El comercial (<see cref="UsuarioId"/>), el folio y la trazabilidad los pone el orquestador
/// o el endpoint (desde el token y el estado de la conversación, ADR-004), nunca el LLM.
/// </summary>
/// <param name="UsuarioId">Comercial autenticado: dueño de la cotización y fuente de la firma (<c>UsuariosWeb</c>).</param>
/// <param name="Cotizacion">Payload del PDF (esquema de <c>ejemplo-solicitud.json</c>).</param>
/// <param name="Folio">
/// Folio ya generado por el servidor (#12, ADR-011); obligatorio. La <c>referencia</c> del payload no se usa (el LLM no
/// inventa folios). Si ya existe → <c>FOLIO_DUPLICADO</c>.
/// </param>
/// <param name="Version">Versión de la cotización (1 la original; una nueva versión la pasa con su folio <c>-vN</c>).</param>
/// <param name="Trazabilidad">Costo, márgenes, precio, origen del margen, cliente y moneda que se guardan en <c>Cotizaciones</c>.</param>
/// <param name="SpaceId">Espacio de la conversación de origen (<c>web/{sesion_id}</c>), o vacío si el PDF no sale de un chat.</param>
/// <param name="ThreadId">Hilo de la conversación (también identifica el evento de auditoría).</param>
public sealed record clsEntradaGenerarPdf(
    Guid UsuarioId,
    clsCotizacionPdfPayload Cotizacion,
    string Folio,
    int Version = 1,
    clsTrazabilidadCotizacion? Trazabilidad = null,
    string? SpaceId = null,
    string? ThreadId = null);

/// <summary>
/// Datos de trazabilidad de precio y margen (§7.3 del diseño) que se persisten con la cotización. En el Python los extraía
/// <c>_datos_trazabilidad</c> del payload del LLM; en .NET los entrega el estado de la conversación (resultados de
/// <c>obtener_margen</c>, <c>calcular_precio</c> y del ajuste negociado).
/// </summary>
/// <param name="OrigenMargen"><c>sap_tm</c> | <c>politica_linea</c> | <c>negociado</c>.</param>
/// <param name="MetodoCalculo"><c>margen</c> | <c>markup</c>; si no viene, el del payload o el de la política de la línea.</param>
/// <param name="OrigenCliente"><c>cliente</c> | <c>prospecto</c>.</param>
/// <param name="Moneda">ISO de 3 letras (por defecto <c>COP</c>); con otra moneda la TRM debe ser mayor que 0.</param>
/// <param name="PrecioCop">
/// Valor cotizado en pesos de una cotización en USD, antes de convertir (HU-012.2); no se imprime en el PDF. No tiene columna propia: es el
/// precio en pesos de <c>Costo</c> y <c>MargenAplicado</c> (persistidos), y se verifica contra ellos.
/// </param>
public sealed record clsTrazabilidadCotizacion(
    decimal? Costo = null,
    decimal? MargenPolitica = null,
    decimal? MargenAplicado = null,
    string? OrigenMargen = null,
    string? MetodoCalculo = null,
    decimal? PrecioFinal = null,
    string? NitCliente = null,
    string? OrigenCliente = null,
    string? Moneda = null,
    decimal TrmAplicada = 0m,
    string? CosteoId = null,
    decimal? PrecioCop = null);
