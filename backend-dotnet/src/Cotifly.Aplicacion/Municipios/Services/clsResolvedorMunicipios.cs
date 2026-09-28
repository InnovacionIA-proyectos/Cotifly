using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Agente;

namespace Cotifly.Aplicacion.Municipios.Services;

/// <summary>
/// Resuelve un origen o destino escrito en texto libre contra el catálogo de municipios de Nexo (HU-005.13). Determinista y sin
/// estado: la herramienta <c>resolver_municipio</c> le pasa el catálogo que devolvió <c>IClsMunicipiosProvider</c>. Nexo expone un
/// solo endpoint sin filtro (<c>GET /api/captura-solicitudes/municipios</c>, mapeo aprobado en
/// <c>docs/MAPEO_CAMPOS_NEXO_COTIFLY.md</c>), así que la búsqueda se hace aquí, no en Nexo.
/// </summary>
/// <remarks>
/// Se compara sin tildes, sin mayúsculas y sin signos (<see cref="Clave"/>). Orden de las reglas, la primera que encuentra algo gana:
/// <list type="number">
/// <item><b>Exacta:</b> el texto es el municipio (<c>La Unión</c>) o el municipio y su departamento (<c>La Unión Nariño</c>,
/// <c>La Unión, Nariño</c>).</item>
/// <item><b>Con pista de departamento:</b> <c>Municipio, pista</c> o <c>Municipio (pista)</c> con un municipio exacto; la pista
/// filtra por departamento (<c>La Unión (Valle)</c> → Valle del Cauca). Si la pista no coincide con ninguno, quedan todos los del
/// nombre.</item>
/// <item><b>Prefijo de palabra:</b> el texto es el comienzo del nombre (<c>Cartagena</c> → <c>Cartagena de Indias</c>).</item>
/// <item><b>Aproximada:</b> el texto (3 letras o más) aparece dentro del nombre. Siempre se pide confirmación, aunque sea una
/// sola: nunca se asume un municipio por parecido.</item>
/// </list>
/// Una sola coincidencia de las reglas 1 a 3 es <see cref="CoincidenciaMunicipio.Unica"/>; más de una, o cualquiera de la regla 4,
/// <see cref="CoincidenciaMunicipio.Varias"/>. Las filas repetidas (mismo municipio, departamento y país) cuentan una vez.
/// </remarks>
public static class clsResolvedorMunicipios
{
    /// <summary>Máximo de candidatos que se devuelven para que el comercial elija.</summary>
    public const int MaximoCandidatos = 8;

    /// <summary>Largo mínimo del texto para la coincidencia aproximada.</summary>
    public const int LargoMinimoAproximada = 3;

    public static clsResolucionMunicipio Resolver(string? texto, IReadOnlyList<clsMunicipio> catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        var original = texto?.Trim() ?? string.Empty;
        var clave = Clave(original);
        if (clave.Length == 0)
        {
            return new clsResolucionMunicipio(original, CoincidenciaMunicipio.Ninguna, [], 0);
        }

        var filas = catalogo
            .Where(m => Clave(m.Municipio).Length > 0)
            .Select(m => new clsFila(m, Clave(m.Municipio), Clave(m.Departamento), Clave(m.Pais)))
            .DistinctBy(f => (f.Municipio, f.Departamento, f.Pais))
            .ToList();

        var exactas = filas.Where(f => f.Municipio == clave || (f.Departamento.Length > 0 && $"{f.Municipio} {f.Departamento}" == clave)).ToList();
        if (exactas.Count > 0)
        {
            return Con(original, exactas, aproximada: false);
        }

        if (Separar(original) is { } separado)
        {
            var porNombre = filas.Where(f => f.Municipio == separado.Municipio).ToList();
            if (porNombre.Count > 0)
            {
                var conPista = porNombre.Where(f => f.Departamento.Length > 0 && (ContienePalabras(f.Departamento, separado.Pista) || ContienePalabras(separado.Pista, f.Departamento))).ToList();
                return Con(original, conPista.Count > 0 ? conPista : porNombre, aproximada: false);
            }
        }

        var porPrefijo = filas.Where(f => f.Municipio.StartsWith(clave + " ", StringComparison.Ordinal)).ToList();
        if (porPrefijo.Count > 0)
        {
            return Con(original, porPrefijo, aproximada: false);
        }

        var aproximadas = clave.Length >= LargoMinimoAproximada
            ? filas.Where(f => f.Municipio.Contains(clave, StringComparison.Ordinal)).ToList()
            : [];
        return Con(original, aproximadas, aproximada: true);
    }

    /// <summary>Texto para comparar: minúsculas, sin tildes, sin signos (cada signo es un espacio) y con espacios simples.</summary>
    public static string Clave(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var descompuesto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var limpio = new StringBuilder(descompuesto.Length);
        var espacio = false;
        foreach (var caracter in descompuesto)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(caracter);
            if (categoria == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter))
            {
                if (espacio && limpio.Length > 0)
                {
                    limpio.Append(' ');
                }

                limpio.Append(caracter);
                espacio = false;
            }
            else
            {
                espacio = true;
            }
        }

        return limpio.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>¿<paramref name="texto"/> (clave) contiene <paramref name="palabras"/> (clave) como palabras completas?</summary>
    public static bool ContienePalabras(string texto, string palabras) =>
        palabras.Length > 0 && $" {texto} ".Contains($" {palabras} ", StringComparison.Ordinal);

    private static clsResolucionMunicipio Con(string texto, List<clsFila> filas, bool aproximada)
    {
        if (filas.Count == 0)
        {
            return new clsResolucionMunicipio(texto, CoincidenciaMunicipio.Ninguna, [], 0);
        }

        var candidatos = filas
            .OrderBy(f => f.Municipio, StringComparer.Ordinal)
            .ThenBy(f => f.Departamento, StringComparer.Ordinal)
            .Take(MaximoCandidatos)
            .Select(f => Resuelto(f.Original))
            .ToList();
        var coincidencia = filas.Count == 1 && !aproximada ? CoincidenciaMunicipio.Unica : CoincidenciaMunicipio.Varias;
        return new clsResolucionMunicipio(texto, coincidencia, candidatos, filas.Count);
    }

    private static clsMunicipioResuelto Resuelto(clsMunicipio municipio) => new(
        municipio.Id,
        municipio.Municipio!.Trim(),
        Limpio(municipio.Departamento),
        Limpio(municipio.Pais),
        Limpio(municipio.CodigoDane));

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary><c>Municipio, pista</c> o <c>Municipio (pista)</c> → las dos claves; null si el texto no trae separador.</summary>
    private static (string Municipio, string Pista)? Separar(string texto)
    {
        var corte = texto.IndexOfAny([',', '(']);
        if (corte <= 0)
        {
            return null;
        }

        var municipio = Clave(texto[..corte]);
        var pista = Clave(texto[(corte + 1)..]);
        return municipio.Length > 0 && pista.Length > 0 ? (municipio, pista) : null;
    }

    private sealed record clsFila(clsMunicipio Original, string Municipio, string Departamento, string Pais);
}
