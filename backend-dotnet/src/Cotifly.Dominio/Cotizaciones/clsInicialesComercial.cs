using System.Globalization;
using System.Text;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Iniciales del comercial en el folio (ADR-011): siempre dos letras <c>A</c>–<c>Z</c> en mayúscula.
/// </summary>
/// <remarks>
/// Regla, pensada para nombres colombianos ("nombres + dos apellidos"). <c>UsuariosWeb.nombreCompleto</c> se llena en el alta
/// JIT con <c>tblSAPUsuariosApp.DESCRIPCIONUSUARIO</c>, un texto libre:
/// <list type="number">
/// <item>Se usa <c>nombreCompleto</c>; si no trae ninguna palabra con letras, <c>nombreUsuario</c> (sin el dominio si es un
/// correo: <c>dguerra@boterosoto.com.co</c> → <c>dguerra</c>); si tampoco, <see cref="SinNombre"/> (<c>XX</c>).</item>
/// <item>Del nombre completo se descarta el texto libre: lo que va entre paréntesis y lo que sigue a un guion rodeado de
/// espacios (<c>Duban Guerra - Comercial Medellín</c> y <c>Duban Guerra (Medellín)</c> → <c>Duban Guerra</c>).</item>
/// <item>Se quitan tildes y diacríticos (<c>Á</c>→<c>A</c>, <c>Ñ</c>→<c>N</c>, <c>Ü</c>→<c>U</c>) y se pasa a mayúscula.</item>
/// <item>Las palabras se separan por espacios y por cualquier signo que no sea letra, dígito, guion o apóstrofo (así
/// <c>duban.guerra</c> y <c>ana_maria</c> son dos palabras). Guion y apóstrofo no separan: <c>Pérez-Gómez</c> y
/// <c>O'Neill</c> son una sola palabra. De cada palabra quedan solo sus letras A–Z; una palabra sin letras (p. ej.
/// <c>123</c>) se descarta.</item>
/// <item>Se descartan las partículas <c>de</c>, <c>del</c>, <c>la</c>, <c>las</c>, <c>los</c> e <c>y</c>, salvo que el nombre
/// sea solo partículas. También se descarta una inicial abreviada (una letra seguida de punto, <c>M.</c>) que no sea la
/// primera palabra: <c>Lina M. Restrepo</c> → <c>LR</c>.</item>
/// <item>Con una palabra: sus dos primeras letras (<c>Carlos</c> → <c>CA</c>; si tiene una sola letra se completa con
/// <c>X</c>). Con dos: la primera letra de cada una (<c>Duban Guerra</c> → <c>DG</c>). Con tres o más: la primera letra de la
/// primera palabra (primer nombre) y la de la penúltima (primer apellido, suponiendo dos apellidos):
/// <c>Duban Guerra Castro</c> → <c>DG</c>, <c>María José Pérez Gómez</c> → <c>MP</c>,
/// <c>Juan Pablo de la Torre Restrepo</c> → <c>JT</c>.</item>
/// </list>
/// Limitaciones conocidas (ADR-011): dos nombres y un apellido (<c>María José Pérez</c> → <c>MJ</c>), apellido primero
/// (<c>Guerra Castro Duban</c> → <c>GC</c>) y apellidos compuestos sin guion (<c>Ana San Martín</c> → <c>AS</c>). Dos
/// comerciales con las mismas iniciales comparten el consecutivo del día (ver <see cref="clsFolio"/>).
/// </remarks>
public static class clsInicialesComercial
{
    /// <summary>Número de letras de las iniciales.</summary>
    public const int Largo = 2;

    /// <summary>Iniciales cuando ni el nombre completo ni el nombre de usuario traen letras.</summary>
    public const string SinNombre = "XX";

    private const char Relleno = 'X';

    private static readonly HashSet<string> _particulas = new(StringComparer.Ordinal) { "DE", "DEL", "LA", "LAS", "LOS", "Y" };

    private static readonly string[] _separadoresTextoLibre = [" - ", " – ", " — "];

    /// <summary>Iniciales del comercial según la regla de la clase.</summary>
    public static string Calcular(string? nombreCompleto, string? nombreUsuario)
    {
        var palabras = Palabras(SinTextoLibre(nombreCompleto));
        if (palabras.Count == 0)
        {
            palabras = Palabras(SinDominio(nombreUsuario));
        }

        if (palabras.Count == 0)
        {
            return SinNombre;
        }

        var utiles = palabras
            .Where((p, i) => !_particulas.Contains(p.Letras) && (i == 0 || !p.Abreviada))
            .Select(p => p.Letras)
            .ToList();
        var elegidas = utiles.Count > 0 ? utiles : palabras.Select(p => p.Letras).ToList();

        return elegidas.Count switch
        {
            1 => elegidas[0].Length >= Largo ? elegidas[0][..Largo] : elegidas[0] + Relleno,
            2 => $"{elegidas[0][0]}{elegidas[1][0]}",
            _ => $"{elegidas[0][0]}{elegidas[^2][0]}",
        };
    }

    /// <summary>¿Son exactamente dos letras <c>A</c>–<c>Z</c> en mayúscula?</summary>
    public static bool EsValida(string? iniciales) =>
        iniciales is { Length: Largo } && iniciales.All(char.IsAsciiLetterUpper);

    /// <summary>Quita lo que va entre paréntesis (o desde un paréntesis sin cerrar) y lo que sigue a " - ".</summary>
    private static string? SinTextoLibre(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return texto;
        }

        foreach (var separador in _separadoresTextoLibre)
        {
            var corte = texto.IndexOf(separador, StringComparison.Ordinal);
            if (corte >= 0)
            {
                texto = texto[..corte];
            }
        }

        var resultado = new StringBuilder(texto.Length);
        var profundidad = 0;
        foreach (var caracter in texto)
        {
            if (caracter == '(')
            {
                profundidad++;
            }
            else if (caracter == ')' && profundidad > 0)
            {
                profundidad--;
                resultado.Append(' ');
            }
            else if (profundidad == 0)
            {
                resultado.Append(caracter);
            }
        }

        return resultado.ToString();
    }

    /// <summary>La parte local de un correo (<c>dguerra@boterosoto.com.co</c> → <c>dguerra</c>); otro texto no cambia.</summary>
    private static string? SinDominio(string? nombreUsuario)
    {
        var arroba = nombreUsuario?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return arroba >= 0 ? nombreUsuario![..arroba] : nombreUsuario;
    }

    /// <summary>
    /// Palabras del texto en mayúscula ASCII (solo letras A–Z), en orden, marcando las iniciales abreviadas (una letra seguida
    /// de punto); vacía si no hay ninguna con letras.
    /// </summary>
    private static List<(string Letras, bool Abreviada)> Palabras(string? texto)
    {
        var palabras = new List<(string, bool)>();
        if (string.IsNullOrWhiteSpace(texto))
        {
            return palabras;
        }

        var actual = new StringBuilder();
        var original = 0;
        foreach (var caracter in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter) || caracter is '-' or '\'' or '’')
            {
                original++;
                var mayuscula = char.ToUpperInvariant(caracter);
                if (char.IsAsciiLetterUpper(mayuscula))
                {
                    actual.Append(mayuscula);
                }

                continue;
            }

            Cerrar(actual, palabras, abreviada: caracter == '.' && original == 1);
            original = 0;
        }

        Cerrar(actual, palabras, abreviada: false);
        return palabras;
    }

    private static void Cerrar(StringBuilder actual, List<(string, bool)> palabras, bool abreviada)
    {
        if (actual.Length > 0)
        {
            palabras.Add((actual.ToString(), abreviada && actual.Length == 1));
            actual.Clear();
        }
    }
}
