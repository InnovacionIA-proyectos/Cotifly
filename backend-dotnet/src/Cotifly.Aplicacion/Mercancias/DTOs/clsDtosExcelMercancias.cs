using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Aplicacion.Mercancias.DTOs;

/// <summary>
/// <c>AnalisisExcelMercancias</c> (HU-035.1, #202, ADR-024): la grilla cruda del Excel de mercancías de una ruta, leída por Cotifly sin
/// Nexo. Misma forma de <see cref="clsAnalisisExcel"/> (para que el frontend reutilice su pantalla de mapeo) más los indicadores de
/// truncado. <paramref name="TotalHojas"/> cuenta todas las hojas del libro; <paramref name="HojasTruncadas"/> dice si había más de las
/// que se leen. <paramref name="CatalogoMercanciasDisponible"/>: si se pudo leer el catálogo del cubicador para sugerir códigos.
/// </summary>
public sealed record clsAnalisisExcelMercancias(
    string Archivo,
    IReadOnlyList<clsHojaExcelMercancias> Hojas,
    int TotalHojas,
    bool HojasTruncadas,
    bool CatalogoMercanciasDisponible = false);

/// <summary>
/// Una hoja con datos del Excel de mercancías: la fila de Excel del encabezado detectado (base 1), los encabezados y las filas con datos
/// como texto con formato estable (cada una del ancho de <paramref name="Encabezados"/>), el número de fila de Excel de cada fila, el
/// total real de filas y de columnas y lo que se truncó. <paramref name="ColumnaMercancia"/>, <paramref name="ColumnaCodigoMercancia"/>
/// y <paramref name="Mercancias"/>: como en <see cref="clsHojaExcel"/> (HU-023.5), <c>null</c> si la hoja no tiene esas columnas.
/// </summary>
public sealed record clsHojaExcelMercancias(
    string Nombre,
    int FilaEncabezado,
    IReadOnlyList<string> Encabezados,
    IReadOnlyList<IReadOnlyList<string>> Filas,
    IReadOnlyList<int> FilasExcel,
    int TotalFilas,
    bool FilasTruncadas,
    int TotalColumnas,
    bool ColumnasTruncadas,
    bool CeldasRecortadas,
    string? ColumnaMercancia = null,
    string? ColumnaCodigoMercancia = null,
    IReadOnlyList<clsCodigoMercanciaFila>? Mercancias = null);
