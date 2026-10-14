using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.DTOs;

/// <summary>
/// Fila de "Mis cotizaciones" (<c>CotizacionResumen</c> de <c>specs/openapi.yaml</c>). La Api la serializa en snake_case
/// (<c>nit_cliente</c>, <c>precio_final</c>...); los enums van con su código (<c>sin_enviar</c>, <c>politica_linea</c>).
/// </summary>
/// <param name="Cliente">Nombre del cliente (<c>clienteNombre</c>).</param>
/// <param name="LineaNegocio">Nombre de la línea (JOIN con <c>MargenesPolitica</c> por <c>lineaNegocioId</c>).</param>
/// <param name="PdfUrl"><c>{Pdf:BaseUrl}/pdfs/{archivo}</c>, o <c>null</c> si la cotización no tiene PDF.</param>
/// <param name="TrmAplicada">TRM con que se convirtió el precio a USD (HU-012.2); 0 en COP.</param>
/// <param name="ConexosDescartados">
/// Solo en la respuesta de <c>POST .../costeo/re-solicitar</c> (HU-013.15, #124). Desde HU-021.9 (#136) siempre vacía: los conexos se
/// conservan (<c>conexos_conservados</c>). En el resto de respuestas es <c>null</c> y no se serializa.
/// </param>
public sealed record clsCotizacionResumen(
    Guid Id,
    string Folio,
    int Version,
    string? NitCliente,
    string? OrigenCliente,
    string? OrigenMargen,
    string? Cliente,
    string? LineaNegocio,
    decimal? PrecioFinal,
    decimal? MargenAplicado,
    string Moneda,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    string EstadoEnvio,
    bool? Ganada,
    string? MotivoRechazo,
    string? PdfUrl,
    DateTimeOffset? CreatedAt,
    decimal TrmAplicada = 0m,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ConexosDescartados = null)
{
    /// <summary>
    /// Solo en la respuesta de <c>POST .../costeo/re-solicitar</c> (HU-021.9, #136): nombres de los servicios conexos que la cotización
    /// resultante conserva o copió (sin montos); siguen en el precio de su ruta. En el resto de respuestas es <c>null</c> y no se serializa.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConexosConservados { get; init; }

    /// <summary>
    /// Solo en la respuesta de <c>POST /web/solicitudes-costeo</c> (HU-023.3, #158): por cada ruta que pasó por el cubicador, su resultado
    /// y sus avisos (mercancías condicionadas, advertencias y extradimensión, que no se guardan). <c>null</c> (no se serializa) si ninguna
    /// ruta llamó al cubicador o en el resto de respuestas.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<clsCubicacionRutaResumen>? Cubicacion { get; init; }

    /// <summary>
    /// #173: avisos de una respuesta que salió incompleta después de guardar (hoy solo <c>RESUMEN_INCOMPLETO</c>: falló la lectura del
    /// nombre de la línea). <c>null</c> (no se serializa) cuando el resumen está completo.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Avisos { get; init; }

    /// <summary>
    /// Resumen de una cotización con el nombre de su línea y la base pública de los PDF. El margen y su origen son los de la ruta 1
    /// (HU-021: el margen es por ruta; el de cada ruta está en <c>CosteoCotizacion.rutas[]</c>, #138).
    /// </summary>
    public static clsCotizacionResumen Desde(clsCotizacionSolicitud cotizacion, string? lineaNegocio, string? baseUrlPdf)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ruta = cotizacion.PrimeraRuta;
        return Desde(cotizacion, lineaNegocio, baseUrlPdf, ruta?.MargenAplicado, ruta?.OrigenMargen);
    }

    /// <summary>Resumen con el margen y el origen del margen ya leídos (lista de "Mis cotizaciones", sin cargar el agregado).</summary>
    public static clsCotizacionResumen Desde(
        clsCotizacionSolicitud cotizacion, string? lineaNegocio, string? baseUrlPdf, decimal? margenAplicado, Dominio.Margen.OrigenMargen? origenMargen)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return new clsCotizacionResumen(
            cotizacion.Id,
            cotizacion.Folio,
            cotizacion.Version,
            cotizacion.NitCliente,
            cotizacion.OrigenCliente is { } origenCliente ? clsCodigoEnum.ACodigo(origenCliente) : null,
            origenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            cotizacion.ClienteNombre,
            lineaNegocio,
            cotizacion.PrecioFinal,
            margenAplicado,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            cotizacion.EstadoCosteo is { } estadoCosteo ? clsCodigoEnum.ACodigo(estadoCosteo) : null,
            cotizacion.MotivoEstadoCosteo,
            clsCodigoEnum.ACodigo(cotizacion.EstadoEnvio ?? Dominio.Cotizaciones.EstadoEnvio.SinEnviar),
            cotizacion.Ganada,
            cotizacion.MotivoRechazo,
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            cotizacion.CreatedAt == default ? null : cotizacion.CreatedAt,
            cotizacion.TrmAplicada);
    }
}

/// <summary>
/// Cabecera con la línea de negocio, el margen y el origen del margen de su ruta 1 (o "Varias" si sus rutas tienen líneas distintas):
/// lectura de "Mis cotizaciones" sin cargar el agregado (HU-021).
/// </summary>
public sealed record clsCotizacionConLinea(
    clsCotizacionSolicitud Cotizacion, string? LineaNegocio, decimal? MargenAplicado = null, Dominio.Margen.OrigenMargen? OrigenMargen = null)
{
    /// <summary>Texto de la línea de negocio cuando las rutas de la solicitud tienen líneas distintas.</summary>
    public const string Varias = "Varias";
}

/// <summary><c>MisCotizaciones</c>: <c>{total, cotizaciones}</c>, máximo 100, de la más reciente a la más antigua.</summary>
public sealed record clsMisCotizaciones(int Total, IReadOnlyList<clsCotizacionResumen> Cotizaciones);

/// <summary>
/// <c>DatosNuevaVersion</c>: datos de una cotización existente para prellenar una versión nueva. No se persiste nada;
/// <paramref name="CotizacionOrigenId"/> es solo el id consultado (sin FK en la versión nueva).
/// </summary>
/// <param name="FolioBase">Folio de la v1 (sin <c>-vN</c>).</param>
/// <param name="VersionSiguiente">Versión que tendría la nueva fila (máxima de la familia + 1), informativa: la definitiva la
/// asigna <c>IClsGeneradorFolioService</c> al crearla.</param>
public sealed record clsDatosNuevaVersion(
    Guid CotizacionOrigenId,
    string FolioBase,
    int VersionSiguiente,
    string? ClienteNombre,
    string? NitCliente,
    string? OrigenCliente,
    string? LineaNegocio,
    decimal? Costo,
    decimal? MargenPolitica,
    decimal? MargenAplicado,
    string? OrigenMargen,
    string? MetodoCalculo,
    string? Moneda)
{
    /// <summary>
    /// HU-021.9 (#136): las rutas de la cotización, en orden, con su línea de negocio y su margen: la versión nueva copia cada ruta con
    /// los suyos. Los campos de margen de primer nivel son los de la ruta 1 (compatibilidad) y <see cref="LineaNegocio"/> es la de la ruta
    /// 1 o "Varias" si sus rutas tienen líneas distintas.
    /// </summary>
    public IReadOnlyList<clsRutaNuevaVersion> Rutas { get; init; } = [];
}

/// <summary><c>RutaNuevaVersion</c> (HU-021.9, #136): una ruta de la cotización que se copia en la versión nueva, sin costos.</summary>
public sealed record clsRutaNuevaVersion(
    int LineaOrden,
    int? LineaNegocioId,
    string? LineaNegocio,
    string? Origen,
    string? Destino,
    decimal? MargenPolitica,
    decimal? MargenAplicado,
    string? OrigenMargen,
    string? MetodoCalculo);

/// <summary>Cotización que toca consultar en Nexo (agenda del polling, HU-005.6) y la marca con la que se reclama.</summary>
public sealed record clsConsultaCosteoPendiente(Guid Id, DateTimeOffset SiguienteConsultaAt);

/// <summary>
/// HU-023.5 (#160): una ruta de la cola de la cubicación en segundo plano: su id, el de su cotización y el <c>cubicacionDisponibleAt</c>
/// leído (para el reclamo optimista).
/// </summary>
public sealed record clsRutaPorCubicar(Guid RutaId, Guid CotizacionId, DateTimeOffset? DisponibleAt);

/// <summary>
/// <c>CubicacionRuta</c> (HU-023.3, #158): lo que respondió el cubicador para una ruta de la captura. <see cref="Resultado"/>,
/// <see cref="MotivoSinSeleccion"/> y <see cref="Condicionada"/> se guardan (y salen también en <c>GET .../costeo</c>); los avisos no.
/// </summary>
public sealed record clsCubicacionRutaResumen(
    int LineaOrden,
    Guid LineaSolicitudId,
    string? Resultado,
    string? MotivoSinSeleccion,
    bool Condicionada,
    IReadOnlyList<clsMercanciaCondicionada> MercanciasCondicionadas,
    IReadOnlyList<clsAdvertenciaCubicador> Advertencias,
    clsExtradimensionCubicador? Extradimension)
{
    /// <summary>Las rutas de <paramref name="cotizacion"/> que pasaron por el cubicador, en orden; <c>null</c> si ninguna.</summary>
    public static IReadOnlyList<clsCubicacionRutaResumen>? De(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas
            .Where(r => r.ResultadoCubicacion is not null)
            .Select(r => new clsCubicacionRutaResumen(
                r.Orden, r.Id, r.ResultadoCubicacion, r.MotivoSinSeleccion, r.Condicionada,
                r.AvisosCubicacion?.MercanciasCondicionadas ?? [], r.AvisosCubicacion?.Advertencias ?? [], r.AvisosCubicacion?.Extradimension))
            .ToList();
        return rutas.Count == 0 ? null : rutas;
    }
}
