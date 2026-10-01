using System.Security.Cryptography;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Excel original del pliego de una licitación (<c>dbo_cotifly.CotizacionArchivosLicitacion</c>, script 0005, HU-011.2/HU-011.3,
/// ADR-017): se guarda tal como lo subió el comercial para regenerarlo con los precios sin tocar sus columnas. Uno por cotización,
/// máximo <see cref="TamanoMaximoBytes"/>; solo lo lee el dueño de la cotización. <see cref="CreatedAt"/> lo asigna la persistencia.
/// </summary>
public sealed class clsArchivoLicitacion
{
    /// <summary>Límite del archivo (el mismo de Nexo para <c>analizar-excel</c>).</summary>
    public const int TamanoMaximoBytes = 10 * 1024 * 1024;

    public const int LargoNombreArchivo = 255;

    /// <summary>Excel limita el nombre de una hoja a 31 caracteres.</summary>
    public const int LargoHoja = 31;

    public const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string TipoXlsm = "application/vnd.ms-excel.sheet.macroEnabled.12";

    private clsArchivoLicitacion()
    {
    }

    public Guid CotizacionId { get; private set; }
    public string NombreArchivo { get; private set; } = string.Empty;
    public string TipoContenido { get; private set; } = TipoXlsx;
    public int TamanoBytes { get; private set; }

    /// <summary>SHA-256 en hexadecimal minúscula del contenido (integridad y trazabilidad).</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Hoja mapeada por el comercial (<c>hojas[h].nombre</c> del análisis).</summary>
    public string Hoja { get; private set; } = string.Empty;
    public byte[] Contenido { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Valida y crea el archivo: nombre con extensión <c>.xlsx</c> o <c>.xlsm</c> (<c>FORMATO_NO_SOPORTADO</c>), contenido no vacío
    /// (<c>ARCHIVO_REQUERIDO</c>) y de máximo <see cref="TamanoMaximoBytes"/> (<c>ARCHIVO_DEMASIADO_GRANDE</c>), y la hoja
    /// obligatoria (<c>LICITACION_INVALIDA</c>). El nombre se reduce al nombre de archivo (sin rutas) y a 255 caracteres.
    /// </summary>
    public static clsResultado<clsArchivoLicitacion> Crear(Guid cotizacionId, string? nombreArchivo, byte[]? contenido, string? hoja)
    {
        var invalido = ValidarArchivo(nombreArchivo, contenido);
        if (invalido is not null || contenido is null)
        {
            return invalido ?? new clsErrorDominio(clsCodigosError.ArchivoRequerido, "Adjunta un archivo Excel (.xlsx).");
        }

        var nombre = NombreLimpio(nombreArchivo);
        var extension = Path.GetExtension(nombre).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hoja) || hoja.Trim().Length > LargoHoja)
        {
            return new clsErrorDominio(clsCodigosError.LicitacionInvalida,
                $"El campo 'hoja' es obligatorio con el archivo y admite máximo {LargoHoja} caracteres.");
        }

        return new clsArchivoLicitacion
        {
            CotizacionId = cotizacionId,
            NombreArchivo = nombre.Length <= LargoNombreArchivo ? nombre : nombre[^LargoNombreArchivo..],
            TipoContenido = extension == ".xlsm" ? TipoXlsm : TipoXlsx,
            TamanoBytes = contenido.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(contenido)),
            Hoja = hoja.Trim(),
            Contenido = contenido,
        };
    }

    /// <summary>
    /// Reglas del archivo (las de <c>ExcelUpload</c> de Nexo): presente y no vacío (<c>ARCHIVO_REQUERIDO</c>), máximo
    /// <see cref="TamanoMaximoBytes"/> (<c>ARCHIVO_DEMASIADO_GRANDE</c>) y extensión <c>.xlsx</c>/<c>.xlsm</c>
    /// (<c>FORMATO_NO_SOPORTADO</c>). <c>null</c> si es aceptable.
    /// </summary>
    public static clsErrorDominio? ValidarArchivo(string? nombreArchivo, byte[]? contenido)
    {
        if (contenido is not { Length: > 0 })
        {
            return new clsErrorDominio(clsCodigosError.ArchivoRequerido, "Adjunta un archivo Excel (.xlsx).");
        }

        if (contenido.Length > TamanoMaximoBytes)
        {
            return new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande, $"El archivo supera {TamanoMaximoBytes / (1024 * 1024)} MB.");
        }

        return Path.GetExtension(NombreLimpio(nombreArchivo)).ToLowerInvariant() is ".xlsx" or ".xlsm"
            ? null
            : new clsErrorDominio(clsCodigosError.FormatoNoSoportado, "Solo se aceptan archivos .xlsx o .xlsm.");
    }

    /// <summary>Solo el nombre del archivo (sin rutas de cliente, con <c>\</c> o <c>/</c>), recortado.</summary>
    public static string NombreLimpio(string? nombreArchivo) => Path.GetFileName((nombreArchivo ?? string.Empty).Replace('\\', '/')).Trim();
}
