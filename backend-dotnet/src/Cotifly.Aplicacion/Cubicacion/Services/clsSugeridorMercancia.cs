using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Cubicacion.DTOs;

namespace Cotifly.Aplicacion.Cubicacion.Services;

/// <summary>
/// Sugiere el tipo de mercancía del catálogo del cubicador (<c>GET /tipos-mercancia</c>) más parecido a un texto libre (HU-023.2, #157;
/// la misma lógica la usa la licitación, HU-023.5, #160). Compara sin tildes, sin mayúsculas y sin signos contra el nombre y el código de
/// cada tipo <b>activo</b>. Determinista y sin estado: no llama a nadie. Prefiere no sugerir antes que sugerir mal: el comercial siempre
/// puede escoger del catálogo.
/// </summary>
/// <remarks>
/// <para><b>Filtro de números.</b> Si el tipo tiene números en su nombre o código (<c>CONTENEDOR_20</c>), el texto debe traer exactamente
/// esos números: "Contenedor 40 pies" nunca es <c>CONTENEDOR_20</c>. Un tipo sin números no se descarta por los números del texto
/// ("Cemento en sacos de 50 kg" sigue siendo <c>CEMENTO</c>): suelen ser pesos o cantidades.</para>
/// <para><b>Parecido</b> (de 0 a 1), el mayor entre:</para>
/// <list type="bullet">
/// <item><b>Palabras</b>: coeficiente de Dice sobre las palabras con significado (sin artículos ni preposiciones). Dos palabras casan si
/// son iguales o si se parecen lo suficiente (plural, una letra de más: <see cref="ParecidoMinimoPalabra"/>). Sube a
/// <see cref="ParecidoContenido"/> más una parte proporcional al Dice cuando (a) todas las palabras del texto están en el tipo ("café" en
/// "Café en estibas") o (b) todas las palabras del tipo están en el texto <b>y</b> el tipo explica la primera palabra del texto, que en
/// español es el núcleo ("Bobinas de acero laminado" → "Bobinas de acero"). Así "Electrodomésticos en cajas" no es <c>CAJAS</c>: las cajas
/// son el empaque, no la mercancía.</item>
/// <item><b>Letras</b>: coeficiente de Dice sobre los pares de letras del texto completo (tolera errores de digitación).</item>
/// </list>
/// <para>El texto igual al nombre o al código da 1. Por debajo del parecido mínimo (<see cref="ParecidoMinimo"/> por defecto, siempre
/// mayor que 0,5) no hay sugerencia. <b>Ambigüedad:</b> si el segundo mejor tipo queda a lo sumo a la distancia del margen
/// (<see cref="MargenAmbiguedad"/> por defecto) del mejor, tampoco ("Contenedor" con "Contenedor seco" y "Contenedor refrigerado";
/// un empate exacto siempre es ambiguo).</para>
/// </remarks>
public static class clsSugeridorMercancia
{
    /// <summary>Parecido mínimo por defecto para sugerir un tipo de mercancía (configurable: <c>Cubicacion:ParecidoMinimoMercancia</c>).</summary>
    public const double ParecidoMinimo = 0.7;

    /// <summary>
    /// Margen por defecto bajo el cual dos candidatos se consideran empatados y no se sugiere ninguno (configurable:
    /// <c>Cubicacion:MargenAmbiguedadMercancia</c>).
    /// </summary>
    public const double MargenAmbiguedad = 0.05;

    /// <summary>Parecido base cuando un texto explica al otro (se le suma <c>(1 - base) × Dice</c>).</summary>
    public const double ParecidoContenido = 0.7;

    /// <summary>Parecido mínimo (por pares de letras) para que dos palabras casen.</summary>
    public const double ParecidoMinimoPalabra = 0.75;

    /// <summary>Palabras que no distinguen una mercancía de otra.</summary>
    private static readonly HashSet<string> _vacias = new(StringComparer.Ordinal)
    {
        "A", "AL", "CON", "DE", "DEL", "EL", "EN", "LA", "LAS", "LO", "LOS", "PARA", "POR", "SIN", "Y", "O", "UN", "UNA", "UNOS", "UNAS",
    };

    /// <summary>
    /// El tipo activo de <paramref name="catalogo"/> más parecido a <paramref name="texto"/> con los criterios por defecto
    /// (<see cref="ParecidoMinimo"/> y <see cref="MargenAmbiguedad"/>), o <c>null</c> si el texto está vacío, nada se parece lo
    /// suficiente o la mejor coincidencia es ambigua.
    /// </summary>
    public static clsMercanciaSugerida? Sugerir(string? texto, IEnumerable<clsTipoMercanciaCubicador> catalogo) =>
        Sugerir(texto, catalogo, ParecidoMinimo, MargenAmbiguedad);

    /// <summary>
    /// Igual que <see cref="Sugerir(string?, IEnumerable{clsTipoMercanciaCubicador})"/> con criterios propios: <paramref name="parecidoMinimo"/>
    /// (mayor que 0,5 y a lo sumo 1) y <paramref name="margenAmbiguedad"/> (de 0 a 0,5).
    /// </summary>
    public static clsMercanciaSugerida? Sugerir(
        string? texto, IEnumerable<clsTipoMercanciaCubicador> catalogo, double parecidoMinimo, double margenAmbiguedad)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parecidoMinimo, 0.5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parecidoMinimo, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(margenAmbiguedad);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(margenAmbiguedad, 0.5);
        var clave = Normalizar(texto);
        if (clave.Length == 0)
        {
            return null;
        }

        var numerosTexto = Numeros(clave);
        var candidatos = new List<clsMercanciaSugerida>();
        foreach (var tipo in catalogo)
        {
            if (tipo is not { Activo: true } || string.IsNullOrWhiteSpace(tipo.Codigo))
            {
                continue;
            }

            var nombre = Normalizar(tipo.Nombre);
            var codigo = Normalizar(tipo.Codigo);
            var numerosTipo = Numeros(nombre);
            numerosTipo.UnionWith(Numeros(codigo));
            if (numerosTipo.Count > 0 && !numerosTipo.SetEquals(numerosTexto))
            {
                continue;
            }

            var parecido = Math.Max(Parecido(clave, nombre), Parecido(clave, codigo));
            if (parecido >= parecidoMinimo && candidatos.TrueForAll(c => !string.Equals(c.Tipo.Codigo, tipo.Codigo, StringComparison.Ordinal)))
            {
                candidatos.Add(new clsMercanciaSugerida(tipo, parecido));
            }
        }

        if (candidatos.Count == 0)
        {
            return null;
        }

        candidatos.Sort((x, y) => y.Parecido.CompareTo(x.Parecido));
        return candidatos.Count > 1 && candidatos[0].Parecido - candidatos[1].Parecido <= margenAmbiguedad ? null : candidatos[0];
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

    /// <summary>
    /// Parecido (de 0 a 1) del <paramref name="texto"/> con un <paramref name="candidato"/>, los dos ya normalizados con
    /// <see cref="Normalizar"/>. No es simétrico: el texto es la descripción libre y el candidato, el nombre o el código del catálogo.
    /// </summary>
    public static double Parecido(string texto, string candidato)
    {
        ArgumentNullException.ThrowIfNull(texto);
        ArgumentNullException.ThrowIfNull(candidato);
        if (texto.Length == 0 || candidato.Length == 0)
        {
            return 0;
        }

        if (string.Equals(texto, candidato, StringComparison.Ordinal))
        {
            return 1;
        }

        return Math.Max(
            ParecidoPalabras(texto, candidato),
            DiceLetras(texto.Replace(" ", string.Empty, StringComparison.Ordinal), candidato.Replace(" ", string.Empty, StringComparison.Ordinal)));
    }

    private static HashSet<string> Numeros(string normalizado) =>
        new(normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Any(char.IsDigit)), StringComparer.Ordinal);

    private static double ParecidoPalabras(string texto, string candidato)
    {
        var palabrasTexto = Palabras(texto);
        var palabrasCandidato = Palabras(candidato);
        if (palabrasTexto.Count == 0 || palabrasCandidato.Count == 0)
        {
            return 0;
        }

        // Cada palabra del texto casa con a lo sumo una del candidato (la primera libre que se le parezca).
        var usadas = new bool[palabrasCandidato.Count];
        var casadas = 0;
        var nucleoCasado = false;
        for (var t = 0; t < palabrasTexto.Count; t++)
        {
            for (var c = 0; c < palabrasCandidato.Count; c++)
            {
                if (!usadas[c] && Casan(palabrasTexto[t], palabrasCandidato[c]))
                {
                    usadas[c] = true;
                    casadas++;
                    nucleoCasado |= t == 0;
                    break;
                }
            }
        }

        var dice = 2.0 * casadas / (palabrasTexto.Count + palabrasCandidato.Count);
        var textoExplicado = casadas == palabrasTexto.Count;
        var candidatoContenidoConNucleo = casadas == palabrasCandidato.Count && nucleoCasado;
        return textoExplicado || candidatoContenidoConNucleo ? Math.Max(dice, ParecidoContenido + ((1 - ParecidoContenido) * dice)) : dice;
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
