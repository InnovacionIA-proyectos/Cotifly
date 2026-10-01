using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Licitaciones.Interfaces;

/// <summary>
/// Análisis del Excel del cliente en Nexo (HU-011.1, ADR-017): reenvía el archivo a
/// <c>POST /api/captura-solicitudes/analizar-excel</c> (multipart <c>archivo</c>) con la identidad del comercial
/// (<c>X-Portal-Usuario-Id</c>). Nexo no guarda nada. Sin <c>Nexo:UrlBase</c>, Infraestructura registra uno que responde
/// <c>COSTEO_NEXO_NO_DISPONIBLE</c>. Nunca lanza por fallos de Nexo: un 400 de Nexo llega con su código
/// (<c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c>, <c>EXCEL_INVALIDO</c>, <c>EXCEL_SIN_DATOS</c>); el resto, como en el costeo.
/// </summary>
public interface IClsAnalizadorExcelNexo
{
    Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(Guid usuarioId, string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lectura local del Excel original de una licitación (HU-011.2/HU-011.3): ubica las filas con datos de una hoja con la MISMA regla
/// de detección de Nexo (<c>ExcelSolicitudesAdapter</c>: encabezado = primera fila con al menos 3 valores distintos dentro de las 15
/// primeras, subencabezado si los rótulos se repiten, celdas combinadas propagadas, filas vacías saltadas), para que el índice
/// <c>fila</c> del análisis apunte a la misma fila del archivo. También regenera el archivo con las columnas de Cotifly (#48).
/// </summary>
public interface IClsLectorExcelLicitacion
{
    /// <summary>
    /// Números de fila (base 1, los de Excel) de las filas con datos de <paramref name="hoja"/>, en orden: el elemento <c>i</c>
    /// es la fila <c>i</c> de <c>hojas[h].filas</c>. <c>EXCEL_INVALIDO</c> si no se puede leer; <c>EXCEL_SIN_DATOS</c> si la hoja
    /// no existe o no tiene encabezado.
    /// </summary>
    clsResultado<IReadOnlyList<int>> FilasDeDatos(byte[] contenido, string hoja);
}

/// <summary>
/// Exportación del Excel de una licitación con los resultados (HU-011.3): con el original, lo regenera SIN tocar sus columnas y agrega
/// a la derecha el precio final, el estado del costeo y los servicios conexos de cada línea, más una hoja de resumen; sin original,
/// arma una hoja con los campos mapeados y las mismas columnas. Nunca escribe costos de Nexo. <c>EXCEL_INVALIDO</c> o
/// <c>EXCEL_SIN_DATOS</c> si el original ya no se puede leer.
/// </summary>
public interface IClsExportadorExcelLicitacion
{
    clsResultado<byte[]> Generar(clsExportacionLicitacion datos);
}
