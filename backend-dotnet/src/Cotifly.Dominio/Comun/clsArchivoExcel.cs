namespace Cotifly.Dominio.Comun;

/// <summary>
/// Reglas genéricas de un Excel subido por el comercial (HU-035.1, #202): las que nacieron con el pliego de licitaciones
/// (<c>clsArchivoLicitacion</c>, HU-011.2) y comparten el análisis del Excel de mercancías de una ruta. Sin nada propio de
/// licitaciones: presente y no vacío, tamaño máximo y extensión <c>.xlsx</c>/<c>.xlsm</c>.
/// </summary>
public static class clsArchivoExcel
{
    /// <summary>
    /// Límite del archivo: 10 MB menos 64 KB. Nexo limita a 10 MB el CUERPO completo de <c>analizar-excel</c> (archivo más el sobre
    /// multipart); con este margen un archivo aceptado por Cotifly nunca recibe un 413 de Nexo. El Excel de mercancías usa el mismo
    /// tope para que el comercial tenga una sola regla.
    /// </summary>
    public const int TamanoMaximoBytes = (10 * 1024 * 1024) - (64 * 1024);

    public const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string TipoXlsm = "application/vnd.ms-excel.sheet.macroEnabled.12";

    public const string MensajeRequerido = "Adjunta un archivo Excel (.xlsx).";
    public const string MensajeDemasiadoGrande = "El archivo supera el máximo de 9,9 MB (10 MB con el envío).";

    /// <summary>
    /// Reglas del archivo (las de <c>ExcelUpload</c> de Nexo): presente y no vacío (<c>ARCHIVO_REQUERIDO</c>), máximo
    /// <see cref="TamanoMaximoBytes"/> (<c>ARCHIVO_DEMASIADO_GRANDE</c>) y extensión <c>.xlsx</c>/<c>.xlsm</c>
    /// (<c>FORMATO_NO_SOPORTADO</c>). <c>null</c> si es aceptable.
    /// </summary>
    public static clsErrorDominio? ValidarArchivo(string? nombreArchivo, byte[]? contenido)
    {
        if (contenido is not { Length: > 0 })
        {
            return new clsErrorDominio(clsCodigosError.ArchivoRequerido, MensajeRequerido);
        }

        if (contenido.Length > TamanoMaximoBytes)
        {
            return new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande, MensajeDemasiadoGrande);
        }

        return Path.GetExtension(NombreLimpio(nombreArchivo)).ToLowerInvariant() is ".xlsx" or ".xlsm"
            ? null
            : new clsErrorDominio(clsCodigosError.FormatoNoSoportado, "Solo se aceptan archivos .xlsx o .xlsm.");
    }

    /// <summary>Solo el nombre del archivo (sin rutas de cliente, con <c>\</c> o <c>/</c>), recortado.</summary>
    public static string NombreLimpio(string? nombreArchivo) => Path.GetFileName((nombreArchivo ?? string.Empty).Replace('\\', '/')).Trim();
}
