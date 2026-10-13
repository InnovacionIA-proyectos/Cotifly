using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Cubicacion.DTOs;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>
/// Sugiere el tipo de mercancía del catálogo del cubicador (<c>GET /tipos-mercancia</c>) más parecido a un texto libre (HU-023.2, #157;
/// la misma lógica la usa la licitación, HU-023.5, #160). Compara sin tildes, sin mayúsculas y sin signos contra el nombre y el código de
/// cada tipo <b>activo</b>, y escoge el de mayor parecido. Determinista y sin estado: no llama a nadie.
/// </summary>
/// <remarks>
/// <para><b>Parecido</b> (de 0 a 1), el mayor entre:</para>
/// <list type="bullet">
/// <item><b>Palabras</b>: coeficiente de Dice sobre las palabras con significado (sin artículos ni preposiciones). Dos palabras casan si
/// son iguales o si se parecen lo suficiente (plural, una letra de más: <see cref="ParecidoMinimoPalabra"/>). Si todas las palabras del
/// texto más corto están en el otro ("café" en "Café en estibas"), el parecido es al menos <see cref="ParecidoContenido"/> más una parte
/// proporcional al Dice, para que entre dos tipos contenidos gane el que cubre más palabras.</item>
/// <item><b>Letras</b>: coeficiente de Dice sobre los pares de letras del texto completo (tolera errores de digitación).</item>
/// </list>
/// <para>El texto igual al nombre o al código da 1. Por debajo de <see cref="ParecidoMinimo"/> no hay sugerencia (<c>null</c>): nunca se
/// adivina una mercancía que no se parece a nada. Un empate se resuelve por el nombre más corto y luego por el código (orden ordinal).</para>
/// </remarks>
public static class clsSugeridorMercancia
{
    /// <summary>Parecido mínimo para sugerir un tipo de mercancía.</summary>
    public const double ParecidoMinimo = 0.5;

    /// <summary>Parecido base de un texto cuyas palabras están todas en el otro (se le suma <c>(1 - base) × Dice</c>).</summary>
    public const double ParecidoContenido = 0.7;

    /// <summary>Parecido mínimo (por pares de letras) para que dos palabras casen.</summary>
    public const double ParecidoMinimoPalabra = 0.75;

    /// <summary>Palabras que no distinguen una mercancía de otra.</summary>
    private static readonly HashSet<string> _vacias = new(StringComparer.Ordinal)
    {
        "A", "AL", "CON", "DE", "DEL", "EL", "EN", "LA", "LAS", "LO", "LOS", "PARA", "POR", "SIN", "Y", "O", "UN", "UNA", "UNOS", "UNAS",
    };

    /// <summary>
    /// El tipo activo de <paramref name="catalogo"/> más parecido a <paramref name="texto"/>, o <c>null</c> si el texto está vacío o
    /// ninguno llega a <see cref="ParecidoMinimo"/>.
    /// </summary>
    public static clsMercanciaSugerida? Sugerir(string? texto, IEnumerable<clsTipoMercanciaCubicador> catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        var clave = Normalizar(texto);
        if (clave.Length == 0)
        {
            return null;
        }

        clsMercanciaSugerida? mejor = null;
        foreach (var tipo in catalogo)
        {
            if (tipo is not { Activo: true } || string.IsNullOrWhiteSpace(tipo.Codigo))
            {
                continue;
            }

            var parecido = Math.Max(Parecido(clave, Normalizar(tipo.Nombre)), Parecido(clave, Normalizar(tipo.Codigo)));
            if (parecido < ParecidoMinimo)
            {
                continue;
            }

            if (mejor is null || EsMejor(parecido, tipo, mejor))
            {
                mejor = new clsMercanciaSugerida(tipo, parecido);
            }
        }

        return mejor;
    }

    /// <summary>
    /// Texto para comparar: mayúsculas, sin tildes y con cada signo (guion bajo incluido) convertido en un espacio simple
    /// ("Café en_estibas!" → <c>CAFE EN ESTIBAS</c>).
    /// </summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var resultado = new StringBuilder(texto.Length);
        var espacioPendiente = false;
        foreach (var caracter in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter))
            {
                if (espacioPendiente && resultado.Length > 0)
                {
                    resultado.Append(' ');
                }

                espacioPendiente = false;
                resultado.Append(char.ToUpperInvariant(caracter));
            }
            else
            {
                espacioPendiente = true;
            }
        }

        return resultado.ToString();
    }

    /// <summary>Parecido (de 0 a 1) entre dos textos ya normalizados con <see cref="Normalizar"/>.</summary>
    public static double Parecido(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1;
        }

        return Math.Max(ParecidoPalabras(a, b), DiceLetras(a.Replace(" ", string.Empty, StringComparison.Ordinal), b.Replace(" ", string.Empty, StringComparison.Ordinal)));
    }

    private static bool EsMejor(double parecido, clsTipoMercanciaCubicador tipo, clsMercanciaSugerida mejor)
    {
        if (parecido != mejor.Parecido)
        {
            return parecido > mejor.Parecido;
        }

        var largo = tipo.Nombre.Length.CompareTo(mejor.Tipo.Nombre.Length);
        return largo != 0 ? largo < 0 : string.CompareOrdinal(tipo.Codigo, mejor.Tipo.Codigo) < 0;
    }

    private static double ParecidoPalabras(string a, string b)
    {
        var palabrasA = Palabras(a);
        var palabrasB = Palabras(b);
        if (palabrasA.Count == 0 || palabrasB.Count == 0)
        {
            return 0;
        }

        // Cada palabra de A casa con a lo sumo una de B (la primera libre que se le parezca).
        var usadas = new bool[palabrasB.Count];
        var casadas = 0;
        foreach (var palabra in palabrasA)
        {
            for (var i = 0; i < palabrasB.Count; i++)
            {
                if (!usadas[i] && Casan(palabra, palabrasB[i]))
                {
                    usadas[i] = true;
                    casadas++;
                    break;
                }
            }
        }

        var dice = 2.0 * casadas / (palabrasA.Count + palabrasB.Count);
        return casadas == Math.Min(palabrasA.Count, palabrasB.Count) ? Math.Max(dice, ParecidoContenido + ((1 - ParecidoContenido) * dice)) : dice;
    }

    private static List<string> Palabras(string texto)
    {
        var todas = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var utiles = todas.Where(p => !_vacias.Contains(p)).ToList();
        return utiles.Count > 0 ? utiles : [.. todas];
    }

    private static bool Casan(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || (Math.Min(a.Length, b.Length) >= 4 && DiceLetras(a, b) >= ParecidoMinimoPalabra);

    /// <summary>Coeficiente de Dice sobre los pares de letras consecutivas (con repetición).</summary>
    private static double DiceLetras(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return string.Equals(a, b, StringComparison.Ordinal) ? 1 : 0;
        }

        var pares = new Dictionary<(char, char), int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var par = (a[i], a[i + 1]);
            pares[par] = pares.GetValueOrDefault(par) + 1;
        }

        var comunes = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var par = (b[i], b[i + 1]);
            if (pares.TryGetValue(par, out var cuantos) && cuantos > 0)
            {
                pares[par] = cuantos - 1;
                comunes++;
            }
        }

        return 2.0 * comunes / (a.Length - 1 + b.Length - 1);
    }
}

/// <summary>Tipo de mercancía sugerido por <see cref="clsSugeridorMercancia"/> y su parecido (de 0 a 1) con el texto.</summary>
public sealed record clsMercanciaSugerida(clsTipoMercanciaCubicador Tipo, double Parecido);
