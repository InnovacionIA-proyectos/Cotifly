using System.Globalization;

namespace Cotifly.Dominio.Correo;

/// <summary>
/// Reglas de lo que viaja como CABECERA del correo (asunto, remitente, destinatarios, nombre del adjunto,
/// correlación). Un salto de línea en una cabecera permite inyectar otras (<c>"Hola\r\nBcc: intruso@..."</c>): nada se sanea en silencio,
/// se rechaza (ADR-023). Las usan el caso de uso (400 <c>CORREO_INVALIDO</c>), la vista previa y, como defensa en profundidad, el transporte.
/// </summary>
public static class clsCabecerasCorreo
{
    /// <summary>RFC 5321 §4.5.3.1.1: la parte local tiene hasta 64 caracteres.</summary>
    public const int LargoMaximoParteLocal = 64;

    /// <summary>RFC 5321 §4.5.3.1.2: el dominio tiene hasta 255 caracteres.</summary>
    public const int LargoMaximoDominio = 255;

    /// <summary>RFC 1035: cada etiqueta del dominio tiene hasta 63 caracteres.</summary>
    public const int LargoMaximoEtiqueta = 63;

    /// <summary>Caracteres ASCII de un <c>atom</c> (RFC 5322 §3.2.3) además de letras y dígitos.</summary>
    private const string SimbolosParteLocal = "!#$%&'*+/=?^_`{|}~-";

    /// <summary>
    /// ¿Tiene <paramref name="texto"/> algún carácter de control? Los de la categoría Unicode <c>Cc</c> (U+0000–U+001F, con CR, LF, NUL y
    /// tabulador; U+007F–U+009F, con NEL U+0085) y los separadores de línea y de párrafo (U+2028, U+2029). <c>null</c> o vacío: no.
    /// </summary>
    public static bool TieneCaracteresDeControl(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return false;
        }

        foreach (var c in texto)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ¿Es <paramref name="email"/> UNA dirección simple, tal como llega (sin recortar)? <c>parte-local@dominio</c> con:
    /// <list type="bullet">
    /// <item>como mucho <see cref="clsCorreoDestinatario.LargoMaximoEmail"/> caracteres en total;</item>
    /// <item>parte local de 1 a <see cref="LargoMaximoParteLocal"/>: letras, dígitos y <c>!#$%&amp;'*+/=?^_`{|}~-</c>, con puntos que no
    /// van al principio, al final ni seguidos (sin comillas, comentarios, espacios, comas, punto y coma ni ángulos);</item>
    /// <item>dominio de hasta <see cref="LargoMaximoDominio"/> con al menos dos etiquetas de 1 a <see cref="LargoMaximoEtiqueta"/> (letras,
    /// dígitos y guiones que no van al principio ni al final; sin literales IP).</item>
    /// </list>
    /// Las letras pueden ser no ASCII (dominios y buzones internacionalizados); los caracteres de control, de formato (ancho cero, bidi)
    /// y los separadores no.
    /// </summary>
    public static bool EsDireccionValida(string? email)
    {
        if (string.IsNullOrEmpty(email) || email.Length > clsCorreoDestinatario.LargoMaximoEmail)
        {
            return false;
        }

        var arroba = email.IndexOf('@', StringComparison.Ordinal);
        if (arroba <= 0 || arroba != email.LastIndexOf('@'))
        {
            return false;
        }

        var local = email[..arroba];
        var dominio = email[(arroba + 1)..];
        return ParteLocalValida(local) && DominioValido(dominio);
    }

    private static bool ParteLocalValida(string local)
    {
        if (local.Length is 0 or > LargoMaximoParteLocal || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in local)
        {
            if (c != '.' && !char.IsLetterOrDigit(c) && !SimbolosParteLocal.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DominioValido(string dominio)
    {
        if (dominio.Length is 0 or > LargoMaximoDominio)
        {
            return false;
        }

        var etiquetas = dominio.Split('.');
        return etiquetas.Length >= 2 && etiquetas.All(EtiquetaValida);
    }

    private static bool EtiquetaValida(string etiqueta) =>
        etiqueta.Length is > 0 and <= LargoMaximoEtiqueta
        && etiqueta[0] != '-' && etiqueta[^1] != '-'
        && etiqueta.All(c => c == '-' || char.IsLetterOrDigit(c));
}
