namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Filtro del catálogo de servicios conexos de Nexo (HU-013.3, <c>GET /api/servicios-conexos</c>). Cotifly siempre pide solo los
/// activos (<c>soloActivos=true</c>); <paramref name="Q"/> busca en el nombre o el código y <paramref name="Categoria"/> es el segmento
/// del catálogo (<c>logistico</c> | <c>adicional</c>).
/// </summary>
public sealed record clsFiltroCatalogoConexos(string? Q, string? Categoria, int Page, int PageSize);

/// <summary>
/// Servicio del catálogo de Nexo (<c>clsServicioConexoDto</c>) SIN <c>costoUnitario</c>: el adaptador nunca lo lee, así que ningún
/// costo de Nexo puede llegar al comercial por esta vía (ADR-018 §4).
/// </summary>
public sealed record clsServicioConexoNexo(
    string Id,
    string? Codigo,
    string? Nombre,
    string? Categoria,
    string? Unidad,
    string? Origen,
    string? Destino,
    string? Puerto,
    string? Ruta,
    string? TipoMaquina,
    DateOnly? Vigencia,
    bool Activo);

/// <summary>Página del catálogo de Nexo (<c>clsPaginaServiciosDto</c>): los servicios de la página y el total que cumple el filtro.</summary>
public sealed record clsPaginaCatalogoConexosNexo(int Total, IReadOnlyList<clsServicioConexoNexo> Items);

/// <summary>
/// Un conexo tal como viaja a Nexo (<c>clsConexoItemRequest</c>): la línea de la COTIZACIÓN de Nexo, el servicio del catálogo y la
/// cantidad. Nunca lleva <c>valorAplicado</c>: Nexo lo calcula (valor base × cantidad) y es la fuente de verdad del costo.
/// </summary>
public sealed record clsConexoParaNexo(string LineaId, string ServicioConexoId, decimal Cantidad);

/// <summary>
/// Resultado de un POST de conexos que Nexo pudo aplicar. <paramref name="Seguro"/>: Nexo respondió 2xx (el conexo existe aunque el
/// cuerpo falte). <c>false</c>: no hubo respuesta (tiempo agotado, red o 5xx) y el efecto es incierto. <paramref name="Cotizacion"/> es
/// la <c>clsCotizacionDto</c> que devolvió Nexo, o <c>null</c> si no vino o no se pudo leer.
/// </summary>
public sealed record clsAgregadoNexo(bool Seguro, Cotifly.Dominio.Cotizaciones.clsCotizacionNexo? Cotizacion, string? Detalle = null);

/// <summary>
/// <c>ServicioConexoCatalogo</c> del contrato (HU-013.3): el servicio del catálogo de Nexo sin ningún costo.
/// </summary>
public sealed record clsServicioConexoCatalogo(
    string Id,
    string? Codigo,
    string? Nombre,
    string? Categoria,
    string? Unidad,
    string? Origen,
    string? Destino,
    string? Puerto,
    string? Ruta,
    string? TipoMaquina,
    DateOnly? Vigencia);

/// <summary><c>PaginaServiciosConexos</c> del contrato: la página pedida, su tamaño, el total del filtro y los servicios.</summary>
public sealed record clsPaginaServiciosConexos(int Total, int Page, int PageSize, IReadOnlyList<clsServicioConexoCatalogo> Items);

/// <summary>
/// Un item de <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos</c> tal como llega (<c>ConexoAgregarWeb</c>); lo valida
/// <c>clsServiciosConexosCosteoService</c>.
/// </summary>
public sealed record clsConexoAgregarEntrada(int? LineaOrden, string? ServicioId, decimal? Cantidad);
