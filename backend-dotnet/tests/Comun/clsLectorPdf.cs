using System.Globalization;
using UglyToad.PdfPig;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Lectura de los PDF generados con PdfPig (Apache-2.0, solo pruebas): texto por página con las palabras separadas por un
/// espacio (así "Yumbo, 19 de marzo de 2026" se compara tal cual), número de páginas, fuentes usadas, imágenes y la
/// disposición de cada letra (posición y tamaño: detecta cambios de maquetación que el texto extraído no muestra).
/// </summary>
public sealed class clsLectorPdf
{
    private clsLectorPdf(IReadOnlyList<string> paginas, IReadOnlyList<string> fuentes, IReadOnlyList<int> imagenes, IReadOnlyList<string> disposicion)
    {
        Paginas = paginas;
        Fuentes = fuentes;
        ImagenesPorPagina = imagenes;
        Disposicion = disposicion;
    }

    /// <summary>Texto de cada página (palabras separadas por un espacio).</summary>
    public IReadOnlyList<string> Paginas { get; }

    /// <summary>Todo el texto del documento.</summary>
    public string Texto => string.Join(' ', Paginas);

    /// <summary>Nombres de las fuentes de todas las letras (ej. <c>ABCDEF+LiberationSans-Bold</c>), sin repetir.</summary>
    public IReadOnlyList<string> Fuentes { get; }

    /// <summary>Imágenes en cada página.</summary>
    public IReadOnlyList<int> ImagenesPorPagina { get; }

    /// <summary>
    /// Maquetación de cada página: una línea por letra con su valor, el origen de su línea base y el tamaño de la fuente
    /// (redondeados a milésimas de punto), en el orden en que el PDF las dibuja. No incluye el nombre de la fuente porque
    /// PDFsharp le pone un prefijo de subconjunto aleatorio en cada render.
    /// </summary>
    public IReadOnlyList<string> Disposicion { get; }

    public static clsLectorPdf Leer(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var documento = PdfDocument.Open(pdf);
        var paginas = new List<string>();
        var fuentes = new HashSet<string>(StringComparer.Ordinal);
        var imagenes = new List<int>();
        var disposicion = new List<string>();
        foreach (var pagina in documento.GetPages())
        {
            paginas.Add(string.Join(' ', pagina.GetWords().Select(p => p.Text)));
            foreach (var letra in pagina.Letters)
            {
                fuentes.Add(letra.FontName ?? string.Empty);
            }

            disposicion.Add(string.Join('\n', pagina.Letters.Select(l => string.Create(
                CultureInfo.InvariantCulture, $"{l.Value}@{l.StartBaseLine.X:F3},{l.StartBaseLine.Y:F3}/{l.PointSize:F3}"))));

            imagenes.Add(pagina.GetImages().Count());
        }

        return new clsLectorPdf(paginas, [.. fuentes.Order(StringComparer.Ordinal)], imagenes, disposicion);
    }
}
