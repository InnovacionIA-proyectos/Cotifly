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

    /// <summary>HU-044.7 (#290): el Excel se leyó con <c>analizar-excel</c> (flujo de hoy; la exportación escribe sobre el original).</summary>
    public const string OrigenAnalizarExcel = "analizar_excel";

    /// <summary>HU-044.7 (#290): el Excel lo leyó la IA de NEXO (<c>leer-estructura</c>); la exportación arma un Excel nuevo.</summary>
    public const string OrigenLeerEstructura = "leer_estructura";

    /// <summary>Largo de columna de <see cref="OrigenLectura"/> en la base (el más largo de los dos valores, con holgura).</summary>
    public const int LargoOrigenLectura = 20;

    private clsArchivoLicitacion()
    {
    }

    public Guid CotizacionId { get; private set; }
    public string NombreArchivo { get; private set; } = string.Empty;
    public string TipoContenido { get; private set; } = TipoXlsx;
    public int TamanoBytes { get; private set; }

    /// <summary>SHA-256 en hexadecimal minúscula del contenido (integridad y trazabilidad).</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Hoja mapeada por el comercial (<c>hojas[h].nombre</c> del análisis; con <c>leer_estructura</c>, el nombre de la tabla).</summary>
    public string Hoja { get; private set; } = string.Empty;
    public byte[] Contenido { get; private set; } = [];

    /// <summary>
    /// HU-044.7 (#290): cómo se leyó este Excel (<c>analizar_excel</c> o <c>leer_estructura</c>, <see cref="clsOrigenLectura"/>). Decide cómo
    /// se exporta: con <c>leer_estructura</c> los precios NO se escriben sobre el original, sino en una hoja «Licitación» nueva.
    /// </summary>
    public string OrigenLectura { get; private set; } = OrigenAnalizarExcel;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Valida y crea el archivo: nombre con extensión <c>.xlsx</c> o <c>.xlsm</c> (<c>FORMATO_NO_SOPORTADO</c>), contenido no vacío
    /// (<c>ARCHIVO_REQUERIDO</c>) y de máximo <see cref="TamanoMaximoBytes"/> (<c>ARCHIVO_DEMASIADO_GRANDE</c>), y la hoja
    /// obligatoria (<c>LICITACION_INVALIDA</c>). El nombre se reduce al nombre de archivo (sin rutas) y a 255 caracteres.
    /// </summary>
    public static clsResultado<clsArchivoLicitacion> Crear(
        Guid cotizacionId, string? nombreArchivo, byte[]? contenido, string? hoja, string? origenLectura = null)
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

        // HU-044.7 (#290): el caso de uso ya validó origen_lectura antes de llamar aquí; vacío cae en analizar_excel (el flujo de hoy).
        var origen = string.IsNullOrWhiteSpace(origenLectura) ? OrigenAnalizarExcel : origenLectura.Trim();
        if (origen is not (OrigenAnalizarExcel or OrigenLeerEstructura))
        {
            return new clsErrorDominio(clsCodigosError.LicitacionInvalida,
                $"El campo 'origen_lectura' debe ser '{OrigenAnalizarExcel}' o '{OrigenLeerEstructura}'.");
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
            OrigenLectura = origen,
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
