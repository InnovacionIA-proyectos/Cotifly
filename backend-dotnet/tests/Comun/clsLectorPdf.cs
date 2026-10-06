using UglyToad.PdfPig;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Lectura de los PDF generados con PdfPig (Apache-2.0, solo pruebas): texto por página con las palabras separadas por un
/// espacio (así "Yumbo, 19 de marzo de 2026" se compara tal cual), número de páginas, fuentes usadas e imágenes.
/// </summary>
public sealed class clsLectorPdf
{
    private clsLectorPdf(IReadOnlyList<string> paginas, IReadOnlyList<string> fuentes, IReadOnlyList<int> imagenes)
    {
        Paginas = paginas;
        Fuentes = fuentes;
        ImagenesPorPagina = imagenes;
    }

    /// <summary>Texto de cada página (palabras separadas por un espacio).</summary>
    public IReadOnlyList<string> Paginas { get; }

    /// <summary>Todo el texto del documento.</summary>
    public string Texto => string.Join(' ', Paginas);

    /// <summary>Nombres de las fuentes de todas las letras (ej. <c>ABCDEF+LiberationSans-Bold</c>), sin repetir.</summary>
    public IReadOnlyList<string> Fuentes { get; }

    /// <summary>Imágenes en cada página.</summary>
    public IReadOnlyList<int> ImagenesPorPagina { get; }

    public static clsLectorPdf Leer(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var documento = PdfDocument.Open(pdf);
        var paginas = new List<string>();
        var fuentes = new HashSet<string>(StringComparer.Ordinal);
        var imagenes = new List<int>();
        foreach (var pagina in documento.GetPages())
        {
            paginas.Add(string.Join(' ', pagina.GetWords().Select(p => p.Text)));
            foreach (var letra in pagina.Letters)
            {
                fuentes.Add(letra.FontName ?? string.Empty);
            }

            imagenes.Add(pagina.GetImages().Count());
        }

        return new clsLectorPdf(paginas, [.. fuentes.Order(StringComparer.Ordinal)], imagenes);
    }
}
