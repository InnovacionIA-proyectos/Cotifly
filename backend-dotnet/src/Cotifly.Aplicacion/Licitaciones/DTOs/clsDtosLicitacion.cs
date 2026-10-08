using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.DTOs;

/// <summary>
/// <c>AnalisisExcelDto</c> de Nexo (<c>POST /api/captura-solicitudes/analizar-excel</c>, HU-011.1): las hojas del Excel del cliente
/// con su fila de encabezado, los encabezados y las filas con datos como texto, para que el comercial mapee las columnas. No se guarda.
/// </summary>
public sealed record clsAnalisisExcel(string Archivo, IReadOnlyList<clsHojaExcel> Hojas);

/// <summary>
/// Una hoja analizada. <paramref name="Filas"/> trae hasta 1000 filas; <paramref name="TotalFilas"/>, el total real.
/// <paramref name="FilasExcel"/> (HU-013.15, #124): el número de fila de Excel (base 1, encabezado incluido) de cada elemento de
/// <paramref name="Filas"/>, el que ve el comercial en su archivo; lo calcula Cotifly con la misma regla de Nexo. <c>null</c> si no se
/// pudo ubicar (la hoja no cuadra con el análisis de Nexo).
/// </summary>
public sealed record clsHojaExcel(
    string Nombre,
    int FilaEncabezado,
    IReadOnlyList<string> Encabezados,
    IReadOnlyList<IReadOnlyList<string>> Filas,
    int TotalFilas,
    IReadOnlyList<int>? FilasExcel = null);

/// <summary>Archivo subido por el comercial (multipart): nombre y bytes.</summary>
public sealed record clsArchivoSubido(string? Nombre, byte[] Contenido);

/// <summary>Una línea mapeada por el frontend: los campos de <c>CapturaLineaRequest</c> y la fila del Excel de donde salió.</summary>
public sealed record clsLineaLicitacionEntrada(int? Fila, clsLineaSolicitudCosteo Linea);

/// <summary>
/// Entrada de <c>POST /web/licitaciones</c> (HU-011.2, ADR-017): el cliente (NIT del buscador), la línea de negocio de Cotifly, el
/// encabezado (siempre <c>licitacion</c>), la hoja mapeada, las líneas ya mapeadas, opcionalmente el Excel original y la moneda
/// (<c>COP</c> por defecto o <c>USD</c>, ADR-014 §5: en USD los precios se convierten con la TRM del día de creación).
/// </summary>
public sealed record clsEntradaLicitacion(
    string? NitCliente,
    string? ClienteNombre,
    string? LineaNegocio,
    clsEncabezadoSolicitudCosteo Encabezado,
    string? Hoja,
    IReadOnlyList<clsLineaLicitacionEntrada> Lineas,
    clsArchivoSubido? Archivo,
    string? Moneda = null);

/// <summary>
/// Un error del reporte de validación (<c>ErrorFilaLicitacion</c>): <paramref name="Indice"/> es la posición en <c>lineas[]</c>
/// (base 0; <c>null</c> si el error es del encabezado), <paramref name="Fila"/> la fila del análisis que mandó el frontend (índice
/// base 0 en <c>hojas[h].filas</c>, técnico) y <paramref name="FilaExcel"/> (HU-013.15, #124) el número de fila de la hoja de Excel
/// (base 1, encabezado incluido), el que ve el comercial: solo con el archivo adjunto y una <c>fila</c> que existe en la hoja.
/// </summary>
public sealed record clsErrorFilaLicitacion(int? Indice, int? Fila, string Campo, string Codigo, string Mensaje, int? FilaExcel = null);

/// <summary>
/// Resultado de crear una licitación: la cotización creada, o un error de negocio (<see cref="Error"/>), o el reporte por fila
/// (<see cref="Errores"/>, con <see cref="Error"/> = <c>LICITACION_INVALIDA</c>). Nada queda guardado si hay error.
/// </summary>
public sealed record clsResultadoLicitacion(clsCotizacion? Cotizacion, Dominio.Comun.clsErrorDominio? Error, IReadOnlyList<clsErrorFilaLicitacion> Errores)
{
    public static clsResultadoLicitacion Exito(clsCotizacion cotizacion) => new(cotizacion, null, []);

    public static clsResultadoLicitacion Fallo(Dominio.Comun.clsErrorDominio error) => new(null, error, []);

    /// <summary>
    /// Reporte por fila. <paramref name="catalogoNoDisponible"/> (HU-013.11) es el código del catálogo que no respondió
    /// (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c> o <c>MUNICIPIOS_NO_DISPONIBLE</c>): va en <c>error.detalle.motivo</c> y el mensaje avisa
    /// que lo que dependía de él se valida al volver a enviarla.
    /// </summary>
    public static clsResultadoLicitacion Invalida(IReadOnlyList<clsErrorFilaLicitacion> errores, string? catalogoNoDisponible = null) => new(
        null,
        new Dominio.Comun.clsErrorDominio(Dominio.Comun.clsCodigosError.LicitacionInvalida,
            $"La licitación tiene {errores.Count} error(es): corrige las filas indicadas y vuelve a enviarla."
            + (catalogoNoDisponible is null
                ? string.Empty
                : " Un catálogo de Nexo no respondió: lo que depende de él (tipo de vehículo, clase de viaje o municipios) se validará al volver a enviarla."),
            catalogoNoDisponible),
        errores);
}

/// <summary>
/// <c>Licitacion</c>: la vista del comercial de una cotización de varias líneas (estado y precios; nunca el costo de Nexo).
/// </summary>
public sealed record clsLicitacion(
    string Folio,
    string? ClienteNombre,
    string? NitCliente,
    string? Referencia,
    DateTimeOffset? Deadline,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    string Moneda,
    decimal TrmAplicada,
    decimal? PrecioFinal,
    int LineasCosteadas,
    int LineasPendientes,
    string? ArchivoNombre,
    string? Hoja,
    IReadOnlyList<clsLineaLicitacion> Lineas);

/// <summary>
/// Datos de la exportación del Excel de una licitación (HU-011.3, ADR-017 §5): solo precios con margen y estados, nunca el costo de
/// Nexo. <see cref="Original"/> es el Excel del cliente (o <c>null</c> si la licitación se armó sin archivo).
/// </summary>
public sealed record clsExportacionLicitacion(
    string Folio,
    string? Cliente,
    string? NitCliente,
    string? Referencia,
    string Moneda,
    decimal TrmAplicada,
    decimal? PrecioTotal,
    string? Hoja,
    byte[]? Original,
    IReadOnlyList<clsLineaExportacion> Lineas,
    IReadOnlyList<clsConexoExportacion> ServiciosConexos,
    DateTimeOffset GeneradoEn);

/// <summary>Una línea exportada: dónde va (fila del análisis), qué es, su estado legible y su precio (<c>null</c> = sin costeo).</summary>
public sealed record clsLineaExportacion(
    int Orden, int? Fila, string Origen, string Destino, string? TipoVehiculo, decimal? PesoKg, string Estado, decimal? PrecioFinal);

/// <summary>Servicio conexo incluido, con su precio con margen.</summary>
public sealed record clsConexoExportacion(string Nombre, string Categoria, decimal Cantidad, decimal Precio);

/// <summary>Archivo para descargar.</summary>
public sealed record clsArchivoExportado(string Nombre, byte[] Contenido, string TipoContenido);

/// <summary><c>LineaLicitacion</c>: una línea con su estado y su precio con margen (<c>null</c> mientras no tiene flete).</summary>
public sealed record clsLineaLicitacion(
    int Orden,
    int? Fila,
    string Origen,
    string Destino,
    string? TipoVehiculo,
    decimal? PesoKg,
    string? EstadoCosteo,
    decimal? PrecioFinal);
