using System.Globalization;
using System.Text;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Limpieza de los textos del payload antes de maquetarlos. El PDF ya no se arma con HTML (ADR-006), así que no hace falta
/// escapar marcado: un <c>&lt;script&gt;</c> se imprime literal. Lo que sí se quita son los caracteres de control (salvo el
/// salto de línea), los separadores de línea Unicode y los caracteres de formato invisibles, que podrían romper la
/// maquetación o esconder texto; los tabuladores pasan a espacio.
/// </summary>
public static class clsTextoSeguro
{
    /// <summary>Texto limpio y recortado, o <c>null</c> si queda vacío.</summary>
    public static string? Limpiar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var normalizado = texto.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var sb = new StringBuilder(normalizado.Length);
        foreach (var c in normalizado)
        {
            if (c == '\n')
            {
                sb.Append(c);
                continue;
            }

            if (c == '\t')
            {
                sb.Append(' ');
                continue;
            }

            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            sb.Append(c);
        }

        var limpio = sb.ToString().Trim();
        return limpio.Length == 0 ? null : limpio;
    }

    /// <summary>Espacio de ancho cero (U+200B): punto de corte invisible para la maquetación.</summary>
    public const char CorteInvisible = '\u200B';

    /// <summary>Largo máximo de un tramo sin espacios antes de insertar un <see cref="CorteInvisible"/>.</summary>
    public const int LargoMaximoPalabra = 30;

    /// <summary>
    /// Inserta un espacio de ancho cero (U+200B) cada <paramref name="largo"/> caracteres dentro de los tramos sin espacios
    /// (un destino, una nota o un código larguísimo sin espacios). Así MigraDoc puede partir la línea dentro de la celda en
    /// vez de desbordarla. Se aplica al texto de celdas y listas, ya limpio (<see cref="Limpiar"/> quita los U+200B del payload).
    /// </summary>
    public static string PartirPalabrasLargas(string texto, int largo = LargoMaximoPalabra)
    {
        ArgumentNullException.ThrowIfNull(texto);
        ArgumentOutOfRangeException.ThrowIfLessThan(largo, 1);
        var sb = new StringBuilder(texto.Length + (texto.Length / largo));
        var tramo = 0;
        foreach (var c in texto)
        {
            if (char.IsWhiteSpace(c))
            {
                tramo = 0;
            }
            else if (tramo == largo && !char.IsLowSurrogate(c))
            {
                sb.Append(CorteInvisible);
                tramo = 0;
            }

            sb.Append(c);
            if (!char.IsWhiteSpace(c))
            {
                tramo++;
            }
        }

        return sb.ToString();
    }

    /// <summary>Texto limpio de una sola línea (los saltos de línea pasan a espacio).</summary>
    public static string? LimpiarLinea(string? texto) => Limpiar(texto)?.Replace('\n', ' ');

    /// <summary>Lista limpia: descarta los elementos que quedan vacíos.</summary>
    public static IReadOnlyList<string> LimpiarLista(IEnumerable<string?>? textos) =>
        textos is null ? [] : [.. textos.Select(Limpiar).OfType<string>()];
}
