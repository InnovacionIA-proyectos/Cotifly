using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Costeo de una cotización del comercial en la consola (HU-005.7, ADR-015 §5): verlo (precios y, desde HU-039.1, #218, el costo bruto
/// de cada ruta; nunca el costo de cada camión o conexo),
/// elegir los servicios conexos que se incluyen y generar el PDF con el módulo de HU-003.5. Solo cotizaciones propias
/// (<c>COTIZACION_NO_ENCONTRADA</c> → 404). Las mutaciones exigen la versión de la fila (<c>If-Match</c>, ADR-018 §8) y siguen la
/// regla del PDF (ADR-018 §11, <see cref="clsReglaPdfCosteo"/>).
/// </summary>
public interface IClsCosteoCotizacionService
{
    Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quita conexos o cambia su cantidad (HU-005.7; por ruta desde HU-021.8, #135, decisión 3; ver <see cref="clsCambioServiciosConexos"/>).
    /// Quitar un conexo BORRA su fila de la ruta y queda auditado (<see cref="HerramientaConexos"/>); la ruta y la cabecera se recalculan
    /// con el margen de la ruta. Errores: <c>CONFLICTO_CONCURRENCIA</c> (409) si <paramref name="versionEsperada"/> no es la de la fila;
    /// <c>COSTEO_NO_LISTO</c> (409) sin precio; <c>PDF_YA_ENVIADO</c> (409) ya enviada o con resultado; <c>SOLICITUD_INVALIDA</c> (400,
    /// forma: las dos formas juntas, cantidad fuera de rango o id repetido); <c>RUTA_REQUERIDA</c> y <c>RUTA_INEXISTENTE</c> (400);
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (400) con un id que no es conexo del costeo (o de la ruta). Si algo cambia, el PDF vigente queda
    /// invalidado (ADR-018 §11) y sube la versión; sin cambios responde igual sin tocar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken = default);

    /// <summary>
    /// PDF de la cotización con el flete a <c>precioFinal</c> y solo los servicios conexos incluidos, cada uno con su precio.
    /// <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_GENERADO</c> (PDF vigente), <c>PDF_YA_ENVIADO</c> y <c>CONFLICTO_CONCURRENCIA</c> (409: si viene
    /// <paramref name="versionEsperada"/> y no es la vigente, o si la cotización cambió mientras se generaba, aunque no venga), y los
    /// errores del módulo PDF. Con el PDF desactualizado, el anterior se archiva y se genera uno nuevo en el mismo folio.
    /// </summary>
    Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, int? versionEsperada = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>CosteoCotizacion</c>: estado del costeo y precios (de los costos de Nexo, solo el bruto de cada ruta, HU-039.1; HU-021:
/// <see cref="PrecioFinal"/> es la suma del precio de las rutas, que ya incluye sus conexos), el margen sugerido y su origen (HU-013.2), si el PDF
/// quedó desactualizado (ADR-018 §11), la versión de la fila (<c>row_version</c>, también en el <c>ETag</c>), la TRM fijada al crear
/// (<see cref="TrmAplicada"/>, HU-013.7; en USD los precios ya vienen convertidos con esa TRM) y el asunto y la introducción guardados
/// para el PDF (<c>textos</c>, HU-013.5; <c>null</c> si todavía no hay).
/// </summary>
public sealed record clsCosteoCotizacion(
    string Folio,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    DateTimeOffset? CosteoListoAt,
    decimal? PrecioFinal,
    decimal? MargenAplicado,
    string Moneda,
    IReadOnlyList<clsServicioConexoPrecio> ServiciosConexos,
    string? PdfUrl,
    decimal? MargenSugerido,
    string? OrigenMargen,
    bool PdfDesactualizado,
    string? RowVersion,
    clsTextosCotizacion? Textos = null) : IClsConVersionFila
{
    /// <summary>HU-013.7: TRM fijada al crear la cotización (COP por USD) con que se convierten sus precios; 0 en COP.</summary>
    public decimal TrmAplicada { get; init; }

    /// <summary>
    /// HU-018.1 (#155): fecha límite de la solicitud (cabecera, la del formulario de costeo) en UTC; <c>null</c> si no se dio. Es de toda la
    /// solicitud, no de cada ruta, por eso va aquí y no en <see cref="clsRutaCosteo"/>.
    /// </summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>
    /// HU-018.1 (#155): <c>scorePrioridad</c> que Nexo devolvió al crear la solicitud (cabecera); <c>null</c> si Nexo no lo mandó, si la
    /// solicitud no se ha enviado o si se envió antes del script 09.
    /// </summary>
    public decimal? ScorePrioridad { get; init; }

    /// <summary>HU-033 (#199): resultado ganada/perdida (<c>ganada</c>, <c>perdida</c> o <c>null</c> si no tiene).</summary>
    public string? Resultado { get; init; }

    /// <summary>
    /// HU-033 (#199): estado del resultado frente a Nexo (<c>pendiente</c>, <c>sincronizado</c>, <c>no_sincronizable</c>, <c>solo_cotifly</c> o
    /// <c>null</c> sin resultado). Ver <c>CotizacionResumen.sincronizacion_resultado_nexo</c>.
    /// </summary>
    public string? SincronizacionResultadoNexo { get; init; }

    /// <summary>HU-033 (#199): último error al enviar el resultado pendiente, o por qué ya no se puede enviar.</summary>
    public string? ErrorSincronizacionResultadoNexo { get; init; }

    /// <summary>HU-036.2 (#206): cuándo se finalizó la cotización (UTC); <c>null</c> sin finalizar. Ver <c>CotizacionResumen.finalizada_at</c>.</summary>
    public DateTimeOffset? FinalizadaAt { get; init; }

    /// <summary>
    /// HU-045.2 (#264): la tasa VIGENTE del seguro de la mercancía (fracción: 0.003450 = 0,345 %; <c>TarifasSeguro</c>), para mostrarla antes
    /// de marcar el seguro en una ruta. La de cada ruta que ya lo tiene es <see cref="clsRutaCosteo.TasaSeguro"/> (no cambia con la
    /// vigente). En <c>GET .../costeo</c>, en <c>PUT .../costeo/seguro</c>, en las respuestas del margen y de los conexos y en el detalle por
    /// folio; <c>null</c> en las demás respuestas (PDF, textos, recálculo de vehículos) o si no se pudo leer.
    /// </summary>
    public decimal? TasaSeguroVigente { get; init; }

    /// <summary>
    /// HU-021.8 (#135): las rutas en orden, cada una con su margen (aplicado, sugerido y origen), su precio y sus conexos. El ajuste de
    /// margen y los cambios de conexos dicen a cuál aplican con <c>linea_orden</c> o <c>linea_solicitud_id</c>. HU-039.1 (#218): el único
    /// costo que trae es el costo bruto de cada ruta (<see cref="clsRutaCosteo.CostoFinal"/>), solo para el dueño.
    /// </summary>
    public IReadOnlyList<clsRutaCosteo> Rutas { get; init; } = [];

    /// <summary>
    /// HU-039.1 (#218): la misma vista sin el costo bruto de las rutas (<see cref="clsRutaCosteo.CostoFinal"/> en <c>null</c>), para quien
    /// la consulta sin ser el dueño (el gerente comercial en solo consulta del detalle).
    /// </summary>
    public clsCosteoCotizacion SinCostoFinal() => this with { Rutas = [.. Rutas.Select(r => r with { CostoFinal = null })] };
}

/// <summary>
/// Servicio conexo tal como lo ve el comercial: precio con el margen de su ruta y si va en el PDF (HU-021: siempre). <see cref="LineaOrden"/>
/// es la ruta a la que pertenece (HU-021.8).
/// </summary>
public sealed record clsServicioConexoPrecio(
    string Id, string? Nombre, string Categoria, decimal Cantidad, decimal Precio, bool Incluido, int? LineaOrden = null)
{
    /// <summary>
    /// Decisión 10 (HU-021.9): estado del envío diferido del conexo a la cotización de Nexo: <c>pendiente</c>, <c>enviado</c>,
    /// <c>incierto</c> (no se pudo confirmar: revisar Nexo; no se reintenta solo) o <c>fallido_definitivo</c>. En <c>GET .../costeo</c> y
    /// en los PUT de margen y de conexos (#173); <c>null</c> antes de enviar la solicitud a Nexo y en las demás mutaciones.
    /// </summary>
    public string? EstadoNexo { get; init; }

    /// <summary>
    /// HU-041.1 (#231): id del servicio en el CATÁLOGO de Nexo (<c>servicio_id</c>, el mismo de <c>GET /web/servicios-conexos</c>), para que
    /// el frontend reconozca el mismo conexo en varias rutas sin comparar por nombre. <see cref="Id"/> puede ser el de la fila de la
    /// cotización de Nexo (conexo agregado con el POST o importado); este es siempre el del catálogo. <c>null</c> en las filas anteriores a
    /// #188 y si Nexo no lo informó.
    /// </summary>
    public string? ServicioId { get; init; }
}

/// <summary>
/// <c>RutaCosteo</c> (HU-021.8, #135): una ruta del costeo como la ve el comercial. <see cref="MargenSugerido"/> es el de SAP TM o, si no
/// hay, el de la política de su línea (<c>margenSAPSugerido</c> o <c>margenPoliticaSugerido</c>): por debajo de él el ajuste de ESTA ruta
/// exige justificación. Hasta HU-039 no traía costos (HU-021.8, #135); desde HU-039.1 (#218) trae el costo bruto de la ruta
/// (<see cref="CostoFinal"/>), sin desglose ni costos por camión o por conexo.
/// </summary>
public sealed record clsRutaCosteo(
    int LineaOrden,
    Guid LineaSolicitudId,
    string? Origen,
    string? Destino,
    string? EstadoCosteo,
    decimal? PrecioFinalLinea,
    decimal? MargenAplicado,
    decimal? MargenSugerido,
    string? OrigenMargen,
    IReadOnlyList<clsServicioConexoPrecio> ServiciosConexos)
{
    /// <summary>
    /// HU-039.1 (#218): costo bruto de la ruta que costeó Nexo (<see cref="clsLineaSolicitud.CostoFinal"/>: fletes + retorno + levante +
    /// conexos de los camiones que cuentan, sin margen), <b>siempre en COP</b> aunque la cotización esté en USD (el costeo de Nexo es en
    /// pesos y nunca se convierte). <c>null</c> en los mismos casos que <see cref="PrecioFinalLinea"/> (sin costeo listo o ruta
    /// <c>rechazada</c>). Solo lo ve el dueño: el detalle del gerente en solo consulta lo deja en <c>null</c>
    /// (<see cref="clsCosteoCotizacion.SinCostoFinal"/>). Reversa de la regla de ADR-015 §5, confirmada por la Gerencia Comercial el
    /// 2026-10-27.
    /// </summary>
    public decimal? CostoFinal { get; init; }

    /// <summary>HU-021.9 (#136): línea de negocio de la ruta (id de <c>MargenesPolitica</c>; define su margen).</summary>
    public int? LineaNegocioId { get; init; }

    /// <summary>HU-045.2 (#264): ¿la ruta incluye el seguro de la mercancía? (el check del costeo, <c>PUT .../costeo/seguro</c>).</summary>
    public bool IncluyeSeguro { get; init; }

    /// <summary>
    /// HU-045.2 (#264): la tasa con que se calculó el seguro de la ruta (fracción: 0.003450 = 0,345 %), copiada de la vigente al marcarlo;
    /// un cambio de la tasa vigente no la toca. <c>null</c> sin seguro o sin valor de la mercancía.
    /// </summary>
    public decimal? TasaSeguro { get; init; }

    /// <summary>
    /// HU-045.2 (#264): el seguro de la mercancía de la ruta (<c>valor_mercancia × tasa_seguro</c> en pesos, sin redondear a pesos; en centavos), en la moneda de la
    /// cotización (en USD, convertido con <c>trm_aplicada</c>). Es venta, sin margen: ya está sumado en <see cref="PrecioFinalLinea"/>.
    /// <c>null</c> sin seguro o sin valor de la mercancía.
    /// </summary>
    public decimal? ValorSeguro { get; init; }

    /// <summary>HU-021.9: peso total que declaró el comercial para la ruta.</summary>
    public decimal? PesoKg { get; init; }

    /// <summary>
    /// HU-030.1 (#184): peso total de la ruta con la tara de los contenedores (solo una ruta <c>Contenedor</c> que la trae); <c>null</c> =
    /// a Nexo va el peso de carga. Cada camión trae su parte en <see cref="clsCamionCosteo.PesoConTaraKg"/>.
    /// </summary>
    public decimal? PesoConTaraKg { get; init; }

    /// <summary>
    /// HU-021.9: los camiones de la ruta en su orden estable (el primero vigente lleva los conexos en Nexo), sin costos ni ids de Nexo.
    /// Los <c>descartada</c> vienen con su estado: no cuentan en el precio.
    /// </summary>
    public IReadOnlyList<clsCamionCosteo> Camiones { get; init; } = [];

    /// <summary>HU-021.9: los camiones agrupados como los imprime el PDF ("2 × 3S3 - FURGON (34000 KG)"); <c>null</c> sin datos.</summary>
    public string? Vehiculos { get; init; }

    /// <summary>HU-021.9: las mercancías de la ruta, en su orden.</summary>
    public IReadOnlyList<clsMercanciaCosteo> Mercancias { get; init; } = [];

    /// <summary>HU-023.3 (#158): qué respondió el cubicador para la ruta (<c>ASIGNADO</c> o <c>SIN_SELECCION_AUTOMATICA</c>); <c>null</c> sin cubicador.</summary>
    public string? ResultadoCubicacion { get; init; }

    /// <summary>HU-023.3: por qué el cubicador no pudo escoger (solo con <c>SIN_SELECCION_AUTOMATICA</c>).</summary>
    public string? MotivoSinSeleccion { get; init; }

    /// <summary>HU-023.3: la ruta va a la Mesa de Costeo por una mercancía condicionada.</summary>
    public bool Condicionada { get; init; }

    /// <summary>
    /// HU-023 (#176): con <c>SIN_SELECCION_AUTOMATICA</c>, el texto para el comercial según el motivo (<c>BUSQUEDA_AGOTADA</c> no dice que
    /// la carga no cabe). Si la base no admite el motivo (sin el script <c>07</c>), el motivo real está en la advertencia
    /// <c>MOTIVO_SIN_SELECCION</c> de <see cref="AvisosCubicacion"/>. <c>null</c> en los demás casos.
    /// </summary>
    public string? MensajeSinSeleccion { get; init; }

    /// <summary>
    /// HU-023.3: avisos del cubicador de la llamada que se acaba de hacer (respuesta de <c>POST .../recalcular-vehiculos</c>): mercancías
    /// condicionadas, advertencias y extradimensión. No se guardan: en el resto de respuestas es <c>null</c> y no se serializa.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public clsAvisosCubicacion? AvisosCubicacion { get; init; }

    /// <summary>
    /// Decisión 10 (HU-021.9): aviso cuando algún conexo de la ruta quedó <c>incierto</c> o <c>fallido_definitivo</c> en su envío a Nexo
    /// (el comercial o la Mesa deben revisarlo en Nexo); <c>null</c> si no hay nada que revisar. Solo en <c>GET .../costeo</c>.
    /// </summary>
    public string? AvisoConexosNexo { get; init; }

    /// <summary>
    /// HU-009 (#139, B1): SAP TM propuso para la ruta un margen fuera de [0, 60 %]; no se aplicó y la ruta usa el de la política de su línea.
    /// </summary>
    public bool MargenSapAnomalo { get; init; }

    /// <summary>HU-009 (#139, B1): aviso al comercial cuando <see cref="MargenSapAnomalo"/>; <c>null</c> si no.</summary>
    public string? AvisoMargen { get; init; }

    /// <summary>
    /// HU-046.3 (#269): lo que exige la política de seguros a la ruta (tramo por el valor total de la mercancía, medidas recomendadas,
    /// obligatorias con su estado y los conexos que las cumplen, y un aviso sin valor de la mercancía). En <c>GET .../costeo</c> y en las
    /// respuestas de los conexos, del margen y del seguro; <c>null</c> en una ruta rechazada por la Mesa, en una cotización finalizada (no se
    /// recalcula, decisión 7), si la política no se pudo leer y en las demás respuestas.
    /// </summary>
    public clsPoliticaSegurosRuta? PoliticaSeguros { get; init; }

    /// <summary>
    /// HU-028.1 (#183, Nexo #320): por qué la Mesa de Costeo de Nexo rechazó camiones de la ruta (los motivos distintos de sus camiones
    /// <c>rechazada</c>, unidos con <c>" | "</c>); <c>null</c> si ninguno está rechazado. Con <see cref="EstadoCosteo"/> = <c>rechazada</c>
    /// (todos rechazados) la ruta no tiene precio, no suma en el total y responde 409 <c>RUTA_RECHAZADA</c> al margen y a los conexos; con
    /// algunos rechazados, su precio es el de los demás. Solo para el comercial: el PDF y el Excel no llevan el motivo.
    /// </summary>
    public string? MotivoRechazo { get; init; }
}

/// <summary>
/// <c>CamionCosteo</c> (HU-021.9, #136): un camión de la ruta como lo ve el comercial. Vehículo y carrocería vacíos = sin selección
/// automática (los resuelve la Mesa de Costeo de Nexo, decisión 7). Sin costos ni ids de Nexo. HU-023.3 (#158): con el cubicador, el
/// volumen que lleva y qué tan lleno va en volumen y en peso (0 a 100); <c>null</c> sin recomendación. D2: los porcentajes también son
/// <c>null</c> si la Mesa de Costeo cambió de verdad el vehículo o la carrocería (eran del vehículo anterior).
/// </summary>
public sealed record clsCamionCosteo(
    string? TipoVehiculo, string? Carroceria, decimal? PesoKg, string? EstadoCosteo,
    decimal? VolumenTotalM3 = null, decimal? PorcentajeLlenadoVolumen = null, decimal? PorcentajeLlenadoPeso = null)
{
    /// <summary>
    /// #181 (Nexo #309): estado de la línea del camión en Nexo, en <c>snake_case</c> (<c>por_costear</c>, <c>auto_resuelta</c>,
    /// <c>revisada</c>), según la última consulta del polling. Solo se muestra (no se guarda en la base): <c>null</c> si Nexo no lo manda,
    /// si el camión aún no está asociado a su línea o si el servicio se reinició y el polling no ha vuelto a leer.
    /// </summary>
    public string? EstadoLineaNexo { get; init; }

    /// <summary>
    /// #181 (Nexo #309): por qué la línea del camión sigue pendiente en Nexo (consulta completa, línea sin vehículo, SICE-TAC que no es del
    /// mes actual...), con el texto de Nexo. Solo con el camión en <c>pendiente_costeo</c>; <c>null</c> en los demás casos o sin el dato.
    /// </summary>
    public string? MotivoPendiente { get; init; }

    /// <summary>
    /// HU-030.1 (#184): el peso con tara que viajó (o viajará) a Nexo en la línea del camión: el de la ruta con un camión o, con varios, el
    /// peso de carga del camión más una parte igual de la tara; <c>null</c> si la ruta no lleva tara (entonces viaja <see cref="PesoKg"/>
    /// o el de la ruta).
    /// </summary>
    public decimal? PesoConTaraKg { get; init; }

    /// <summary>
    /// HU-028.1 (#183, Nexo #320): por qué la Mesa de Costeo de Nexo rechazó la línea del camión (<see cref="EstadoCosteo"/> =
    /// <c>rechazada</c>), con el texto de Nexo. Se guarda en el camión (script 10): sale aunque el servicio se reinicie. <c>null</c> si el
    /// camión no está rechazado o Nexo no mandó el motivo.
    /// </summary>
    public string? MotivoRechazo { get; init; }

    /// <summary>
    /// HU-028.1 (#183): cuándo vio Cotifly el rechazo por primera vez (UTC). Nexo no expone su fecha ni quién rechazó en
    /// <c>GET /api/solicitudes/{id}</c>. <c>null</c> si el camión no está rechazado.
    /// </summary>
    public DateTimeOffset? RechazadaAt { get; init; }
}

/// <summary>
/// <c>MercanciaCosteo</c> (HU-021.9, #136): una mercancía de la ruta (código del catálogo del cubicador, HU-023.3; nombre, medidas y peso).
/// <c>codigo_mercancia</c> es <c>null</c> en las mercancías anteriores al script <c>02</c>. HU-032 (#197): las medidas son <c>null</c> en una
/// mercancía no cubicable (sin medidas ni tipo de mercancía).
/// </summary>
public sealed record clsMercanciaCosteo(
    int Orden, string? CodigoMercancia, string? NombreMercancia, int Cantidad, decimal? LargoCm, decimal? AnchoCm, decimal? AltoCm, decimal? PesoKg);

/// <summary>PDF del costeo generado (<c>SalidaPdf</c>) y la versión nueva de la fila (va en el <c>ETag</c>, no en el cuerpo).</summary>
public sealed record clsPdfCosteoGenerado(clsSalidaPdf Pdf, string? RowVersion);

/// <summary>Implementación de <see cref="IClsCosteoCotizacionService"/>.</summary>
public sealed class clsCosteoCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsUsuarioWebRepository usuarios,
    IClsGeneradorPdf generador,
    IClsAlmacenPdf almacen,
    IClsUnitOfWork unidadTrabajo,
    IClsCalendarioNegocio calendario,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsCosteoCotizacionService> logger,
    IClsEnvioConexosNexoService? envioConexos = null,
    Cubicacion.Interfaces.IClsCubicador? cubicador = null,
    IClsCategoriasConexos? categoriasConexos = null,
    IClsSituacionLineasNexo? situacionesNexo = null,
    Margen.Services.IClsAvisoSincronizacionMargenSap? avisoSincronizacion = null,
    IClsTarifaSeguroRepository? tarifasSeguro = null,
    IClsPoliticaSegurosRepository? politicaSeguros = null) : IClsCosteoCotizacionService
{
    public const string HerramientaPdf = "generar_pdf_costeo";

    /// <summary>Nombres <c>{folio}-hN.pdf</c> que se prueban al archivar un PDF invalidado.</summary>
    private const int MaximoIntentosArchivo = 20;

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> al elegir conexos de una cotización ya entregada.</summary>
    public const string MotivoVersionConexos = "Cambio de servicios conexos después del envío";

    /// <summary>Validación HU-036 (#208): motivo de la versión nueva cuando el costeo de Nexo cambió después de finalizar.</summary>
    public const string MotivoVersionCosteoCambiado = "El costeo de Nexo cambió después de finalizar";

    public async Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        // HU-046.3 (#269): los conexos sin codigoServicio (anteriores al script 21 o importados de Nexo) se completan con el catálogo.
        await CompletarCodigosConexosAsync(cotizacion, cancellationToken);

        // Decisión 10 (HU-021.9): el estado del envío diferido de cada conexo a Nexo (incierto o fallido definitivo quedan a la vista).
        var estadosNexo = envioConexos is null ? null : await envioConexos.EstadosAsync(cotizacion, cancellationToken);
        // #181: el estado y el motivo pendiente de la línea de Nexo de cada camión, de la última lectura del polling (en memoria).
        var vista = Vista(cotizacion, opcionesPdf.Value.BaseUrl, estadosNexo, await CategoriasAsync(categoriasConexos, cotizacion, logger, cancellationToken),
            situacionesNexo?.Obtener(cotizacion.Id),
            await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, cancellationToken))
            with
            {
                // HU-045.2 (#264): la tasa vigente, para mostrarla antes de marcar el seguro.
                TasaSeguroVigente = await TasaSeguroVigenteSinFallarAsync(tarifasSeguro, logger, cotizacion.Folio, cancellationToken),
            };

        // HU-046.3 (#269): el bloque politica_seguros de cada ruta, con la política leída de sus tablas en esta petición.
        return clsPoliticaSegurosCosteo.ConPolitica(
            vista, cotizacion, await clsPoliticaSegurosCosteo.LeerSinFallarAsync(politicaSeguros, logger, cotizacion.Folio, cancellationToken));
    }

    /// <summary>
    /// HU-046.3 (#269): completa el <c>codigoServicio</c> de los conexos que no lo tienen con el índice del catálogo de Nexo y lo guarda.
    /// Nunca falla la lectura del costeo: si no se puede guardar (p. ej. otra petición cambió los conexos), se deja en el log y se completa
    /// en otra lectura. No sube la versión de negocio (no cambia nada que vea el comercial: solo el dato del catálogo).
    /// </summary>
    private async Task CompletarCodigosConexosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (await clsPoliticaSegurosCosteo.CompletarCodigosAsync(categoriasConexos, cotizacion, logger, cancellationToken) == 0)
        {
            return;
        }

        try
        {
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logCodigosNoGuardados(logger, cotizacion.Folio, ex);
        }
    }

    private static readonly Action<ILogger, string, Exception?> _logCodigosNoGuardados = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1737, nameof(_logCodigosNoGuardados)),
        "No se pudo guardar el codigoServicio de los conexos de {Folio}; se completa en otra lectura del costeo.");

    /// <summary>
    /// HU-045.2 (#264): la tasa vigente del seguro (<see cref="clsCosteoCotizacion.TasaSeguroVigente"/>). Nunca falla: sin repositorio o si la
    /// lectura lanza, <c>null</c> (y un aviso en el log); la vista del costeo sale igual.
    /// </summary>
    public static async Task<decimal?> TasaSeguroVigenteSinFallarAsync(
        IClsTarifaSeguroRepository? tarifasSeguro, ILogger logger, string folio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (tarifasSeguro is null)
        {
            return null;
        }

        try
        {
            return await tarifasSeguro.ObtenerTasaVigenteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logTasaSeguroNoDisponible(logger, folio, ex);
            return null;
        }
    }

    private static readonly Action<ILogger, string, Exception?> _logTasaSeguroNoDisponible = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1734, nameof(_logTasaSeguroNoDisponible)),
        "No se pudo leer la tasa vigente del seguro de la mercancía para el costeo de {Folio}; la respuesta sale sin tasa_seguro_vigente.");

    /// <summary>Herramienta de la auditoría de los cambios de conexos (<c>EventosHerramienta.herramienta</c>, HU-021.8).</summary>
    public const string HerramientaConexos = "cambiar_servicios_conexos";

    public async Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cambio);
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var (resultado, movimientos, pdfInvalidado) = await CambiarConexosAsync(cotizacion, usuarioId, cambio, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaConexos,
            new
            {
                folio = cotizacion.Folio,
                linea_orden = cambio.Ruta.LineaOrden,
                linea_solicitud_id = cambio.Ruta.LineaSolicitudId,
                incluidos = cambio.Incluidos,
                conexos = cambio.Conexos?.Select(c => new { id = c?.Id, cantidad = c?.Cantidad }).ToList(),
            },
            resultado.EsExitoso
                ? (object)new
                {
                    // Sin montos: qué conexo de qué ruta se quitó (fila borrada) o cambió de cantidad.
                    movimientos = movimientos.Select(m => new
                    {
                        linea_orden = m.Orden,
                        linea_solicitud_id = m.RutaId,
                        id = m.Movimiento.IdServicioConexo,
                        nombre = m.Movimiento.Nombre,
                        cantidad_anterior = m.Movimiento.CantidadAnterior,
                        cantidad_nueva = m.Movimiento.CantidadNueva,
                        quitado = m.Movimiento.Quitado,
                    }).ToList(),
                    pdf_invalidado = pdfInvalidado,
                }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, IReadOnlyList<(int Orden, Guid RutaId, clsMovimientoConexo Movimiento)> Movimientos, bool PdfInvalidado)> CambiarConexosAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        if (SinCosteo(cotizacion) is { } sinCosteo)
        {
            return (sinCosteo, [], false);
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersionConexos) is { } entregada)
        {
            return (entregada, [], false);
        }

        // HU-021 (ADR-019): los conexos son filas de CotizacionServiciosConexos en sus rutas; un conexo excluido se BORRA de la ruta (la tabla
        // no tiene "incluido": la fila es lo que va en el precio y en el PDF). Para volver a tenerlo se agrega de nuevo con el POST.
        var plan = PlanConexos(cotizacion, cambio);
        if (plan.EsFallo)
        {
            return (plan.Error, [], false);
        }

        var ahora = cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt;
        var movimientos = new List<(int, Guid, clsMovimientoConexo)>();
        foreach (var (ruta, cantidades) in plan.Valor)
        {
            var ajustados = cotizacion.AjustarConexosRuta(ruta.Orden, cantidades, ahora);
            if (ajustados.EsFallo)
            {
                return (ajustados.Error, [], false);
            }

            movimientos.AddRange(ajustados.Valor.Select(m => (ruta.Orden, ruta.Id, m)));
        }

        if (movimientos.Count == 0)
        {
            // La misma selección y las mismas cantidades no cambian nada (ni invalidan el PDF): idempotente.
            return (await VistaConEstadosAsync(cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, logger, cancellationToken, categoriasConexos, situacionesNexo?.Obtener(cotizacion.Id),
                await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, cancellationToken), tarifasSeguro,
                politicaSeguros), [], false);
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? (await VistaConEstadosAsync(cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, logger, CancellationToken.None, categoriasConexos, situacionesNexo?.Obtener(cotizacion.Id),
                await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, CancellationToken.None), tarifasSeguro,
                politicaSeguros), movimientos, pdfInvalidado)
            : (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
    }

    /// <summary>
    /// Qué conexos deben quedar en cada ruta afectada (id → cantidad), sin tocar la cotización: valida la forma, la ruta y que cada id
    /// sea un conexo de la ruta (o del costeo, con <c>incluidos</c> sin ruta).
    /// </summary>
    public static clsResultado<IReadOnlyList<(clsLineaSolicitud Ruta, IReadOnlyDictionary<string, decimal> Cantidades)>> PlanConexos(
        clsCotizacionSolicitud cotizacion, clsCambioServiciosConexos cambio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cambio);
        if (cambio.Conexos is not null && cambio.Incluidos is not null)
        {
            return Invalida("Envía 'conexos' (la ruta con sus cantidades) o 'incluidos' (los que se conservan), no los dos.");
        }

        if (cambio.Conexos is null && cambio.Incluidos is null)
        {
            // Sin ninguna de las dos formas no se adivina: un cuerpo como {"linea_orden": 2} no borra los conexos de la ruta.
            return Invalida("Envía 'conexos' (la ruta con sus cantidades) o 'incluidos' (los que se conservan; [] los quita todos).");
        }

        var indicada = cambio.Ruta.LineaOrden is not null || cambio.Ruta.LineaSolicitudId is not null;
        if (cambio.Conexos is null && !indicada)
        {
            // HU-005.7: 'incluidos' sin ruta conserva esos ids en TODAS las rutas.
            var incluidos = cambio.Incluidos!;
            var todos = cotizacion.Rutas.SelectMany(r => r.Conexos).ToList();
            if (Ajenos(incluidos, todos) is { Count: > 0 } desconocidos)
            {
                return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                    $"Estos servicios conexos no son del costeo de la cotización {cotizacion.Folio}: {string.Join(", ", desconocidos)}.");
            }

            return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito(
                [.. cotizacion.RutasOrdenadas.Select(r => (r, Conservar(r, incluidos)))]);
        }

        var resuelta = cotizacion.ResolverRuta(cambio.Ruta.LineaOrden, cambio.Ruta.LineaSolicitudId);
        if (resuelta.EsFallo)
        {
            return resuelta.Error;
        }

        var ruta = resuelta.Valor;
        // HU-028.1 (#183): una ruta rechazada por la Mesa de Costeo no admite ajustar sus conexos (409 RUTA_RECHAZADA).
        if (cotizacion.ValidarRutaNoRechazada(ruta) is { } rechazada)
        {
            return rechazada;
        }

        if (cambio.Conexos is null)
        {
            var incluidos = cambio.Incluidos ?? [];
            if (Ajenos(incluidos, ruta.Conexos) is { Count: > 0 } ajenos)
            {
                return NoDeLaRuta(cotizacion, ruta, ajenos);
            }

            return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito([(ruta, Conservar(ruta, incluidos))]);
        }

        var cantidades = new Dictionary<string, decimal>(ComparadorIdConexo);
        for (var i = 0; i < cambio.Conexos.Count; i++)
        {
            var item = cambio.Conexos[i];
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
            {
                return Invalida($"conexos[{i}].id es obligatorio.");
            }

            if (item.Cantidad is not { } cantidad || cantidad < clsServiciosConexosCosteoService.CantidadMinima
                || cantidad > clsServiciosConexosCosteoService.CantidadMaxima || decimal.Round(cantidad, 2) != cantidad)
            {
                return Invalida($"conexos[{i}].cantidad debe estar entre {clsServiciosConexosCosteoService.CantidadMinima.ToString(System.Globalization.CultureInfo.InvariantCulture)} y {clsServiciosConexosCosteoService.CantidadMaxima.ToString(System.Globalization.CultureInfo.InvariantCulture)}, con hasta 2 decimales.");
            }

            if (!cantidades.TryAdd(item.Id.Trim(), cantidad))
            {
                return Invalida($"conexos[{i}].id está repetido.");
            }
        }

        if (Ajenos(cantidades.Keys, ruta.Conexos) is { Count: > 0 } noEstan)
        {
            return NoDeLaRuta(cotizacion, ruta, noEstan);
        }

        return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito([(ruta, cantidades)]);
    }

    /// <summary>
    /// Un solo criterio para comparar ids de conexos en la validación y en la aplicación: <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// (el mismo del dominio, <c>clsLineaSolicitud.AjustarConexos</c>). Así un id que pasa la validación es el mismo que se conserva.
    /// </summary>
    public static readonly StringComparer ComparadorIdConexo = StringComparer.OrdinalIgnoreCase;

    private static List<string> Ajenos(IEnumerable<string> ids, IEnumerable<clsServicioConexoRuta> conexos)
    {
        var propios = conexos.Select(c => c.IdServicioConexo).ToHashSet(ComparadorIdConexo);
        return [.. ids.Where(id => !propios.Contains(id))];
    }

    /// <summary>Los conexos de la ruta que están en <paramref name="incluidos"/>, con su cantidad actual (los demás se quitan).</summary>
    private static IReadOnlyDictionary<string, decimal> Conservar(clsLineaSolicitud ruta, IReadOnlyCollection<string> incluidos)
    {
        var conservar = incluidos.ToHashSet(ComparadorIdConexo);
        return ruta.Conexos.Where(c => conservar.Contains(c.IdServicioConexo))
            .ToDictionary(c => c.IdServicioConexo, c => c.Cantidad, ComparadorIdConexo);
    }

    private static clsErrorDominio NoDeLaRuta(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IEnumerable<string> ids) =>
        new(clsCodigosError.SolicitudCosteoInvalida,
            $"Estos servicios conexos no son de la ruta {ruta.Orden} de la cotización {cotizacion.Folio}: {string.Join(", ", ids)}. Para agregar uno nuevo usa el POST.");

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);

    public async Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, int? versionEsperada = null, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var resultado = await GenerarAsync(cotizacion, usuarioId, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaPdf,
            new { folio = cotizacion.Folio },
            resultado.EsExitoso ? (object)resultado.Valor.Pdf : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<clsResultado<clsPdfCosteoGenerado>> GenerarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int? versionEsperada, CancellationToken cancellationToken)
    {
        // ADR-018 §8: aunque el cliente no envíe If-Match, el PDF se confirma solo sobre la versión que se cargó (no queda vigente
        // un PDF con un precio que otro flujo cambió mientras se generaba).
        var versionCargada = cotizacion.VersionNegocio;
        if (versionEsperada is { } esperada && !cotizacion.TieneVersion(esperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        if (Bloqueo(cotizacion) is { } bloqueo)
        {
            return bloqueo;
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario is null)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "No se encontró el comercial autenticado en UsuariosWeb.");
        }

        // HU-021.9 (#136): la línea de negocio es de cada ruta. El título y el asunto llevan la de la solicitud si todas sus rutas tienen
        // la misma; en el pliego, cada sección lleva la de su ruta.
        var nombres = await clsLineasNegocioRutas.NombresAsync(cotizacion, politicas, cancellationToken);
        var lineaNegocio = clsLineasNegocioRutas.Comun(cotizacion, nombres);
        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;

        // Todas las rutas con precio y el total = la suma de sus precios (con los conexos), en una ruta y en el pliego.
        if (ValidarTotal(cotizacion, moneda) is { } noListo)
        {
            return noListo;
        }

        // HU-021.9: el layout de pliego es para una solicitud de más de una ruta (formulario o licitación); con una ruta, el PDF de la ruta.
        var esPliego = EsPliego(cotizacion);
        // HU-023.1: el PDF muestra el nombre del vehículo y de la carrocería del catálogo del cubicador (Cotifly guarda solo el código).
        var catalogo = await NombresCubicadorAsync(cotizacion, cancellationToken);
        var payload = esPliego
            ? PayloadPliego(cotizacion, lineaNegocio, moneda, nombres, catalogo)
            : Payload(cotizacion, lineaNegocio, moneda, catalogo);
        AplicarTextos(payload, cotizacion);
        var invalido = esPliego
            ? clsValidadorCotizacionPdfPayload.Validar(payload, MaximoLineasPliego, MaximoLineasPliego)
            : clsValidadorCotizacionPdfPayload.Validar(payload);
        if (invalido is not null)
        {
            return invalido;
        }

        // En el pliego cada fila tiene su propio precio: aquí solo la moneda. En una ruta, la tarifa impresa es el precio FINAL de la ruta
        // (HU-033, #199, decisión 5: transporte y conexos incluidos, sin desglose), que debe ser exactamente su precioFinalLinea. Sin costo ni
        // margen en la trazabilidad: el PDF ya no los refleja (no hay nada que recalcular contra ellos).
        var primera = cotizacion.PrimeraRuta;
        var precioRuta = esPliego || primera is null ? null : PrecioRutaPdf(primera);
        var traza = new clsTrazabilidadCotizacion(
            null, primera?.MargenPoliticaSugerido, null, null, primera?.MetodoCalculo,
            precioRuta, cotizacion.NitCliente, null, moneda, cotizacion.TrmAplicada, cotizacion.SolicitudNexoId);
        if (clsGenerarPdfService.ValidarCoherencia(payload, traza, primera?.MetodoCalculo ?? "margen", moneda) is { } incoherente)
        {
            return incoherente;
        }

        var firma = new clsFirmaDocumento(usuario.NombreCompleto ?? usuario.NombreUsuario ?? string.Empty, null, usuario.Email, clsTextoSeguro.LimpiarLinea(usuario.Telefono));
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, firma, calendario.Hoy, moneda, cotizacion.TrmAplicada);
        if (documento.EsFallo)
        {
            return documento.Error;
        }

        // HU-033 (#199): al final de la tarifa, el total general (la suma del precio final de las rutas) con la leyenda de impuestos.
        var conTotal = documento.Valor with { TotalGeneral = TotalGeneral(cotizacion, moneda) };
        var aRenderizar = esPliego ? DocumentoPliego(conTotal, cotizacion, moneda, catalogo) : conTotal;

        clsPdfGenerado pdf;
        try
        {
            pdf = await generador.GenerarAsync(aRenderizar, cancellationToken);
        }
        catch (clsRenderPdfOcupadoException)
        {
            // El turno de render no llegó a tiempo (#92/#95): ya quedó en el log del generador; es transitorio (503).
            return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "El generador de PDF está ocupado. Reintenta en unos segundos.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo renderizar el PDF del costeo del folio {Folio}.", cotizacion.Folio);
            return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "No se pudo generar el PDF de la cotización. Reintenta más tarde o contacta a soporte.");
        }

        var nombre = cotizacion.Folio + ".pdf";
        if (cotizacion.PdfDesactualizado)
        {
            // ADR-018 §11: el PDF invalidado ocupa el nombre del folio; se archiva (sin borrarlo) para generar el nuevo con ese nombre.
            await ArchivarPdfAnteriorAsync(cotizacion, nombre, cancellationToken);
        }

        var ruta = await almacen.GuardarNuevoAsync(nombre, pdf.Contenido, cancellationToken);
        if (ruta is null)
        {
            return new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"Ya existe el PDF {nombre} en el almacén: genera una versión nueva.");
        }

        cotizacion.AsignarPdf(ruta);
        cotizacion.IncrementarVersionNegocio();
        bool guardado;
        try
        {
            guardado = await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionCargada, CancellationToken.None);
        }
        catch
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            throw;
        }

        if (!guardado)
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        return new clsPdfCosteoGenerado(
            new clsSalidaPdf(cotizacion.Id, cotizacion.Folio, clsUrlPdf.Construir(opcionesPdf.Value.BaseUrl, nombre), RegistradaEnBd: true),
            VersionEtag(cotizacion));
    }

    /// <summary>
    /// Archiva el PDF invalidado que ocupa <paramref name="nombre"/>: lo copia como <c>{folio}-hN.pdf</c>, actualiza su referencia en el
    /// historial (lo guarda de inmediato, antes del PDF nuevo) y libera el nombre. Si el archivo ya no está, no hay nada que archivar.
    /// </summary>
    private async Task ArchivarPdfAnteriorAsync(clsCotizacionSolicitud cotizacion, string nombre, CancellationToken cancellationToken)
    {
        var historial = await cotizaciones.ListarHistorialPdfAsync(cotizacion.Id, cancellationToken);
        var ocupante = historial.LastOrDefault(h => string.Equals(clsUrlPdf.NombreArchivo(h.PdfPath), nombre, StringComparison.Ordinal));
        var contenido = ocupante is null ? null : await almacen.LeerPorReferenciaAsync(ocupante.PdfPath, cancellationToken);
        if (ocupante is null || contenido is null)
        {
            return;
        }

        string? archivado = null;
        for (var n = historial.Count; archivado is null && n < historial.Count + MaximoIntentosArchivo; n++)
        {
            archivado = await almacen.GuardarNuevoAsync($"{cotizacion.Folio}-h{n}.pdf", contenido, cancellationToken);
        }

        if (archivado is null)
        {
            return; // sin nombre libre: el PDF nuevo responderá PDF_YA_GENERADO y el anterior queda intacto.
        }

        ocupante.Archivar(archivado);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        await almacen.EliminarAsync(nombre, cancellationToken);
    }

    /// <summary>Versión de negocio como texto (el valor opaco del <c>ETag</c> y de <c>row_version</c>).</summary>
    public static string VersionEtag(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.VersionNegocio.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Asunto e introducción que imprime el PDF de la cotización sin textos de la IA ni del comercial: los de tarifa única (con la
    /// línea) o, en un pliego (<see cref="EsPliego"/>), el asunto de licitación y la introducción de <c>MultiSeccion</c>. Son el
    /// respaldo del redactor (HU-013.5): lo que ve el comercial es exactamente lo que sale en el PDF.
    /// </summary>
    public static clsTextosBase TextosEstandar(clsCotizacionSolicitud cotizacion, string? lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var esPliego = EsPliego(cotizacion);
        return new clsTextosBase(
            esPliego ? clsTextosEstandar.AsuntoVariasRutas(EsLicitacion(cotizacion), cotizacion.Rutas.Count) : clsTextosEstandar.AsuntoEstandar(lineaNegocio),
            clsTextosEstandar.IntroduccionEstandar(esPliego));
    }

    /// <summary>
    /// HU-013.5 (ADR-018 §2a): el asunto y la introducción guardados (de la IA ya validados, o del comercial) reemplazan los del PDF.
    /// Sin textos, o con los estándar sin editar, el PDF usa los suyos (los de <see cref="clsTextosEstandar"/> de su variante). Nunca
    /// llama a la IA: el PDF siempre sale. Las condiciones, las notas legales y la nota de TRM no cambian.
    /// </summary>
    public static void AplicarTextos(clsCotizacionPdfPayload payload, clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion) is not { } textos || !textos.ReemplazanLosDelPdf())
        {
            return;
        }

        payload.Asunto = textos.Asunto;
        payload.Textos ??= new clsTextosPayload();
        payload.Textos.Intro = [.. textos.Parrafos()];
    }

    /// <summary>
    /// HU-033 (#199, decisión 5): precio que imprime el PDF para una ruta: su precio FINAL (<c>precioFinalLinea</c>: transporte y servicios
    /// conexos con su margen), un solo valor sin desglose; <c>null</c> si la Mesa de Costeo la rechazó (sale "Sin costeo").
    /// </summary>
    public static decimal? PrecioRutaPdf(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.Rechazada ? null : ruta.PrecioFinalLinea;
    }

    /// <summary>
    /// HU-033 (#199): total general del PDF (la suma del precio final de las rutas que no rechazó la Mesa, igual a <c>precioFinal</c> por
    /// <see cref="ValidarTotal"/>) con la moneda y la leyenda de impuestos.
    /// </summary>
    public static clsTotalDocumento TotalGeneral(clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentException.ThrowIfNullOrWhiteSpace(moneda);
        var total = cotizacion.RutasCotizables.Sum(r => r.PrecioFinalLinea ?? 0m);
        return new clsTotalDocumento(
            clsTextosEstandar.EtiquetaTotalGeneral,
            $"{clsFormatoMoneda.Formatear(total, moneda)} {moneda.Trim().ToUpperInvariant()}",
            clsTextosEstandar.NotaTotalGeneral + " " + clsTextosEstandar.LeyendaImpuestos);
    }

    /// <summary>
    /// HU-033 (#199): los conexos de las rutas para el PDF: solo su nombre (van en "Estas tarifas incluyen"), sin precio; el precio de la
    /// ruta ya los incluye. En el pliego, con el número de su ruta.
    /// </summary>
    private static List<clsServicioConexoPayload>? ConexosSinPrecio(clsCotizacionSolicitud cotizacion, IEnumerable<clsLineaSolicitud> rutas, bool conRuta)
    {
        var conexos = rutas
            .SelectMany(r => ConexosConPrecio(cotizacion, r).Select(c => conRuta ? $"{c.Nombre ?? c.Categoria} (ruta {r.Orden})" : c.Nombre ?? c.Categoria))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(d => new clsServicioConexoPayload { Descripcion = d })
            .ToList();
        return conexos.Count == 0 ? null : conexos;
    }

    /// <summary>
    /// Payload del PDF de una ruta armado desde la cotización (nada del LLM ni del cliente): una fila con el origen, el destino, los
    /// camiones y el precio FINAL de la ruta (HU-033: transporte y conexos incluidos, un solo valor); los servicios conexos de la ruta se
    /// listan en "Estas tarifas incluyen" sin precio.
    /// </summary>
    public static clsCotizacionPdfPayload Payload(
        clsCotizacionSolicitud cotizacion, string? lineaNegocio, string moneda, clsNombresCatalogoCubicador? catalogo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ruta = cotizacion.PrimeraRuta;
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio,
            MetodoCalculo = ruta?.MetodoCalculo,
            Tarifa = new clsTarifaPayload
            {
                Titulo = lineaNegocio is null ? "COTIZACIÓN DE TRANSPORTE" : $"COTIZACIÓN DE TRANSPORTE {lineaNegocio.ToUpperInvariant()}",
                Origen = ruta?.OrigenNombre,
                Filas =
                [
                    new clsFilaTarifaPayload
                    {
                        Destino = ruta?.DestinoNombre,
                        VehiculoPeso = ruta is null ? null : Camiones(ruta, catalogo),
                        Tarifa = ruta is null ? null : PrecioRutaPdf(ruta),
                        Moneda = moneda,
                    },
                ],
            },
            ServiciosConexos = ruta is null ? null : ConexosSinPrecio(cotizacion, [ruta], conRuta: false),
            // #173: la nota general usa las horas de cargue y descargue capturadas en la ruta (antes, 4 h fijas).
            HorasCargue = ruta?.HorasCargue,
            HorasDescargue = ruta?.HorasDescargue,
        };
    }

    /// <summary>
    /// Payload del PDF de un pliego (HU-011.5; por ruta desde HU-021.9, #136): una sección por ruta, en su orden, con su título (y la
    /// línea de negocio de la ruta si se conoce: <paramref name="lineasPorId"/>, si no <paramref name="lineaNegocio"/>), su origen y una
    /// fila con el destino, sus camiones agrupados y el precio FINAL de la ruta (HU-033, #199: transporte y conexos incluidos, sin
    /// desglose); los conexos se listan en "Estas tarifas incluyen" sin precio, con su ruta. Nada del LLM ni del cliente.
    /// </summary>
    public static clsCotizacionPdfPayload PayloadPliego(
        clsCotizacionSolicitud cotizacion, string? lineaNegocio, string moneda, IReadOnlyDictionary<int, string>? lineasPorId = null,
        clsNombresCatalogoCubicador? catalogo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        // OBS-C-3 (endurecimiento B de #199): "PLIEGO" solo en una licitación; una spot de varias rutas dice "TARIFA DE TRANSPORTE".
        var tituloSeccion = EsLicitacion(cotizacion) ? "PLIEGO DE TRANSPORTE" : "TARIFA DE TRANSPORTE";
        var secciones = rutas
            .Select(r =>
            {
                var linea = (lineasPorId is null ? null : clsLineasNegocioRutas.DeRuta(r, lineasPorId)) ?? lineaNegocio;
                return new clsTarifaPayload
                {
                    Titulo = $"RUTA {r.Orden}: " + (linea is null ? tituloSeccion : $"{tituloSeccion} {linea.ToUpperInvariant()}"),
                    Origen = r.OrigenNombre,
                    Filas =
                    [
                        new clsFilaTarifaPayload
                        {
                            Destino = r.DestinoNombre,
                            VehiculoPeso = Camiones(r, catalogo),
                            // HU-028.1 (#183): una ruta rechazada por la Mesa sale "Sin costeo", sin precio y sin motivo.
                            Tarifa = PrecioRutaPdf(r),
                            Moneda = moneda,
                            Nota = r.Rechazada ? TextoSinCosteo : null,
                        },
                    ],
                };
            })
            .ToList();
        // Cabecera del pliego: la línea común, o "Varias" si las rutas tienen líneas distintas (cada sección lleva la de su ruta).
        var lineasDeRutas = lineasPorId is null
            ? []
            : rutas.Select(r => clsLineasNegocioRutas.DeRuta(r, lineasPorId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio ?? (lineasDeRutas.Count > 1 ? Cotizaciones.DTOs.clsCotizacionConLinea.Varias : lineasDeRutas.FirstOrDefault()),
            MetodoCalculo = cotizacion.PrimeraRuta?.MetodoCalculo,
            Asunto = clsTextosEstandar.AsuntoVariasRutas(EsLicitacion(cotizacion), rutas.Count),
            Secciones = secciones,
            ServiciosConexos = ConexosSinPrecio(cotizacion, rutas, conRuta: true),
        };
    }

    /// <summary>
    /// Resumen del pliego: una fila por ruta, en orden, con su precio FINAL (HU-033, #199: transporte y servicios conexos incluidos, sin
    /// desglose), y el total general (la suma de esos precios, igual a <c>PrecioFinal</c>). HU-028.1 (#183): una ruta rechazada por la Mesa
    /// de Costeo sale <see cref="TextoSinCosteo"/> (sin precio y sin motivo) y no suma en el total.
    /// </summary>
    public static clsResumenPliegoDocumento ResumenPliego(clsCotizacionSolicitud cotizacion, string moneda, clsNombresCatalogoCubicador? catalogo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        var tarifas = rutas.Select(PrecioRutaPdf).ToList();
        var filas = rutas.Select((r, i) => new clsFilaPliegoDocumento(
            r.Orden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clsTextoSeguro.LimpiarLinea($"{r.OrigenNombre} → {r.DestinoNombre}") ?? string.Empty,
            Camiones(r, catalogo) ?? "—",
            r.Rechazada ? TextoSinCosteo : tarifas[i] is { } precio ? clsFormatoMoneda.Formatear(precio, moneda) : "—")).ToList();
        return new clsResumenPliegoDocumento(
            EsLicitacion(cotizacion) ? clsTextosEstandar.TituloResumenPliego : clsTextosEstandar.TituloResumenRutas,
            filas,
            $"{clsTextosEstandar.EtiquetaTotalGeneral} ({filas.Count} RUTAS)",
            clsFormatoMoneda.Formatear(tarifas.Sum(t => t ?? 0m), moneda),
            "Valor por viaje de cada ruta con sus servicios conexos incluidos; el total es la suma de un viaje por ruta.");
    }

    /// <summary>
    /// HU-028.1 (#183): texto de una ruta rechazada por la Mesa de Costeo de Nexo en el PDF (resumen y sección del pliego) y en el Excel:
    /// sin precio y sin motivo (el motivo solo lo ve el comercial en Cotifly; decisión de Nexo #320).
    /// </summary>
    public const string TextoSinCosteo = "Sin costeo";

    /// <summary>Filas que admite el PDF de pliego: las mismas 1000 líneas de una licitación.</summary>
    public const int MaximoLineasPliego = Licitaciones.Validators.clsValidadorLicitacion.MaximoLineas;

    /// <summary>
    /// Por encima de estas rutas el pliego sale COMPACTO: solo el resumen (que ya trae todas las rutas con su tarifa), sin la sección de
    /// cada ruta, que repetiría las mismas filas en decenas de páginas (el máximo de tablas de un PDF de tarifas).
    /// </summary>
    public const int UmbralRutasCompacto = clsValidadorCotizacionPdfPayload.MaxSecciones;

    /// <summary>¿El PDF sale con el layout de pliego? HU-021.9 (#136): cuando la solicitud tiene más de una ruta (formulario o licitación).</summary>
    public static bool EsPliego(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > 1;
    }

    /// <summary>
    /// OBS-C-3 (E2E 2026-10-22, endurecimiento B de #199): ¿la solicitud es una licitación? Los textos del PDF de varias rutas dependen del
    /// tipo: solo una licitación dice "para su licitación" y "RESUMEN DEL PLIEGO"; una spot de varias rutas usa el mismo layout con textos
    /// neutros.
    /// </summary>
    public static bool EsLicitacion(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.TipoSolicitud == TipoSolicitud.Licitacion;
    }

    /// <summary>¿Las rutas del pliego pasan del umbral del layout completo (una sección por ruta)?</summary>
    public static bool EsPliegoCompacto(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > UmbralRutasCompacto;
    }

    /// <summary>
    /// Condiciones del PDF, de una ruta o de pliego (409 <c>COSTEO_NO_LISTO</c> si no se cumplen; HU-021.9): la solicitud tiene rutas,
    /// TODAS las que no rechazó la Mesa (HU-028.1, #183; sin ninguna, <c>SIN_RUTAS_COTIZABLES</c>) tienen precio (el ajuste de margen es por ruta, pero el PDF exige todas) y el total de la solicitud es la suma de esos precios
    /// (<c>precioFinal</c> = Σ <c>precioFinalLinea</c>, con los conexos incluidos: nada se inventa en el PDF). En USD (ADR-014 §5) los
    /// precios ya vienen convertidos con la <c>trmAplicada</c>.
    /// </summary>
    public static clsErrorDominio? ValidarTotal(clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (string.IsNullOrWhiteSpace(moneda))
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, $"La cotización {cotizacion.Folio} no trae la moneda.");
        }

        if (cotizacion.Rutas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} no tiene rutas.");
        }

        // HU-028.1 (#183): las rutas rechazadas por la Mesa no cuentan en "todas con precio" ni en el total (salen "Sin costeo").
        var cotizables = cotizacion.RutasCotizables;
        if (cotizables.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.SinRutasCotizables,
                $"La Mesa de Costeo de Nexo rechazó todas las rutas de la cotización {cotizacion.Folio}: no hay nada que cotizar y no se genera el PDF.");
        }

        if (cotizables.FirstOrDefault(r => r.PrecioFinalLinea is null) is { } sinPrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La ruta {sinPrecio.Orden} de la cotización {cotizacion.Folio} todavía no tiene precio.");
        }

        var suma = cotizables.Sum(r => r.PrecioFinalLinea!.Value);
        if (suma != cotizacion.PrecioFinal && cotizacion.Finalizada)
        {
            // Validación HU-036 (#208): con el precio finalizado congelado la Mesa rechazó una ruta que cotizaba: ninguna lectura del
            // costeo va a cuadrar el total, así que no se dice "espera": el PDF sale en una versión nueva con el costeo actual.
            return clsReglaPdfCosteo.ConVersionNueva(
                new clsErrorDominio(clsCodigosError.CosteoNoListo,
                    $"El costeo de Nexo cambió después de finalizar la cotización {cotizacion.Folio} (la Mesa de Costeo rechazó una ruta): el "
                    + "precio finalizado ya no es la suma de sus rutas. Crea una versión nueva para cotizar con el costeo actual."),
                cotizacion.Folio, MotivoVersionCosteoCambiado);
        }

        return suma == cotizacion.PrecioFinal
            ? null
            : new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"El total de la cotización {cotizacion.Folio} ({cotizacion.PrecioFinal}) todavía no es la suma de sus rutas ({suma}): espera la siguiente consulta del costeo.");
    }

    /// <summary>
    /// Documento del pliego: la variante <see cref="VarianteDocumento.Pliego"/> con el resumen y, en modo compacto
    /// (<see cref="EsPliegoCompacto"/>), sin las secciones de cada ruta.
    /// </summary>
    public static clsDocumentoCotizacion DocumentoPliego(
        clsDocumentoCotizacion documento, clsCotizacionSolicitud cotizacion, string moneda, clsNombresCatalogoCubicador? catalogo = null)
    {
        ArgumentNullException.ThrowIfNull(documento);
        var compacto = EsPliegoCompacto(cotizacion);
        var resumen = ResumenPliego(cotizacion, moneda, catalogo);
        return documento with
        {
            Variante = VarianteDocumento.Pliego,
            Pliego = compacto
                ? resumen with { Nota = resumen.Nota + $" Con más de {UmbralRutasCompacto} rutas el PDF solo trae este resumen." }
                : resumen,
            Secciones = compacto ? [] : documento.Secciones,
        };
    }

    /// <summary>
    /// Camiones de la ruta para el PDF (HU-021.9): los que cuentan (un camión descartado en Nexo no se imprime), agrupando los iguales. Con
    /// uno, "vehículo - carrocería - peso KG"; con varios, cada vehículo con cuántos van y el peso total de la ruta
    /// ("2 × 3S3 - FURGON, 1 × Vehículo por definir (34000 KG)"). <c>null</c> si no hay nada que mostrar.
    /// </summary>
    public static string? Camiones(clsLineaSolicitud ruta, clsNombresCatalogoCubicador? catalogo = null)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        // HU-028.1 (#183): los camiones rechazados no se cotizan y no se imprimen; en una ruta toda rechazada ("Sin costeo") se muestran
        // los que se pidieron, para que el cliente sepa qué ruta no se cotizó.
        var camiones = clsVehiculoLineaSolicitud.EnOrden(ruta.Rechazada ? ruta.VehiculosVigentes : ruta.VehiculosQueCuentan).ToList();
        if (camiones.Count <= 1)
        {
            var unico = camiones.Count == 1 ? camiones[0] : null;
            return VehiculoPeso(catalogo.Vehiculo(unico?.TipoVehiculo), catalogo.Carroceria(unico?.Carroceria), unico?.PesoKg ?? ruta.PesoKg);
        }

        var grupos = camiones
            .GroupBy(c => VehiculoPeso(catalogo.Vehiculo(c.TipoVehiculo), catalogo.Carroceria(c.Carroceria), null) ?? VehiculoPorDefinir,
                StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} × {g.Key}");
        var pesoCamiones = camiones.All(c => c.PesoKg is not null) ? camiones.Sum(c => c.PesoKg!.Value) : (decimal?)null;
        var peso = (pesoCamiones ?? ruta.PesoKg) is { } total ? $" ({total.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG)" : string.Empty;
        return string.Join(", ", grupos) + peso;
    }

    /// <summary>
    /// HU-023.1 (#156): nombres de los vehículos y carrocerías del catálogo del cubicador para el PDF, o <c>null</c> si ningún camión tiene
    /// código o el cubicador no está configurado. Un catálogo que no responde se omite: el PDF usa el código como respaldo.
    /// </summary>
    private async Task<clsNombresCatalogoCubicador?> NombresCubicadorAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (cubicador is not { EstaConfigurado: true }
            || !cotizacion.Rutas.SelectMany(r => r.Vehiculos).Any(v => v.TipoVehiculo is not null || v.Carroceria is not null))
        {
            return null;
        }

        var vehiculos = await cubicador.ListarTiposVehiculoAsync(cancellationToken);
        var carrocerias = await cubicador.ListarTiposCarroceriaAsync(cancellationToken);
        if (vehiculos.EsFallo || carrocerias.EsFallo)
        {
            logger.LogInformation("El catálogo de vehículos o de carrocerías del cubicador no respondió: el PDF de {Folio} muestra los códigos.", cotizacion.Folio);
        }

        return new clsNombresCatalogoCubicador(
            vehiculos.EsExitoso ? Diccionario(vehiculos.Valor.Select(v => (v.Codigo, v.Nombre))) : null,
            carrocerias.EsExitoso ? Diccionario(carrocerias.Valor.Select(c => (c.Codigo, c.Nombre))) : null);
    }

    private static Dictionary<string, string> Diccionario(IEnumerable<(string Codigo, string Nombre)> filas)
    {
        var diccionario = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (codigo, nombre) in filas)
        {
            diccionario.TryAdd(codigo, nombre);
        }

        return diccionario;
    }

    /// <summary>Texto de un camión sin vehículo ni carrocería (los resuelve la Mesa de Costeo de Nexo, decisión 7).</summary>
    public const string VehiculoPorDefinir = "Vehículo por definir";

    /// <summary>
    /// Precio del transporte de la ruta (fletes, retorno y levante con su margen), en la moneda de la solicitud; <c>null</c> sin costo.
    /// Es la tarifa que ven el Excel, la licitación y <c>totales.precio_transporte</c>: los conexos van aparte (<see cref="ConexosConPrecio"/>),
    /// así que la tarifa + los conexos tienen que dar exactamente <c>precioFinalLinea</c>. HU-043.2 (#251): con la fórmula de hoy los
    /// conexos van a su venta sin margen y la diferencia es 0 (<see cref="clsLineaSolicitud.PrecioRutaCon"/> suma la venta ya redondeada).
    /// Con un precio de la fórmula anterior (<see cref="clsLineaSolicitud.PrecioConFormulaAnterior"/>), cada precio se redondeó por
    /// separado y la diferencia de redondeo (a lo sumo una unidad por conexo) la absorbe la tarifa: <c>precioFinalLinea − conexos</c>. Si
    /// la diferencia es mayor, la ruta no incluye los conexos en su precio (cotización migrada antes de HU-021) y la tarifa es el
    /// transporte calculado.
    /// </summary>
    public static decimal? PrecioTransporte(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        if (ruta.PrecioDe(ruta.CostoTransporte, cotizacion.Moneda, cotizacion.TrmAplicada, out var precio) is not null || precio is not { } transporte)
        {
            return null;
        }

        var conexos = ConexosConPrecio(cotizacion, ruta);
        // HU-045.2 (#264): el seguro de la mercancía también va aparte del transporte (sin margen, en centavos, tal como se sumó al precio).
        var seguro = ruta.ValorSeguro(cotizacion.Moneda, cotizacion.TrmAplicada, out var valorSeguro) is null ? valorSeguro ?? 0m : 0m;
        if (ruta.PrecioFinalLinea is { } total && (conexos.Count > 0 || seguro != 0m))
        {
            // Tolerancia por moneda (HU-021.9): cada precio se redondea a la unidad de su moneda (1 peso en COP, 0,01 en USD), así que la
            // diferencia entre la ruta y transporte + conexos es a lo sumo media unidad por redondeo: (n + 1) / 2 unidades con n conexos,
            // que nunca pasa de n unidades. Una tolerancia de 1 por conexo en USD dejaba pasar un dólar entero por conexo.
            var ajustado = total - conexos.Sum(c => c.Precio) - seguro;
            var unidad = Dominio.Precio.clsConversorMoneda.EsPesos(cotizacion.Moneda) ? 1m : 0.01m;
            if (Math.Abs(ajustado - transporte) <= conexos.Count * unidad)
            {
                return ajustado;
            }
        }

        return transporte;
    }

    /// <summary>
    /// Conexos de la ruta como los ve el comercial (nunca el costo de Nexo); todos incluidos. HU-043.2 (#251): el precio es la VENTA del
    /// conexo sin margen (<see cref="clsLineaSolicitud.VentaConexo"/>), en la moneda de la solicitud; como antes, solo cuando la ruta ya
    /// tiene margen (se resuelve al enviar a Nexo). Una ruta cuyo precio guardado es de la fórmula anterior
    /// (<see cref="clsLineaSolicitud.PrecioConFormulaAnterior"/>: finalizada o con PDF antes de HU-043.2) muestra sus conexos como se
    /// calcularon, con el margen de la ruta, para que el desglose siga cuadrando con su precio. La categoría es la del catálogo de Nexo
    /// (<paramref name="categorias"/>, #173) o <see cref="CategoriaConexo"/> si no se conoce. HU-028.1 (#183): una ruta rechazada por la
    /// Mesa de Costeo no tiene precio y sus conexos tampoco (lista vacía: no salen en el costeo, el PDF ni el Excel, y no suman; siguen
    /// guardados en la ruta y se ven en la captura).
    /// </summary>
    public static IReadOnlyList<clsServicioConexoPrecio> ConexosConPrecio(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IReadOnlyDictionary<string, string>? categorias = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        if (ruta.Rechazada || ruta.MargenAplicado is null || string.IsNullOrWhiteSpace(ruta.MetodoCalculo))
        {
            return [];
        }

        var anterior = ruta.PrecioConFormulaAnterior(cotizacion.Moneda, cotizacion.TrmAplicada);
        return [.. ruta.Conexos.OrderBy(c => c.CreatedAt).ThenBy(c => c.IdServicioConexo, StringComparer.Ordinal)
            .Select(c => (Conexo: c, Error: PrecioConexo(cotizacion, ruta, c, anterior, out var precio), Precio: precio))
            .Where(x => x.Error is null && x.Precio is not null)
            .Select(x => new clsServicioConexoPrecio(
                x.Conexo.IdServicioConexo,
                x.Conexo.Nombre,
                // HU-014 (#188): la categoría guardada (script 05); si no la tiene (fila anterior), la del catálogo o la de respaldo.
                x.Conexo.Categoria
                ?? (categorias is not null && categorias.TryGetValue(x.Conexo.IdServicioConexo, out var categoria) ? categoria : CategoriaConexo),
                x.Conexo.Cantidad,
                x.Precio!.Value,
                true,
                ruta.Orden)
            {
                ServicioId = x.Conexo.IdServicioConexoCatalogo,
            })];
    }

    /// <summary>
    /// HU-043.2 (#251): precio de un conexo para el comercial: su venta sin margen o, con <paramref name="formulaAnterior"/>, su costo con el
    /// margen de la ruta (como se calculó el precio congelado).
    /// </summary>
    private static clsErrorDominio? PrecioConexo(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsServicioConexoRuta conexo, bool formulaAnterior, out decimal? precio) =>
        formulaAnterior
            ? ruta.PrecioDe(conexo.Costo, cotizacion.Moneda, cotizacion.TrmAplicada, out precio)
            : clsLineaSolicitud.VentaConexo(conexo, cotizacion.Moneda, cotizacion.TrmAplicada, out precio);

    /// <summary>
    /// Categoría por defecto de un conexo sin categoría guardada (filas anteriores a #188, que no llenaron <c>categoriaServicioConexo</c> del
    /// script 05) cuando tampoco se pudo leer la del catálogo de Nexo (#173).
    /// </summary>
    public const string CategoriaConexo = "logistico";

    private static string? VehiculoPeso(string? tipoVehiculo, string? carroceria, decimal? pesoKg)
    {
        var texto = string.Join(" - ", new[]
        {
            tipoVehiculo, carroceria,
            pesoKg is { } peso ? $"{peso.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG" : null,
        }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return texto.Length == 0 ? null : texto;
    }

    private async Task<clsCotizacionSolicitud?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    /// <summary><c>COSTEO_NO_LISTO</c> si la cotización todavía no tiene el costeo de Nexo con precio.</summary>
    private static clsErrorDominio? SinCosteo(clsCotizacionSolicitud cotizacion) => cotizacion.TieneCostoDisponible
        ? null
        : new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado.");

    /// <summary>
    /// Condiciones para generar el PDF: hay rutas que cotizar (HU-028.1, #183: con TODAS rechazadas por la Mesa, <c>SIN_RUTAS_COTIZABLES</c>,
    /// decisión D1 de Nexo #320), costeo con precio, sin envío (<c>PDF_YA_ENVIADO</c>, que gana sobre el PDF vigente: OBS-1027-6 de #226),
    /// sin PDF vigente (<c>PDF_YA_GENERADO</c>) y, desde
    /// HU-036.3 (#207), finalizada (<c>COTIZACION_NO_FINALIZADA</c>: el PDF sale con el precio que aceptó el comercial).
    /// </summary>
    private static clsErrorDominio? Bloqueo(clsCotizacionSolicitud cotizacion)
    {
        if (cotizacion.TodasLasRutasRechazadas)
        {
            return new clsErrorDominio(clsCodigosError.SinRutasCotizables,
                $"La Mesa de Costeo de Nexo rechazó todas las rutas de la cotización {cotizacion.Folio}: no hay nada que cotizar y no se genera el PDF.");
        }

        if (SinCosteo(cotizacion) is { } sinCosteo)
        {
            return sinCosteo;
        }

        // OBS-1027-6 (#226): la entrega al cliente manda sobre el PDF vigente. Una enviada (o con resultado) con su PDF responde
        // PDF_YA_ENVIADO (crear una versión nueva), no PDF_YA_GENERADO, que solo aplica antes de entregarla. DEF-1027B-1 (#228): con la
        // llamada para crear la versión nueva en detalle, como margen, conexos, textos y re-solicitar.
        if (clsReglaPdfCosteo.ValidarEntrega(cotizacion, MotivoVersionPdf) is { } entregada)
        {
            return entregada;
        }

        if (cotizacion.PdfPath is not null)
        {
            return new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"La cotización {cotizacion.Folio} ya tiene PDF: genera una versión nueva para cambiarla.");
        }

        return cotizacion.ValidarFinalizadaAntesDe(AccionPdf);
    }

    /// <summary>HU-036.3 (#207): cierre del mensaje de <c>COTIZACION_NO_FINALIZADA</c> al generar el PDF.</summary>
    public const string AccionPdf = "generar el PDF";

    /// <summary>DEF-1027B-1 (#228): motivo de la versión nueva que sugiere el <c>PDF_YA_ENVIADO</c> del PDF.</summary>
    public const string MotivoVersionPdf = "Generar el PDF después del envío";

    /// <summary>
    /// #173: <see cref="Vista"/> con el estado del envío diferido de cada conexo a Nexo (<c>estado_nexo</c>), igual que
    /// <c>GET .../costeo</c>: las respuestas de los PUT de margen y de conexos salían con <c>estado_nexo = null</c>. Se llama después de
    /// guardar: si la lectura de los estados falla, responde sin ellos (y lo deja en el log) en lugar de un 500 con el cambio ya hecho.
    /// HU-045.2 (#264, B2 de la auditoría): con <paramref name="tarifasSeguro"/>, también la tasa vigente del seguro
    /// (<see cref="clsCosteoCotizacion.TasaSeguroVigente"/>), para que el frontend que refresca con la respuesta no la pierda.
    /// </summary>
    public static async Task<clsCosteoCotizacion> VistaConEstadosAsync(
        clsCotizacionSolicitud cotizacion, string? baseUrlPdf, IClsEnvioConexosNexoService? envioConexos, ILogger logger,
        CancellationToken cancellationToken = default, IClsCategoriasConexos? categoriasConexos = null,
        IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? situaciones = null, Margen.DTOs.clsSincronizacionSapVieja? sincronizacionVieja = null,
        IClsTarifaSeguroRepository? tarifasSeguro = null, IClsPoliticaSegurosRepository? politicaSeguros = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(logger);
        var vista = await VistaConEstadosSinTasaAsync(cotizacion, baseUrlPdf, envioConexos, logger, cancellationToken, categoriasConexos, situaciones, sincronizacionVieja);
        if (tarifasSeguro is not null)
        {
            vista = vista with { TasaSeguroVigente = await TasaSeguroVigenteSinFallarAsync(tarifasSeguro, logger, cotizacion.Folio, cancellationToken) };
        }

        // HU-046.3 (#269): con la política, también el bloque politica_seguros de cada ruta (el frontend refresca el panel con la respuesta).
        return politicaSeguros is null
            ? vista
            : clsPoliticaSegurosCosteo.ConPolitica(
                vista, cotizacion, await clsPoliticaSegurosCosteo.LeerSinFallarAsync(politicaSeguros, logger, cotizacion.Folio, cancellationToken));
    }

    private static async Task<clsCosteoCotizacion> VistaConEstadosSinTasaAsync(
        clsCotizacionSolicitud cotizacion, string? baseUrlPdf, IClsEnvioConexosNexoService? envioConexos, ILogger logger,
        CancellationToken cancellationToken, IClsCategoriasConexos? categoriasConexos,
        IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? situaciones, Margen.DTOs.clsSincronizacionSapVieja? sincronizacionVieja)
    {
        var categorias = await CategoriasAsync(categoriasConexos, cotizacion, logger, cancellationToken);
        if (envioConexos is null)
        {
            return Vista(cotizacion, baseUrlPdf, null, categorias, situaciones, sincronizacionVieja);
        }

        try
        {
            return Vista(cotizacion, baseUrlPdf, await envioConexos.EstadosAsync(cotizacion, cancellationToken), categorias, situaciones, sincronizacionVieja);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logEstadosNoDisponibles(logger, cotizacion.Folio, ex);
            return Vista(cotizacion, baseUrlPdf, null, categorias, situaciones, sincronizacionVieja);
        }
    }

    /// <summary>
    /// #173: categorías de los conexos según el catálogo de Nexo (<see cref="IClsCategoriasConexos"/>). Nunca falla: sin servicio o si
    /// la lectura lanza, <c>null</c> (la vista usa <see cref="CategoriaConexo"/>).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>?> CategoriasAsync(
        IClsCategoriasConexos? categoriasConexos, clsCotizacionSolicitud cotizacion, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(logger);
        if (categoriasConexos is null)
        {
            return null;
        }

        try
        {
            return await categoriasConexos.CategoriasAsync(cotizacion, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logCategoriasNoDisponibles(logger, cotizacion.Folio, ex);
            return null;
        }
    }

    private static readonly Action<ILogger, string, Exception?> _logCategoriasNoDisponibles = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1733, nameof(_logCategoriasNoDisponibles)),
        "No se pudo leer la categoría de los conexos de {Folio} en el catálogo de Nexo; se usa la categoría por defecto.");

    private static readonly Action<ILogger, string, Exception?> _logEstadosNoDisponibles = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(1731, nameof(_logEstadosNoDisponibles)),
        "No se pudo leer el estado de los conexos en Nexo de {Folio} después de guardar; la respuesta sale sin estado_nexo.");

    /// <summary>
    /// <c>CosteoCotizacion</c> de la cotización (de los costos de Nexo, solo el costo bruto de cada ruta, HU-039.1, #218). La comparten
    /// el costeo, el ajuste de margen y las demás acciones de la etapa final. <paramref name="situaciones"/> (#181) es lo que Nexo dice de la línea de cada camión (por id de camión), si se conoce.
    /// </summary>
    public static clsCosteoCotizacion Vista(
        clsCotizacionSolicitud cotizacion, string? baseUrlPdf, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo = null,
        IReadOnlyDictionary<string, string>? categorias = null, IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? situaciones = null,
        Margen.DTOs.clsSincronizacionSapVieja? sincronizacionVieja = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);

        // HU-021: el margen y su sugerido de primer nivel son los de la ruta 1 (compatibilidad); cada ruta trae los suyos en `rutas`
        // (HU-021.8, #135). El precio es la suma de las rutas.
        var ruta = cotizacion.PrimeraRuta;
        return new clsCosteoCotizacion(
            cotizacion.Folio,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.CosteoListoAt,
            cotizacion.PrecioFinal,
            cotizacion.PrecioFinal is null ? null : ruta?.MargenAplicado,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            [.. cotizacion.RutasOrdenadas.SelectMany(r => ConEstadoNexo(r, ConexosConPrecio(cotizacion, r, categorias), estadosNexo))],
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            ruta?.MargenSugeridoMostrado,
            ruta?.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            cotizacion.PdfDesactualizado,
            VersionEtag(cotizacion),
            clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion))
        {
            TrmAplicada = cotizacion.TrmAplicada,
            Deadline = cotizacion.Deadline?.ToUniversalTime(),
            ScorePrioridad = cotizacion.ScorePrioridad,
            Resultado = cotizacion.Ganada switch { true => "ganada", false => "perdida", null => null },
            SincronizacionResultadoNexo = cotizacion.EstadoSincronizacionResultado,
            ErrorSincronizacionResultadoNexo = cotizacion.Ganada is null ? null : cotizacion.ErrorSincronizacionResultado,
            FinalizadaAt = cotizacion.FinalizadaAt?.ToUniversalTime(),
            Rutas = [.. cotizacion.RutasOrdenadas.Select(r => RutaCosteo(cotizacion, r, estadosNexo, categorias, situaciones, sincronizacionVieja))],
        };
    }

    /// <summary>
    /// <c>RutaCosteo</c> (HU-021.8; rehecha en HU-021.9, #136): la ruta con su margen, su precio, sus conexos, sus camiones y sus
    /// mercancías, como la ve el comercial. De los costos de Nexo solo lleva el costo bruto de la ruta (HU-039.1, #218), nunca el de cada
    /// camión o conexo.
    /// </summary>
    public static clsRutaCosteo RutaCosteo(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo = null,
        IReadOnlyDictionary<string, string>? categorias = null, IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? situaciones = null,
        Margen.DTOs.clsSincronizacionSapVieja? sincronizacionVieja = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        var conexos = ConEstadoNexo(ruta, ConexosConPrecio(cotizacion, ruta, categorias), estadosNexo);
        var revisar = conexos
            .Where(c => c.EstadoNexo is clsEnvioConexosNexoService.VisibleIncierto or clsEnvioConexosNexoService.VisibleFallidoDefinitivo)
            .Select(c => $"«{c.Nombre ?? c.Id}»")
            .ToList();
        return new clsRutaCosteo(
            ruta.Orden,
            ruta.Id,
            ruta.OrigenNombre,
            ruta.DestinoNombre,
            ruta.EstadoCosteo is { } estadoRuta ? clsCodigoEnum.ACodigo(estadoRuta) : null,
            ruta.PrecioFinalLinea,
            ruta.PrecioFinalLinea is null ? null : ruta.MargenAplicado,
            ruta.MargenSugeridoMostrado,
            ruta.OrigenMargen is { } origenRuta ? clsCodigoEnum.ACodigo(origenRuta) : null,
            conexos)
        {
            // HU-039.1 (#218): el costo bruto sale con el precio, como el margen aplicado (nunca un costo sin precio).
            CostoFinal = ruta.PrecioFinalLinea is null ? null : ruta.CostoFinal,
            MargenSapAnomalo = ruta.MargenSapAnomalo,
            // HU-028.1 (#183): los motivos del rechazo de sus camiones (la ruta puede seguir con precio por los no rechazados).
            MotivoRechazo = ruta.MotivoRechazo,
            AvisoMargen = AvisoMargen(ruta, sincronizacionVieja),
            AvisoConexosNexo = revisar.Count == 0
                ? null
                : $"Revisa en la cotización de Nexo los servicios conexos {string.Join(", ", revisar)}: Cotifly no pudo confirmar que llegaron "
                  + "y no los reenvía solo (Nexo no tiene DELETE). El precio en Cotifly ya los incluye.",
            LineaNegocioId = ruta.LineaNegocioId,
            IncluyeSeguro = ruta.IncluyeSeguro,
            TasaSeguro = ruta.IncluyeSeguro ? ruta.TasaSeguroAplicada : null,
            ValorSeguro = ruta.ValorSeguro(cotizacion.Moneda, cotizacion.TrmAplicada, out var seguro) is null ? seguro : null,
            PesoKg = ruta.PesoKg,
            PesoConTaraKg = ruta.LlevaTara ? ruta.PesoConTaraKg : null,
            Camiones = [.. ruta.VehiculosOrdenados.Select(v => CamionCosteo(v, situaciones) with { PesoConTaraKg = ruta.PesoConTaraCamion(v) })],
            ResultadoCubicacion = ruta.ResultadoCubicacion,
            MotivoSinSeleccion = ruta.MotivoSinSeleccion,
            Condicionada = ruta.Condicionada,
            MensajeSinSeleccion = string.Equals(ruta.ResultadoCubicacion, clsResultadosRecomendacion.SinSeleccionAutomatica, StringComparison.Ordinal)
                ? clsMotivosSinSeleccion.Mensaje(ruta.MotivoSinSeleccion ?? MotivoDelAviso(ruta.AvisosCubicacion))
                : null,
            AvisosCubicacion = ruta.AvisosCubicacion,
            Vehiculos = clsCosteoCotizacionService.Camiones(ruta),
            Mercancias = [.. ruta.MercanciasOrdenadas.Select(m => new clsMercanciaCosteo(
                m.Orden, m.CodigoMercancia, m.NombreMercancia, m.Cantidad, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg))],
        };
    }

    /// <summary>
    /// <c>CamionCosteo</c> del camión <paramref name="camion"/> con lo que Nexo dice de su línea (#181), si se conoce y el camión sigue
    /// asociado a la línea de la que se leyó (<see cref="clsSituacionLineaNexo.LineaNexoId"/>): el estado de la línea siempre y el motivo
    /// pendiente solo con el camión en <c>pendiente_costeo</c>.
    /// </summary>
    public static clsCamionCosteo CamionCosteo(clsVehiculoLineaSolicitud camion, IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? situaciones = null)
    {
        ArgumentNullException.ThrowIfNull(camion);
        // Solo si el camión sigue asociado a la línea de la que se leyó: tras una re-solicitud (el camión conserva su id pero olvida su
        // línea) o un cambio de línea, la foto anterior no se muestra hasta que el polling lea la nueva.
        var situacion = situaciones is not null && situaciones.TryGetValue(camion.Id, out var deNexo)
            && camion.LineaSolicitudNexoId is { } linea && string.Equals(deNexo.LineaNexoId, linea, StringComparison.OrdinalIgnoreCase)
            ? deNexo
            : null;
        return new clsCamionCosteo(
            camion.TipoVehiculo, camion.Carroceria, camion.PesoKg, camion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            camion.VolumenTotalM3, camion.PorcentajeLlenadoVolumen, camion.PorcentajeLlenadoPeso)
        {
            EstadoLineaNexo = situacion?.EstadoLinea,
            MotivoPendiente = camion.EstadoCosteo == EstadoCosteo.PendienteCosteo ? situacion?.MotivoPendiente : null,
            // HU-028.1 (#183): guardados en el camión (script 10), no dependen de la foto en memoria de #181.
            MotivoRechazo = camion.Rechazado ? camion.MotivoRechazo : null,
            RechazadaAt = camion.Rechazado ? camion.RechazadaAt?.ToUniversalTime() : null,
        };
    }

    /// <summary>El motivo real que quedó en la advertencia <c>MOTIVO_SIN_SELECCION</c> cuando la base no lo admitía (sin el script 07).</summary>
    private static string? MotivoDelAviso(clsAvisosCubicacion? avisos) =>
        avisos?.Advertencias?.FirstOrDefault(a => a.Codigo == clsLineaSolicitud.CodigoAvisoMotivoNoGuardado)?.Mensaje is { } mensaje
            ? mensaje.Split(':', 2)[0].Trim()
            : null;

    /// <summary>
    /// HU-009 (#139, B1): aviso al comercial cuando SAP TM propuso para la ruta un margen anómalo (fuera de [0, 60 %]) y se aplicó el de la
    /// política de la línea; <c>null</c> si no.
    /// </summary>
    public static string? AvisoMargenSapAnomalo(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.MargenSapAnomalo && ruta.MargenSAPSugerido is { } sap
            ? $"SAP TM propuso un margen de {(sap * 100m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} % para esta ruta, "
              + "fuera del rango normal (0 a 60 %): no se aplicó y la ruta usa el margen de la política de su línea de negocio. Revísalo antes "
              + "de enviar la cotización."
            : null;
    }

    /// <summary>
    /// <c>aviso_margen</c> de la ruta: el del margen de SAP anómalo (#139) y, si su sugerido salió de SAP TM y la copia lleva más de
    /// <c>SapTm:AvisoSincronizacionHoras</c> sin sincronizarse (HU-042.2, #243), el de la sincronización vieja; los dos, uno tras otro.
    /// </summary>
    public static string? AvisoMargen(clsLineaSolicitud ruta, Margen.DTOs.clsSincronizacionSapVieja? sincronizacionVieja)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var anomalo = AvisoMargenSapAnomalo(ruta);
        var vieja = sincronizacionVieja is not null && Margen.Services.clsAvisoSincronizacionMargenSap.TieneSugeridoDeSap(ruta)
            ? sincronizacionVieja.Aviso
            : null;
        return anomalo is null ? vieja : vieja is null ? anomalo : $"{anomalo} {vieja}";
    }

    /// <summary>Los conexos de la ruta con el estado de su envío a Nexo (decisión 10), si se conoce; sin estados, tal cual.</summary>
    private static IReadOnlyList<clsServicioConexoPrecio> ConEstadoNexo(
        clsLineaSolicitud ruta, IReadOnlyList<clsServicioConexoPrecio> conexos, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo) =>
        estadosNexo is null || estadosNexo.Count == 0
            ? conexos
            : [.. conexos.Select(c => c with
            {
                EstadoNexo = estadosNexo.TryGetValue((ruta.Id, c.Id), out var estado) ? estado : clsEnvioConexosNexoService.VisiblePendiente,
            })];

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}

/// <summary>
/// HU-023.1 (#156): nombres del catálogo del cubicador por código (<c>null</c> = catálogo no disponible) para el PDF. Un código que no
/// está en el catálogo se muestra tal cual.
/// </summary>
public sealed record clsNombresCatalogoCubicador(IReadOnlyDictionary<string, string>? Vehiculos, IReadOnlyDictionary<string, string>? Carrocerias);

/// <summary>El nombre del catálogo con respaldo al código (también con el catálogo en <c>null</c>).</summary>
public static class clsNombresCatalogoCubicadorExtensiones
{
    public static string? Vehiculo(this clsNombresCatalogoCubicador? catalogo, string? codigo) => Nombre(catalogo?.Vehiculos, codigo);

    public static string? Carroceria(this clsNombresCatalogoCubicador? catalogo, string? codigo) => Nombre(catalogo?.Carrocerias, codigo);

    /// <summary>
    /// El nombre de <paramref name="codigo"/> en el catálogo del cubicador: por el código exacto o, si no está, por un código o un nombre
    /// equivalente (<see cref="clsVehiculoLineaSolicitud.MismoCodigo"/>). D2 (2026-10-19): la Mesa de Costeo manda y Nexo puede devolver
    /// el nombre (<c>Estacas</c>) o el código escrito de otra forma (<c>3s3</c>) en vez del código; el PDF y el Excel muestran igual el nombre
    /// del catálogo. Sin coincidencia (o sin catálogo) se muestra lo que haya, tal cual.
    /// </summary>
    public static string? Nombre(IReadOnlyDictionary<string, string>? catalogo, string? codigo)
    {
        if (codigo is null || catalogo is null)
        {
            return codigo;
        }

        if (catalogo.TryGetValue(codigo, out var nombre))
        {
            return nombre;
        }

        foreach (var (clave, valor) in catalogo)
        {
            if (clsVehiculoLineaSolicitud.MismoCodigo(clave, codigo) || clsVehiculoLineaSolicitud.MismoCodigo(valor, codigo))
            {
                return valor;
            }
        }

        return codigo;
    }
}
