using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// HU-028 (#186, ADR-021): entrada de <c>PUT /web/cotizaciones/{folio}/solicitud</c> (<c>EdicionSolicitudWeb</c>). Reemplazo completo del
/// contenido editable: cada ruta con <see cref="clsRutaEdicionEntrada.LineaSolicitudId"/> es la misma ruta guardada; sin él, una ruta
/// nueva; las guardadas que no vienen se quitan. Los campos bloqueados (<see cref="Tipo"/>, <see cref="NitCliente"/>,
/// <see cref="ClienteNombre"/>, <see cref="OrigenCliente"/>) son opcionales: si vienen deben tener el valor guardado. <see cref="Moneda"/>
/// ausente conserva la moneda guardada. <see cref="Motivo"/> (opcional, hasta 300) queda en la auditoría y viaja a Nexo como comentario del
/// rechazo. <see cref="ModoEsperado"/> (opcional) es el modo que anunció el GET: si ya no es el mismo, 409 <c>ESTADO_NO_PERMITE</c>.
/// </summary>
public sealed record clsEntradaEdicionSolicitud(
    IReadOnlyList<clsRutaEdicionEntrada?> Rutas,
    string? Tipo = null,
    string? NitCliente = null,
    string? ClienteNombre = null,
    string? OrigenCliente = null,
    string? LineaNegocio = null,
    DateTimeOffset? Deadline = null,
    string? Notas = null,
    decimal? HorasCargue = null,
    decimal? HorasDescargue = null,
    string? Moneda = null,
    string? Motivo = null,
    string? ModoEsperado = null)
{
    /// <summary>Largo máximo del motivo (como el de la re-solicitud).</summary>
    public const int LargoMaximoMotivo = 300;
}

/// <summary>
/// Una ruta de la edición: el id de la ruta guardada que se edita (<c>null</c> = nueva) y su contenido (como en la captura). HU-028.1 (#183):
/// <paramref name="ReincluirRechazados"/> (<c>reincluir_rechazados</c>) pide que los camiones de la ruta que rechazó la Mesa de Costeo
/// vuelvan a viajar a Nexo sin el rechazo (por defecto no viajan); en una ruta nueva o sin rechazados no hace nada.
/// </summary>
public sealed record clsRutaEdicionEntrada(Guid? LineaSolicitudId, clsLineaLicitacionEntrada Ruta, bool ReincluirRechazados = false);

/// <summary>Aviso de la edición para el comercial (<c>AvisoEdicion</c>): código estable y texto.</summary>
public sealed record clsAvisoEdicion(string Codigo, string Mensaje)
{
    public const string ReemplazaSolicitudNexo = "REEMPLAZA_SOLICITUD_NEXO";
    public const string PierdeTrabajoMesa = "PIERDE_TRABAJO_MESA";
    public const string PierdeCosteoActual = "PIERDE_COSTEO_ACTUAL";
    public const string InvalidaPdf = "INVALIDA_PDF";
    public const string CreaVersionNueva = "CREA_VERSION_NUEVA";
    public const string MargenDesvinculado = "MARGEN_RUTA_QUITADA";
    public const string SinCambios = "SIN_CAMBIOS";

    /// <summary>HU-028.1 (#183): la edición dejó fuera del envío camiones (o rutas) que la Mesa de Costeo había rechazado.</summary>
    public const string RechazadosOmitidos = "RECHAZADOS_OMITIDOS";
}

/// <summary>
/// HU-028: respuesta de <c>PUT /web/cotizaciones/{folio}/solicitud</c> (<c>EdicionSolicitudResultado</c>): el modo aplicado, el folio
/// resultante (el mismo, o el de la <c>-vN</c>), la cotización resultante, los avisos, qué rutas se volvieron a cubicar, cuáles se
/// agregaron (por orden) y cuáles se quitaron (por id), y cómo quedó la solicitud anterior en Nexo en el reemplazo.
/// </summary>
public sealed record clsResultadoEdicionSolicitud(
    string Modo,
    string Folio,
    string FolioOrigen,
    bool SinCambios,
    clsCotizacionResumen Cotizacion,
    IReadOnlyList<clsAvisoEdicion> Avisos,
    IReadOnlyList<int> RutasReplanificadas,
    IReadOnlyList<int> RutasAgregadas,
    IReadOnlyList<Guid> RutasQuitadas,
    string? SolicitudNexoReemplazada)
{
    /// <summary>
    /// HU-028.1 (#183, hallazgo B3): qué camiones y rutas rechazados por la Mesa de Costeo quedaron fuera del envío, con sus motivos (el mismo
    /// texto que <c>cotizacion.aviso_rechazos</c> y el aviso <c>RECHAZADOS_OMITIDOS</c>); <c>null</c> si ninguno (no se serializa).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AvisoRechazos { get; init; }
}

/// <summary>Lo que devuelve el caso de uso: la respuesta, la versión de negocio resultante (<c>ETag</c>) y si se creó la <c>-vN</c> (201).</summary>
public sealed record clsEdicionAplicada(clsResultadoEdicionSolicitud Respuesta, string RowVersion, bool Creada);

/// <summary>
/// HU-028: <c>GET /web/cotizaciones/{folio}/solicitud</c> (<c>SolicitudEditable</c>): el modelo editable completo, los campos bloqueados
/// con su valor, cómo se aplicaría la edición en el estado actual y las rutas con todo lo que se captura. Sin costos de Nexo.
/// </summary>
public sealed record clsSolicitudEditable(
    string Folio,
    int Version,
    int VersionNegocio,
    clsBloqueadosSolicitud Bloqueados,
    IReadOnlyList<string> CamposBloqueados,
    clsEstadoEdicion Edicion,
    DateTimeOffset? Deadline,
    string? Notas,
    string Moneda,
    string? LineaNegocio,
    IReadOnlyList<string> LineasNegocioDisponibles,
    IReadOnlyList<clsRutaEditable> Rutas);

/// <summary>Los campos que no se editan, con su valor guardado (<c>BloqueadosSolicitud</c>).</summary>
public sealed record clsBloqueadosSolicitud(string? Tipo, string? NitCliente, string? ClienteNombre, string? OrigenCliente);

/// <summary>
/// Cómo se aplicaría una edición ahora (<c>EstadoEdicion</c>): el modo, si se permite, el motivo si no (<c>LICITACION</c>,
/// <c>SIN_FORMULARIO</c>, <c>ENVIO_EN_CURSO</c>, <c>CUBICACION_EN_CURSO</c>) con su mensaje, y los avisos para confirmar.
/// </summary>
public sealed record clsEstadoEdicion(string? Modo, bool Permitida, string? MotivoNoPermitida, string? Mensaje, IReadOnlyList<clsAvisoEdicion> Avisos);

/// <summary>
/// Una ruta editable (<c>RutaEditable</c>): todo lo que se captura, con los municipios por id y nombre, y su margen. HU-028.1 (#183):
/// <paramref name="CamionesRechazados"/> y <paramref name="MotivoRechazo"/> (solo lectura) dicen si la Mesa de Costeo rechazó camiones de la
/// ruta: no se reenvían salvo que el PUT traiga <c>reincluir_rechazados</c> en la ruta.
/// </summary>
public sealed record clsRutaEditable(
    Guid LineaSolicitudId,
    int LineaOrden,
    string? LineaNegocio,
    bool LineaNegocioBloqueada,
    int? OrigenMunicipioId,
    string? Origen,
    int? DestinoMunicipioId,
    string? Destino,
    string? Tipologia,
    string? ClaseViaje,
    string? ClaseContenedor,
    bool? Exclusivo,
    bool? Consolidado,
    bool RetornaContenedor,
    int? CiudadDevolucionMunicipioId,
    string? CiudadDevolucionContenedor,
    bool LevanteContenedorDiferente,
    int? CiudadLevanteMunicipioId,
    string? CiudadLevanteContenedor,
    decimal? HorasCargue,
    decimal? HorasDescargue,
    decimal? ValorLevanteContenedor,
    decimal? ValorMercancia,
    decimal? PesoKg,
    IReadOnlyList<clsMercanciaEditable> Mercancias,
    IReadOnlyList<clsConexoEditable> ServiciosConexos,
    decimal? MargenAplicado,
    string? OrigenMargen,
    bool MargenNegociado,
    int CamionesRechazados = 0,
    string? MotivoRechazo = null);

/// <summary>Una mercancía editable (<c>MercanciaEditable</c>), con su código del catálogo del cubicador.</summary>
public sealed record clsMercanciaEditable(
    string? CodigoMercancia, string? NombreMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

/// <summary>Un servicio conexo editable (<c>ConexoEditable</c>): el id del catálogo de Nexo, su nombre y la cantidad (sin precio).</summary>
public sealed record clsConexoEditable(string IdServicioConexo, string? Nombre, decimal Cantidad);
