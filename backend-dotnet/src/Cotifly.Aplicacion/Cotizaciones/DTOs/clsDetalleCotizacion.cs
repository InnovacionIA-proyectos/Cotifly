using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;

namespace Cotifly.Aplicacion.Cotizaciones.DTOs;

/// <summary>
/// <c>DetalleCotizacion</c> (HU-027.1, #179): <c>GET /web/cotizaciones/{folio}</c>, la cotización completa en una llamada para "Ver detalle"
/// (HU-021-FE.7, #75) y para recargar esa página. Es una composición de las proyecciones que ya existen, sin reglas nuevas:
/// <list type="bullet">
/// <item><see cref="Cotizacion"/>: la fila de "Mis cotizaciones" (<c>CotizacionResumen</c>) con el comercial dueño (<c>comercial</c>).</item>
/// <item><see cref="Costeo"/>: lo mismo que <c>GET .../costeo</c> (<c>CosteoCotizacion</c>): rutas con camiones, porcentajes, mercancías,
/// conexos con precio, margen, precio por ruta, costo bruto por ruta (HU-039.1, #218: solo el dueño; con <see cref="SoloLectura"/> va
/// en <c>null</c>) y avisos; <c>pdf_desactualizado</c> y <c>textos</c>.</item>
/// <item><see cref="Captura"/>: cada ruta como la capturó el comercial (<c>RutaEditable</c> de <c>GET .../solicitud</c>): municipios por id
/// y nombre y condiciones. Se cruza con <c>costeo.rutas[]</c> por <c>linea_solicitud_id</c>.</item>
/// </list>
/// </summary>
/// <param name="Folio">Folio consultado (también en <c>cotizacion</c> y <c>costeo</c>).</param>
/// <param name="RowVersion">Versión de negocio de la cotización (también en el <c>ETag</c>): el <c>If-Match</c> de las acciones que siguen.</param>
/// <param name="SoloLectura"><c>true</c> si quien consulta es un gerente de la regional del dueño: solo consulta (las acciones son del dueño).</param>
/// <param name="Tipo">Tipo de la solicitud (<c>spot</c>, <c>licitacion</c>...); <c>null</c> en las del chat.</param>
/// <param name="Notas">Notas de la solicitud (formulario de costeo).</param>
public sealed record clsDetalleCotizacion(
    string Folio,
    string? RowVersion,
    bool SoloLectura,
    string? Tipo,
    string? Notas,
    clsFechasCotizacion Fechas,
    clsCotizacionResumen Cotizacion,
    clsCosteoCotizacion Costeo,
    IReadOnlyList<clsRutaEditable> Captura,
    clsTotalesCotizacion Totales) : IClsConVersionFila;

/// <summary><c>FechasCotizacion</c> (#179): las fechas de la cabecera (todas en UTC).</summary>
/// <param name="CreatedAt">Creación.</param>
/// <param name="UpdatedAt">Último cambio de la fila.</param>
/// <param name="CosteoEnviadoAt">Último envío a Nexo; <c>null</c> si nunca llegó.</param>
/// <param name="CosteoListoAt">Cuándo quedó lista con precio; <c>null</c> sin costeo.</param>
/// <param name="MarcadoAt">Cuándo se marcó el resultado (ganada o perdida); <c>null</c> sin resultado.</param>
public sealed record clsFechasCotizacion(
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? CosteoEnviadoAt, DateTimeOffset? CosteoListoAt, DateTimeOffset? MarcadoAt);

/// <summary>
/// <c>TotalesCotizacion</c> (#179): conteos y precios de toda la solicitud, derivados de <c>costeo.rutas[]</c> con las reglas del costeo y
/// del PDF (nunca costos de Nexo). Los precios son <c>null</c> mientras alguna ruta no tenga precio.
/// </summary>
/// <param name="Rutas">Número de rutas.</param>
/// <param name="Camiones">Camiones que cuentan en el precio (los descartados en Nexo no, salvo que lo estén todos; HU-028.1, #183: los
/// rechazados por la Mesa de Costeo nunca).</param>
/// <param name="PesoKg">Suma del peso declarado de las rutas; <c>null</c> si ninguna lo trae.</param>
/// <param name="PrecioTransporte">Suma de la tarifa de transporte de las rutas (la del PDF).</param>
/// <param name="PrecioConexos">Suma del precio de los servicios conexos de todas las rutas.</param>
/// <param name="PrecioFinal">Precio de la solicitud (= <c>costeo.precio_final</c>): transporte + conexos.</param>
public sealed record clsTotalesCotizacion(
    int Rutas, int Camiones, decimal? PesoKg, decimal? PrecioTransporte, decimal? PrecioConexos, decimal? PrecioFinal)
{
    /// <summary>HU-028.1 (#183): rutas rechazadas por la Mesa de Costeo de Nexo (todos sus camiones): no cuentan en los precios.</summary>
    public int RutasRechazadas { get; init; }

    /// <summary>HU-028.1 (#183): camiones rechazados por la Mesa de Costeo de Nexo (de cualquier ruta).</summary>
    public int CamionesRechazados { get; init; }
}
