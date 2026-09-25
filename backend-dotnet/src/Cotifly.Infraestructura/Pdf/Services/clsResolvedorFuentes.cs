using PdfSharp.Fonts;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// <see cref="IFontResolver"/> de PDFsharp con Liberation Sans embebida (ADR-006): la imagen chiseled no trae fontconfig ni
/// fuentes del sistema. Cualquier familia que pida MigraDoc (Liberation Sans, Arial, Courier New, Symbol...) se resuelve a
/// Liberation Sans con su negrita y cursiva, así nada depende de una fuente instalada. Las viñetas de las listas se dibujan
/// con el carácter <c>•</c> de la misma fuente (no se usan las listas de MigraDoc, que piden la fuente Symbol).
/// </summary>
internal sealed class clsResolvedorFuentes : IFontResolver
{
    public const string Familia = "Liberation Sans";

    private static readonly Lock _candado = new();

    /// <summary>
    /// Registra el resolvedor en <see cref="GlobalFontSettings"/> (estado global de PDFsharp, una vez por proceso y antes del
    /// primer render). Si ya está registrado no hace nada.
    /// </summary>
    public static void Registrar()
    {
        lock (_candado)
        {
            if (GlobalFontSettings.FontResolver is clsResolvedorFuentes)
            {
                return;
            }

            GlobalFontSettings.FontResolver = new clsResolvedorFuentes();
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new((bold, italic) switch
        {
            (true, true) => "LiberationSans-BoldItalic",
            (true, false) => "LiberationSans-Bold",
            (false, true) => "LiberationSans-Italic",
            _ => "LiberationSans-Regular",
        });

    public byte[] GetFont(string faceName) => clsRecursosPdf.Fuente(faceName);
}
