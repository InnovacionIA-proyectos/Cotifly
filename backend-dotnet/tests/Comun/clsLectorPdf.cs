using System.Globalization;
using UglyToad.PdfPig;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Lectura de los PDF generados con PdfPig (Apache-2.0, solo pruebas): texto por página con las palabras separadas por un
/// espacio (así "Yumbo, 19 de marzo de 2026" se compara tal cual), número de páginas, fuentes usadas, imágenes, la posición
/// de cada palabra e imagen (referencia de maquetación) y la disposición de cada letra (posición y tamaño: detecta
/// cambios de maquetación que el texto extraído no muestra).
/// </summary>
public sealed class clsLectorPdf
{
    private clsLectorPdf()
    {
    }

    /// <summary>Texto de cada página (palabras separadas por un espacio).</summary>
    public IReadOnlyList<string> Paginas { get; private init; } = [];

    /// <summary>Todo el texto del documento.</summary>
    public string Texto => string.Join(' ', Paginas);

    /// <summary>Nombres de las fuentes de todas las letras (ej. <c>ABCDEF+LiberationSans-Bold</c>), sin repetir.</summary>
    public IReadOnlyList<string> Fuentes { get; private init; } = [];

    /// <summary>Imágenes en cada página.</summary>
    public IReadOnlyList<int> ImagenesPorPagina { get; private init; } = [];

    /// <summary>Palabras de cada página, en el orden de <see cref="Paginas"/>, con la esquina inferior izquierda de su caja (pt).</summary>
    public IReadOnlyList<IReadOnlyList<clsPalabraPdf>> Palabras { get; private init; } = [];

    /// <summary>Esquina inferior izquierda (pt) de cada imagen de cada página, en el orden en que se dibujan.</summary>
    public IReadOnlyList<IReadOnlyList<clsPalabraPdf>> Imagenes { get; private init; } = [];

    /// <summary>
    /// Maquetación de cada página: una línea por letra con su valor, el origen de su línea base y el tamaño de la fuente
    /// (redondeados a milésimas de punto), en el orden en que el PDF las dibuja. No incluye el nombre de la fuente porque
    /// PDFsharp le pone un prefijo de subconjunto aleatorio en cada render.
    /// </summary>
    public IReadOnlyList<string> Disposicion { get; private init; } = [];

    public static clsLectorPdf Leer(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var documento = PdfDocument.Open(pdf);
        var paginas = new List<string>();
        var fuentes = new HashSet<string>(StringComparer.Ordinal);
        var imagenes = new List<int>();
        var palabras = new List<IReadOnlyList<clsPalabraPdf>>();
        var cajasImagen = new List<IReadOnlyList<clsPalabraPdf>>();
        var disposicion = new List<string>();
        foreach (var pagina in documento.GetPages())
        {
            var deLaPagina = pagina.GetWords().Select(p => new clsPalabraPdf(p.Text, p.BoundingBox.Left, p.BoundingBox.Bottom)).ToList();
            palabras.Add(deLaPagina);
            paginas.Add(string.Join(' ', deLaPagina.Select(p => p.Texto)));
            foreach (var letra in pagina.Letters)
            {
                fuentes.Add(letra.FontName ?? string.Empty);
            }

            disposicion.Add(string.Join('\n', pagina.Letters.Select(l => string.Create(
                CultureInfo.InvariantCulture, $"{l.Value}@{l.StartBaseLine.X:F3},{l.StartBaseLine.Y:F3}/{l.PointSize:F3}"))));

            var deImagen = pagina.GetImages().Select(i => new clsPalabraPdf("[imagen]", i.BoundingBox.Left, i.BoundingBox.Bottom)).ToList();
            cajasImagen.Add(deImagen);
            imagenes.Add(deImagen.Count);
        }

        return new clsLectorPdf
        {
            Paginas = paginas,
            Fuentes = [.. fuentes.Order(StringComparer.Ordinal)],
            ImagenesPorPagina = imagenes,
            Palabras = palabras,
            Imagenes = cajasImagen,
            Disposicion = disposicion,
        };
    }

    /// <summary>
    /// Primera aparición de <paramref name="frase"/> (palabras consecutivas, comparación exacta por palabra) en el documento:
    /// página (base 1) y esquina inferior izquierda de su primera palabra. <c>null</c> si no aparece.
    /// </summary>
    public (int Pagina, double X, double Y)? Ubicar(string frase)
    {
        ArgumentNullException.ThrowIfNull(frase);
        var buscadas = frase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var p = 0; p < Palabras.Count; p++)
        {
            var palabras = Palabras[p];
            for (var i = 0; i + buscadas.Length <= palabras.Count; i++)
            {
                if (buscadas.Select((b, k) => palabras[i + k].Texto == b).All(c => c))
                {
                    return (p + 1, palabras[i].X, palabras[i].Y);
                }
            }
        }

        return null;
    }
}

/// <summary>Texto (o <c>[imagen]</c>) y esquina inferior izquierda de su caja en puntos PDF (origen abajo a la izquierda).</summary>
public sealed record clsPalabraPdf(string Texto, double X, double Y);
