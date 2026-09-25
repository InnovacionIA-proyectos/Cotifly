namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// URL pública de un PDF (<c>pdf_url</c>): <c>{Pdf:BaseUrl}/pdfs/{archivo}</c>; sin <c>Pdf:BaseUrl</c> queda relativa
/// (<c>/pdfs/PCN-...pdf</c>) y la sirve <c>GET /pdfs/{archivo}</c>, que exige sesión.
/// </summary>
public static class clsUrlPdf
{
    /// <summary>URL del archivo <paramref name="nombre"/> (solo el nombre, nunca una ruta).</summary>
    public static string Construir(string? baseUrl, string nombre)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombre);
        var raiz = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return $"{raiz}/pdfs/{Uri.EscapeDataString(nombre)}";
    }

    /// <summary>URL a partir de la referencia guardada en <c>Cotizaciones.pdfPath</c>, o <c>null</c> si no hay PDF.</summary>
    public static string? DesdeReferencia(string? baseUrl, string? pdfPath)
    {
        var nombre = NombreArchivo(pdfPath);
        return nombre is null ? null : Construir(baseUrl, nombre);
    }

    /// <summary>Nombre del archivo de una referencia (<c>/data/pdfs/PCN-1.pdf</c> → <c>PCN-1.pdf</c>), o <c>null</c>.</summary>
    public static string? NombreArchivo(string? pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            return null;
        }

        var nombre = Path.GetFileName(pdfPath.Trim().Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(nombre) ? null : nombre;
    }
}
