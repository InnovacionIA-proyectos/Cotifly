using System.Globalization;
using System.Text;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// #278 (contrato único de NEXO del 2026-11-04, DU §8): las líneas de negocio que acepta <c>POST /api/captura-solicitudes</c> en
/// <c>lineas[].lineaNegocio</c>. Son exactamente seis; NEXO también las acepta en camelCase (<c>puertoAInterior</c>), en mayúsculas o con
/// guiones, y un valor vacío es "sin línea". Cualquier otro texto lo rechaza con <c>400 LINEA_NEGOCIO_INVALIDO</c> (antes se perdía en
/// silencio). Cotifly envía el <c>MargenesPolitica.mapeoLineasNexo</c> de la línea de negocio de cada ruta y lo valida con esta lista
/// ANTES de llamar a NEXO.
/// </summary>
public static class clsLineasNegocioNexo
{
    /// <summary>Las seis líneas de negocio de NEXO, escritas como en el contrato (DU §8).</summary>
    public static readonly IReadOnlyList<string> Permitidas =
        ["Nacional", "Puerto a interior", "Interior a puerto", "Devolución de contenedor", "Andino", "Licitaciones"];

    private static readonly Dictionary<string, string> _porClave =
        Permitidas.ToDictionary(Clave, l => l, StringComparer.Ordinal);

    /// <summary>
    /// La línea de NEXO equivalente a <paramref name="valor"/>, escrita como en <see cref="Permitidas"/>; <c>null</c> si viene vacío o no
    /// es una de las seis. La equivalencia ignora tildes, mayúsculas, espacios, guiones y <c>_</c> (<c>PuertoAInterior</c>,
    /// <c>PUERTO-A-INTERIOR</c> y <c>Puerto a interior</c> son la misma).
    /// </summary>
    public static string? Canonica(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : _porClave.GetValueOrDefault(Clave(valor));

    /// <summary>¿NEXO aceptaría <paramref name="valor"/>? Vacío o <c>null</c> (sin línea) sí; si no, debe ser una de las seis.</summary>
    public static bool EsValida(string? valor) => string.IsNullOrWhiteSpace(valor) || Canonica(valor) is not null;

    /// <summary>Texto para los mensajes: las seis separadas por coma.</summary>
    public static string Lista => string.Join(", ", Permitidas);

    private static string Clave(string valor)
    {
        var descompuesto = valor.Normalize(NormalizationForm.FormD);
        var clave = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))
            {
                clave.Append(char.ToLowerInvariant(c));
            }
        }

        return clave.ToString();
    }
}
