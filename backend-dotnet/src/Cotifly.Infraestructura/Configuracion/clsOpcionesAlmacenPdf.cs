using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Almacén de los PDFs (sección <c>Pdf</c>, la misma de <c>clsOpcionesPdf</c> de Aplicación, que solo lleva <c>BaseUrl</c>).
/// Port de <c>pdf_storage_modo</c> / <c>pdf_storage_dir</c> de <c>shared/config.py</c>.
/// </summary>
public sealed class clsOpcionesAlmacenPdf
{
    public const string Seccion = "Pdf";
    public const string ModoVolumen = "volumen";
    public const string ModoS3 = "s3";

    /// <summary><c>volumen</c> (único implementado). <c>s3</c> queda diferido (ADR-012) y no arranca.</summary>
    public string? StorageModo { get; set; } = ModoVolumen;

    /// <summary>Carpeta de los PDFs (en la imagen, <c>/data/pdfs</c>, escribible por el usuario <c>app</c>).</summary>
    public string StorageDir { get; set; } = "/data/pdfs";

    /// <summary><see cref="StorageModo"/> sin espacios y en minúsculas; vacío equivale a <c>volumen</c>.</summary>
    public string ModoNormalizado => string.IsNullOrWhiteSpace(StorageModo) ? ModoVolumen : StorageModo.Trim().ToLowerInvariant();
}

/// <summary>Reglas de <see cref="clsOpcionesAlmacenPdf"/>: solo el modo volumen, con carpeta obligatoria.</summary>
public sealed class clsValidadorOpcionesAlmacenPdf : IValidateOptions<clsOpcionesAlmacenPdf>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesAlmacenPdf options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var prefijo = clsOpcionesAlmacenPdf.Seccion + ":";

        if (options.ModoNormalizado == clsOpcionesAlmacenPdf.ModoS3)
        {
            return ValidateOptionsResult.Fail($"'{prefijo}StorageModo=s3' no está implementado en el backend .NET (diferido, ADR-012): use '{clsOpcionesAlmacenPdf.ModoVolumen}'.");
        }

        if (options.ModoNormalizado != clsOpcionesAlmacenPdf.ModoVolumen)
        {
            return ValidateOptionsResult.Fail($"'{prefijo}StorageModo' no es válido: use '{clsOpcionesAlmacenPdf.ModoVolumen}'.");
        }

        return string.IsNullOrWhiteSpace(options.StorageDir)
            ? ValidateOptionsResult.Fail($"'{prefijo}StorageDir' es obligatorio.")
            : ValidateOptionsResult.Success;
    }
}
