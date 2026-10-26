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
    /// <summary>Límite del archivo: 10 MB menos 64 KB (<see cref="clsArchivoExcel.TamanoMaximoBytes"/>, compartido desde HU-035.1).</summary>
    public const int TamanoMaximoBytes = clsArchivoExcel.TamanoMaximoBytes;

    public const int LargoNombreArchivo = 255;

    /// <summary>Excel limita el nombre de una hoja a 31 caracteres.</summary>
    public const int LargoHoja = 31;

    public const string TipoXlsx = clsArchivoExcel.TipoXlsx;
    public const string TipoXlsm = clsArchivoExcel.TipoXlsm;

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
            return invalido ?? new clsErrorDominio(clsCodigosError.ArchivoRequerido, clsArchivoExcel.MensajeRequerido);
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
    /// Reglas genéricas del archivo (<see cref="clsArchivoExcel.ValidarArchivo"/>; HU-035.1, #202, las sacó a un lugar neutro para el Excel
    /// de mercancías): <c>ARCHIVO_REQUERIDO</c>, <c>ARCHIVO_DEMASIADO_GRANDE</c> o <c>FORMATO_NO_SOPORTADO</c>. <c>null</c> si es aceptable.
    /// </summary>
    public static clsErrorDominio? ValidarArchivo(string? nombreArchivo, byte[]? contenido) => clsArchivoExcel.ValidarArchivo(nombreArchivo, contenido);

    /// <summary>Solo el nombre del archivo (sin rutas de cliente, con <c>\</c> o <c>/</c>), recortado.</summary>
    public static string NombreLimpio(string? nombreArchivo) => clsArchivoExcel.NombreLimpio(nombreArchivo);
}
