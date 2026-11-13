namespace Cotifly.Aplicacion.Licitaciones.DTOs;

// POST /web/licitaciones/aplicar-mapeo. Lo que llega del front y lo que vuelve, en los nombres de Cotifly (la Api los
// escribe en snake_case; cada línea con los nombres de LineaLicitacionWeb). La forma de NEXO (camelCase) la arma el adaptador de la importación de NEXO.

/// <summary>
/// Entrada del caso de uso: el NIT de la licitación (opcional; con él NEXO usa el diccionario de ese cliente), el mapeo confirmado, TODAS
/// las filas de la hoja sin el encabezado (máx. 5000, el tope de NEXO) y el número de Excel de la primera fila enviada (<c>null</c> = 2, como
/// NEXO). <c>null</c> dentro de las listas es una parte mal formada (lo reporta la validación). <paramref name="NumeroEncabezados"/>: los
/// encabezados de la hoja (1 a 100), de donde empiezan las columnas derivadas, igual que en <c>sugerir-mapeo</c> (<c>null</c> = la fila más ancha).
/// </summary>
public sealed record clsEntradaAplicacionMapeo(
    string? NitCliente,
    IReadOnlyList<clsColumnaEntradaMapeo?>? Columnas,
    IReadOnlyList<IReadOnlyList<string?>?>? Filas,
    int? PrimeraFila,
    int? NumeroEncabezados = null);

/// <summary>Una columna confirmada: índice base 0, campo de NEXO (camelCase, o <c>ignorar</c>) y transformación de NEXO (vacía = <c>ninguna</c>).</summary>
public sealed record clsColumnaEntradaMapeo(int? Indice, string? Campo, string? Transformacion);

/// <summary>
/// <c>MapeoAplicado</c>: la respuesta de <c>aplicar-mapeo</c>. Los conteos de NEXO más las filas que rechazó Cotifly por su línea de negocio
/// (<see cref="FilasConErrores"/> y <see cref="TotalErrores"/> los suman), las líneas en formato Cotifly (en el orden del archivo; una fila
/// sin equivalencia de línea de negocio también vuelve aquí) y los errores por fila (los de NEXO, máx. 100 en la lista, con el campo tal como
/// lo manda NEXO; los de Cotifly, <c>LINEA_NEGOCIO_SIN_EQUIVALENCIA</c>), ordenados por fila de Excel.
/// </summary>
public sealed record clsMapeoAplicado(
    int TotalFilas,
    int FilasVacias,
    int FilasConErrores,
    int TotalErrores,
    IReadOnlyList<clsLineaMapeada> Lineas,
    IReadOnlyList<clsErrorFilaImportada> Errores);

/// <summary>
/// Un error de una fila, con los mismos nombres que el <c>400 LICITACION_INVALIDA</c> (sin <c>indice</c>: aquí la fila basta):
/// <paramref name="Fila"/> la posición de la fila en las <c>filas</c> enviadas (base 0, la <c>fila</c> de <c>LineaLicitacionWeb</c>;
/// <c>null</c> si NEXO numeró fuera de ellas), <paramref name="FilaExcel"/> el número de fila de Excel que dio NEXO (<c>primera_fila</c> +
/// posición), <paramref name="Campo"/> el campo (los de NEXO tal como los manda NEXO; <c>linea_negocio</c> en el error de Cotifly; <c>fila</c>
/// si es de toda la fila).
/// </summary>
public sealed record clsErrorFilaImportada(int? Fila, int FilaExcel, string Campo, string Codigo, string Mensaje);

/// <summary>
/// <c>LineaLicitacionImportada</c>: una fila ya traducida a la línea de licitación de Cotifly. Sus propiedades son un subconjunto de
/// <c>LineaLicitacionWeb</c> (<c>clsLineaLicitacionWebRequest</c>) con los mismos nombres y significado, MÁS <paramref name="FilaExcel"/> (el
/// número de Excel) y <paramref name="LineaNexo"/> (la línea de negocio de NEXO de la que sale): la línea va tal cual en <c>lineas[]</c> de
/// <c>POST /web/licitaciones</c>. <paramref name="LineaNegocio"/> es la de Cotifly (ADR-029); si no tiene equivalencia es <c>null</c> y la fila
/// también va a <c>errores[]</c>. Los ids de municipio van siempre en <c>null</c> (los resuelve el alta). Lo que no sale del Excel (conexos,
/// valor del levante, peso con tara) no está: el front lo completa en la revisión.
/// </summary>
public sealed record clsLineaMapeada(
    int? Fila,
    int FilaExcel,
    string? LineaNegocio,
    string? LineaNexo,
    string Origen,
    string Destino,
    int? OrigenMunicipioId,
    int? DestinoMunicipioId,
    decimal? HorasCargue,
    decimal? HorasDescargue,
    string? TipoVehiculo,
    string? Carroceria,
    string? Tipologia,
    string? ClaseViaje,
    string? ClaseContenedor,
    bool? Exclusivo,
    bool? Consolidado,
    bool? RetornaContenedor,
    string? CiudadDevolucionContenedor,
    int? CiudadDevolucionMunicipioId,
    bool? LevanteContenedorDiferente,
    string? CiudadLevanteContenedor,
    int? CiudadLevanteMunicipioId,
    decimal? PesoKg,
    decimal? ValorMercancia,
    IReadOnlyList<clsMercanciaMapeada>? Mercancias);

/// <summary>
/// <c>MercanciaImportada</c> (subconjunto de <c>PosicionCargaWeb</c>, mismos nombres): una posición de NEXO como mercancía de Cotifly:
/// código del catálogo del cubicador (el del Excel o el sugerido por la descripción), la descripción de NEXO en
/// <paramref name="Descripcion"/> y en <paramref name="NombreMercancia"/> (el mismo texto, los dos nombres del campo), la cantidad, las medidas
/// en cm (<c>null</c> las tres si NEXO no trae ninguna) y el peso POR UNIDAD en kg.
/// </summary>
public sealed record clsMercanciaMapeada(
    string? CodigoMercancia, string? Descripcion, string? NombreMercancia, int Cantidad, decimal? LargoCm, decimal? AnchoCm, decimal? AltoCm,
    decimal? PesoKg);
