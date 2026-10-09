namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Cuerpo de <c>PUT /web/cotizaciones/{folio}/costeo/servicios-conexos</c> (<c>SeleccionServiciosConexos</c>; por ruta desde HU-021.8,
/// #135, decisión 3), tal como llega. Dos formas, excluyentes:
/// <list type="bullet">
/// <item><see cref="Conexos"/> (nueva): los conexos que deben QUEDAR en la ruta <see cref="Ruta"/>, cada uno con su cantidad; los que no
/// vienen se quitan (se borra la fila) y a los demás se les cambia la cantidad. Sin ruta, la única de la cotización.</item>
/// <item><see cref="Incluidos"/> (HU-005.7): los ids que se conservan, sin cambiar cantidades; los demás se quitan. Con ruta, solo en esa
/// ruta; sin ruta, en todas.</item>
/// </list>
/// Agregar un conexo nuevo es el <c>POST</c> (su costo lo da Nexo). Los límites los valida <c>clsCosteoCotizacionService</c>.
/// </summary>
public sealed record clsCambioServiciosConexos(
    clsRutaIndicada Ruta, IReadOnlyCollection<string>? Incluidos, IReadOnlyList<clsCantidadConexoEntrada>? Conexos);

/// <summary><c>CantidadConexoWeb</c>: el <c>id</c> de un conexo de la ruta (<c>servicios_conexos[].id</c>) y su cantidad nueva.</summary>
public sealed record clsCantidadConexoEntrada(string? Id, decimal? Cantidad);
