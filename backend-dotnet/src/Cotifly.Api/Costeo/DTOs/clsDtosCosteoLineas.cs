using Cotifly.Aplicacion.Costeo.DTOs;

namespace Cotifly.Api.Costeo.DTOs;

/// <summary>
/// <c>MargenLineasWeb</c>: cuerpo de <c>PUT /web/cotizaciones/{folio}/costeo/margen/lineas</c>. Las rutas (<c>lineas</c>
/// o <c>todas</c>) y el margen (<c>margen</c> o <c>revertir_sugerido</c>); la forma la valida el caso de uso (400 <c>ErrorWeb</c>).
/// </summary>
public sealed record clsMargenLineasWebRequest(
    IReadOnlyList<int?>? Lineas, bool? Todas, decimal? Margen, bool? RevertirSugerido, string? Justificacion)
{
    /// <summary>Entrada del caso de uso.</summary>
    public clsMargenLineasEntrada AEntrada() => new(new clsSeleccionLineas(Lineas, Todas), Margen, RevertirSugerido, Justificacion);
}

/// <summary>
/// <c>ConexoLineasWeb</c>: cuerpo de <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas</c>: las rutas
/// (<c>lineas</c> o <c>todas</c>), el <c>servicio_id</c> del catálogo de Nexo y la <c>cantidad</c>.
/// </summary>
public sealed record clsConexoLineasWebRequest(IReadOnlyList<int?>? Lineas, bool? Todas, string? ServicioId, decimal? Cantidad)
{
    /// <summary>Entrada del caso de uso.</summary>
    public clsConexoLineasEntrada AEntrada() => new(new clsSeleccionLineas(Lineas, Todas), ServicioId, Cantidad);
}

/// <summary>
/// <c>QuitarConexoLineasWeb</c>: cuerpo de <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas/quitar</c>:
/// las rutas (<c>lineas</c> o <c>todas</c>) y el <c>servicio_id</c> del catálogo de Nexo (<c>servicios_conexos[].servicio_id</c>).
/// </summary>
public sealed record clsQuitarConexoLineasWebRequest(IReadOnlyList<int?>? Lineas, bool? Todas, string? ServicioId)
{
    /// <summary>Entrada del caso de uso.</summary>
    public clsConexoLineasEntrada AEntrada() => new(new clsSeleccionLineas(Lineas, Todas), ServicioId);
}
