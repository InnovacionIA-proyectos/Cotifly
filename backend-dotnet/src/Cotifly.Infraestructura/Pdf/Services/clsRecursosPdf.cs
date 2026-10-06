using System.Collections.Concurrent;
using System.Reflection;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Recursos embebidos del PDF (ver <c>Cotifly.Infraestructura.csproj</c>): Liberation Sans (SIL OFL 1.1, licencia en
/// <c>licencias/LiberationSans-OFL.txt</c> de la aplicación publicada), el logo y las imágenes por línea, copiadas de
/// <c>mcp_server/pdf/assets</c> con nombres ASCII y reducidas a JPEG de 1200 px de ancho (los PNG originales pesaban
/// ~2 MB cada uno; en el PDF se muestran a 122 mm, ~250 ppp). Se leen una vez y quedan en memoria.
/// </summary>
internal static class clsRecursosPdf
{
    public const string PrefijoFuentes = "Cotifly.Pdf.Fuentes.";
    public const string PrefijoImagenes = "Cotifly.Pdf.Imagenes.";
    public const string Logo = "logo_botero_soto.png";

    private static readonly Assembly _ensamblado = typeof(clsRecursosPdf).Assembly;
    private static readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    /// <summary>Bytes de una cara de Liberation Sans (ej. <c>LiberationSans-Bold</c>).</summary>
    public static byte[] Fuente(string cara) => Leer(PrefijoFuentes + cara + ".ttf");

    /// <summary>Bytes del logo corporativo (PNG).</summary>
    public static byte[] ImagenLogo() => Leer(PrefijoImagenes + Logo);

    /// <summary>Bytes de la imagen de una línea (clave de <c>clsTextosEstandar.ImagenPorLinea</c>, ej. <c>nacional</c>).</summary>
    public static byte[] ImagenLinea(string clave) => Leer(PrefijoImagenes + clave + ".jpg");

    /// <summary>Fuente de imagen para MigraDoc sin archivos temporales (<c>base64:</c>).</summary>
    public static string ComoFuenteMigraDoc(byte[] imagen) => "base64:" + Convert.ToBase64String(imagen);

    /// <summary>Nombres de todos los recursos embebidos del PDF (pruebas y diagnóstico).</summary>
    public static IReadOnlyList<string> Nombres() =>
        [.. _ensamblado.GetManifestResourceNames().Where(n => n.StartsWith("Cotifly.Pdf.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static byte[] Leer(string nombre) =>
        _cache.GetOrAdd(nombre, static n =>
        {
            using var flujo = _ensamblado.GetManifestResourceStream(n)
                ?? throw new InvalidOperationException($"Falta el recurso embebido del PDF '{n}'.");
            using var memoria = new MemoryStream();
            flujo.CopyTo(memoria);
            return memoria.ToArray();
        });
}
