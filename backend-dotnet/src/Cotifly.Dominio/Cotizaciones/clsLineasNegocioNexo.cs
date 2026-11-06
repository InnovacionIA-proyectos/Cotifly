using System.Globalization;
using System.Text;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// #278 (contrato único de NEXO del 2026-11-04, DU §8): las líneas de negocio que acepta <c>POST /api/captura-solicitudes</c> en
/// <c>lineas[].lineaNegocio</c>. Son exactamente seis; NEXO también las acepta en camelCase (<c>puertoAInterior</c>), en mayúsculas o con
/// guiones, y un valor vacío es "sin línea". Cualquier otro texto lo rechaza con <c>400 LINEA_NEGOCIO_INVALIDO</c> (antes se perdía en
/// silencio). Cotifly toma el <c>MargenesPolitica.mapeoLineasNexo</c> de la línea de negocio de cada ruta, lo valida con esta lista
/// ANTES de llamar a NEXO y envía su forma canónica (<see cref="Canonica"/>: el nombre de la lista, con espacios y tildes).
/// </summary>
public static class clsLineasNegocioNexo
{
    /// <summary>La línea de devolución de contenedor (DU §5: sus horas por defecto son 1 + 1, no 4 + 4).</summary>
    public const string DevolucionContenedor = "Devolución de contenedor";

    /// <summary>Horas de cargue (y de descargue) que NEXO aplica por defecto a una línea cargada (DU §5 y §8).</summary>
    public const decimal HorasPorDefecto = 4m;

    /// <summary>Horas de cargue (y de descargue) que NEXO aplica por defecto a una devolución de contenedor (DU §5 y §8).</summary>
    public const decimal HorasPorDefectoDevolucion = 1m;

    /// <summary>
    /// Las horas por defecto de NEXO para <paramref name="lineaNegocio"/> (cualquier escritura que <see cref="Canonica"/> reconozca):
    /// <see cref="HorasPorDefectoDevolucion"/> en una devolución de contenedor y <see cref="HorasPorDefecto"/> en las demás.
    /// </summary>
    public static decimal HorasPorDefectoDe(string? lineaNegocio) =>
        string.Equals(Canonica(lineaNegocio), DevolucionContenedor, StringComparison.Ordinal) ? HorasPorDefectoDevolucion : HorasPorDefecto;

    /// <summary>Las seis líneas de negocio de NEXO, escritas como en el contrato (DU §8).</summary>
    public static readonly IReadOnlyList<string> Permitidas =
        ["Nacional", "Puerto a interior", "Interior a puerto", "Devolución de contenedor", "Andino", "Licitaciones"];

    /// <summary>
    /// Único alias propio de Cotifly: <c>DevolucionContenedor</c>, el <c>mapeoLineasNexo</c> que siembran la migración 0009 y
    /// <c>db/seeder/01_lineas_negocio.sql</c> para la línea DevolucionContenedor. No es una forma que NEXO acepte (su camelCase es
    /// <c>devolucionDeContenedor</c>, con "de"), así que sin el alias toda captura de esa línea respondería <c>LINEA_NEGOCIO_INVALIDO</c>.
    /// Como Cotifly envía siempre la forma canónica (<see cref="Canonica"/>), aceptarlo no depende de NEXO. No se agregan otras variantes.
    /// </summary>
    private static readonly (string Alias, string Linea)[] _alias = [("DevolucionContenedor", DevolucionContenedor)];

    private static readonly Dictionary<string, string> _porClave =
        Permitidas.Select(l => (Alias: l, Linea: l)).Concat(_alias).ToDictionary(a => Clave(a.Alias), a => a.Linea, StringComparer.Ordinal);

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
