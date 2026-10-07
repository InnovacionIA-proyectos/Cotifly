using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Agente;

namespace Cotifly.Aplicacion.Municipios.Services;

/// <summary>
/// Resuelve un origen o destino escrito en texto libre contra el catálogo de municipios de Nexo (HU-005.13). Determinista y sin
/// estado: quien lo usa le pasa el catálogo que devolvió <c>IClsMunicipiosProvider</c> (la herramienta <c>resolver_municipio</c> del
/// agente legado se retiró en HU-013.9, #105; el resolvedor se conserva para otros flujos, como las licitaciones). Nexo expone un
/// solo endpoint sin filtro (<c>GET /api/captura-solicitudes/municipios</c>, mapeo aprobado en
/// <c>docs/MAPEO_CAMPOS_NEXO_COTIFLY.md</c>), así que la búsqueda se hace aquí, no en Nexo.
/// </summary>
/// <remarks>
/// Se compara con <see cref="Clave"/>: sin tildes, sin mayúsculas, sin signos y con las siglas de una letra unidas
/// (<c>D.C.</c>, <c>DC</c> y <c>D C</c> son <c>dc</c>). Un país al final (<c>Medellín Antioquia Colombia</c>) se ignora. Orden de las
/// reglas; la primera que encuentra algo gana:
/// <list type="number">
/// <item><b>Exacta:</b> el texto es el municipio (<c>La Unión</c>) o el municipio y su departamento (<c>La Unión Nariño</c>,
/// <c>La Unión, Nariño</c>).</item>
/// <item><b>Nombre con pista de departamento:</b> <c>Nombre, pista</c>, <c>Nombre (pista)</c> o un departamento del catálogo al
/// final (<c>Cúcuta Norte de Santander</c>). El nombre se busca con los niveles de la regla 3 y la pista filtra por departamento;
/// si varios departamentos coinciden porque uno contiene al otro (<c>Cauca</c> y <c>Valle del Cauca</c>), gana el más largo que
/// aparece en la pista. Si la pista no coincide con ninguno, quedan todos los del nombre.</item>
/// <item><b>Por niveles sobre el texto completo</b>, el primero que encuentra algo:
///   <list type="bullet">
///   <item>nombre exacto;</item>
///   <item>palabras completas dentro del nombre (<c>Cali</c> → <c>Santiago de Cali</c>, nunca <c>Calima</c> ni
///   <c>California</c>). Si hay varias y una sola es capital de departamento (código Dane terminado en <c>001</c>), se usa esa
///   solo si el texto es el comienzo de su nombre y no es un componente genérico (<c>Cartagena</c> → <c>Cartagena de Indias</c>,
///   no <c>Cartagena del Chairá</c>; <c>Santa</c>, <c>Juan</c> o <c>Puerto</c> se preguntan, con la capital primera);</item>
///   <item>comienzo de una palabra del nombre, con 4 letras o más (<c>Barranq</c> → <c>Barranquilla</c>);</item>
///   <item><b>aproximada:</b> el texto (3 letras o más) aparece dentro del nombre. Siempre se pide confirmación, aunque sea una
///   sola: nunca se asume un municipio por parecido.</item>
///   </list></item>
/// </list>
/// Un solo resultado que no sea aproximado es <see cref="CoincidenciaMunicipio.Unica"/>; más de uno, o cualquiera aproximado,
/// <see cref="CoincidenciaMunicipio.Varias"/>. Dos municipios con el mismo nombre exacto siempre se preguntan (el desempate por
/// capital solo aplica a coincidencias por palabra). Las filas repetidas (mismo municipio, departamento y país) cuentan una vez.
/// </remarks>
public static class clsResolvedorMunicipios
{
    /// <summary>Máximo de candidatos que se devuelven para que el comercial elija.</summary>
    public const int MaximoCandidatos = 8;

    /// <summary>Largo mínimo del texto para la coincidencia aproximada.</summary>
    public const int LargoMinimoAproximada = 3;

    /// <summary>Largo mínimo del texto para la coincidencia por comienzo de palabra.</summary>
    public const int LargoMinimoPrefijo = 4;

    /// <summary>Sufijo del código Dane de las capitales de departamento.</summary>
    public const string SufijoDaneCapital = "001";

    public static clsResolucionMunicipio Resolver(string? texto, IReadOnlyList<clsMunicipio> catalogo)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        var original = texto?.Trim() ?? string.Empty;
        var filas = catalogo
            .Where(m => Clave(m.Municipio).Length > 0)
            .Select(m => new clsFila(m, Clave(m.Municipio), Clave(m.Departamento), Clave(m.Pais)))
            .DistinctBy(f => (f.Municipio, f.Departamento, f.Pais))
            .ToList();
        var paises = filas.Select(f => f.Pais).Where(p => p.Length > 0).Distinct().ToList();
        var clave = SinPais(Clave(original), paises);
        if (clave.Length == 0)
        {
            return Ninguna(original);
        }

        var exactas = filas.Where(f => f.Municipio == clave || (f.Departamento.Length > 0 && $"{f.Municipio} {f.Departamento}" == clave)).ToList();
        if (exactas.Count > 0)
        {
            return Con(original, exactas, aproximada: false);
        }

        foreach (var (nombre, pista) in Divisiones(original, clave, filas, paises))
        {
            var porNombre = Buscar(nombre, filas, desempatar: false);
            if (porNombre.Filas.Count == 0)
            {
                continue;
            }

            var conPista = FiltrarPorDepartamento(porNombre.Filas, pista);
            if (conPista.Count > 0)
            {
                return Con(original, conPista, porNombre.Aproximada);
            }

            var (desempatadas, primero) = Desempatar(porNombre.Filas, porNombre.PorPalabra, nombre);
            return Con(original, desempatadas, porNombre.Aproximada, primero);
        }

        var niveles = Buscar(clave, filas, desempatar: true);
        return Con(original, niveles.Filas, niveles.Aproximada, niveles.Primero);
    }

    /// <summary>
    /// Texto para comparar: minúsculas, sin tildes, sin signos (cada signo es un espacio), con espacios simples y con las siglas de
    /// una letra unidas (<c>D.C.</c> → <c>dc</c>).
    /// </summary>
    public static string Clave(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var descompuesto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var palabras = new List<string>();
        var actual = new StringBuilder();
        foreach (var caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(caracter))
            {
                actual.Append(caracter);
            }
            else if (actual.Length > 0)
            {
                palabras.Add(actual.ToString());
                actual.Clear();
            }
        }

        if (actual.Length > 0)
        {
            palabras.Add(actual.ToString());
        }

        // Siglas: palabras seguidas de una sola letra se unen ("d c" → "dc").
        var unidas = new List<string>(palabras.Count);
        var anteriorEsSigla = false;
        foreach (var palabra in palabras)
        {
            var letra = palabra.Length == 1 && char.IsLetter(palabra[0]);
            if (letra && anteriorEsSigla)
            {
                unidas[^1] += palabra;
            }
            else
            {
                unidas.Add(palabra);
            }

            anteriorEsSigla = letra || (anteriorEsSigla && letra);
        }

        return string.Join(' ', unidas).Normalize(NormalizationForm.FormC);
    }

    /// <summary>¿<paramref name="texto"/> (clave) contiene <paramref name="palabras"/> (clave) como palabras completas?</summary>
    public static bool ContienePalabras(string texto, string palabras) =>
        palabras.Length > 0 && $" {texto} ".Contains($" {palabras} ", StringComparison.Ordinal);

    /// <summary>
    /// Departamentos (claves) nombrados en <paramref name="pista"/>: los que aparecen como palabras completas, quedándose con el más
    /// largo cuando uno contiene al otro (<c>valle del cauca</c> gana sobre <c>cauca</c>).
    /// </summary>
    public static IReadOnlyList<string> DepartamentosNombrados(string pista, IEnumerable<string> departamentos)
    {
        ArgumentNullException.ThrowIfNull(departamentos);
        var nombrados = departamentos.Where(d => d.Length > 0 && ContienePalabras(pista, d)).Distinct().ToList();
        return nombrados.Where(d => !nombrados.Any(otro => otro.Length > d.Length && ContienePalabras(otro, d))).ToList();
    }

    private static clsBusqueda Buscar(string clave, List<clsFila> filas, bool desempatar)
    {
        var exactas = filas.Where(f => f.Municipio == clave).ToList();
        if (exactas.Count > 0)
        {
            return new clsBusqueda(exactas, false, false);
        }

        var porPalabra = filas.Where(f => ContienePalabras(f.Municipio, clave)).ToList();
        if (porPalabra.Count > 0)
        {
            var (desempatadas, primero) = desempatar ? Desempatar(porPalabra, true, clave) : (porPalabra, null);
            return new clsBusqueda(desempatadas, false, true, primero);
        }

        if (clave.Length >= LargoMinimoPrefijo)
        {
            var porPrefijo = filas.Where(f => $" {f.Municipio}".Contains($" {clave}", StringComparison.Ordinal)).ToList();
            if (porPrefijo.Count > 0)
            {
                return new clsBusqueda(porPrefijo, false, false);
            }
        }

        var aproximadas = clave.Length >= LargoMinimoAproximada
            ? filas.Where(f => f.Municipio.Contains(clave, StringComparison.Ordinal)).ToList()
            : [];
        return new clsBusqueda(aproximadas, true, false);
    }

    /// <summary>
    /// Entre varias coincidencias por palabra, la única capital de departamento (Dane <c>XX001</c>), solo si el texto es el comienzo
    /// del nombre de la capital (palabras completas) y no es un componente genérico de nombres (<see cref="ComponentesGenericos"/>):
    /// <c>Cartagena</c> → <c>Cartagena de Indias</c>; <c>Santa</c>, <c>Juan</c> o <c>Puerto</c> no. Si no aplica, se devuelven todas y
    /// la capital va primera en los candidatos.
    /// </summary>
    private static (List<clsFila> Filas, clsFila? Primero) Desempatar(List<clsFila> filas, bool porPalabra, string clave)
    {
        if (!porPalabra || filas.Count < 2)
        {
            return (filas, null);
        }

        var capitales = filas.Where(f => f.Original.CodigoDane?.Trim() is { Length: 5 } dane && dane.EndsWith(SufijoDaneCapital, StringComparison.Ordinal)).ToList();
        if (capitales.Count != 1)
        {
            return (filas, null);
        }

        var capital = capitales[0];
        var alComienzo = capital.Municipio.StartsWith(clave + " ", StringComparison.Ordinal);
        return alComienzo && !ComponentesGenericos.Contains(clave) ? ([capital], null) : (filas, capital);
    }

    /// <summary>
    /// Palabras (claves) que forman parte de muchos nombres de municipios y no identifican uno solo: nunca desempatan por capital
    /// (<c>Santa</c> no es <c>Santa Marta</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> ComponentesGenericos = new HashSet<string>(StringComparer.Ordinal)
    {
        "san", "santa", "santo", "santos", "puerto", "villa", "villa de", "el", "la", "los", "las", "nueva", "nuevo", "san jose",
        "san juan", "santiago", "san andres", "san antonio", "san carlos", "san luis", "san martin", "san pedro", "san rafael",
        "san vicente", "san bernardo", "santa ana", "santa cruz", "santa rosa", "santa maria", "santa barbara", "juan", "jose", "maria",
        "valle", "monte", "alto", "bajo", "la paz", "el carmen", "de", "del",
    };

    private static List<clsFila> FiltrarPorDepartamento(List<clsFila> filas, string pista)
    {
        var nombrados = DepartamentosNombrados(pista, filas.Select(f => f.Departamento));
        if (nombrados.Count > 0)
        {
            return filas.Where(f => nombrados.Contains(f.Departamento)).ToList();
        }

        // La pista es parte del departamento ("Valle" → "Valle del Cauca").
        return filas.Where(f => f.Departamento.Length > 0 && ContienePalabras(f.Departamento, pista)).ToList();
    }

    /// <summary>
    /// Formas de partir el texto en nombre y pista de departamento: primero por el separador (<c>,</c> o <c>(</c>), luego por un
    /// departamento del catálogo al final del texto (el más largo primero).
    /// </summary>
    private static IEnumerable<(string Nombre, string Pista)> Divisiones(string original, string clave, List<clsFila> filas, List<string> paises)
    {
        var corte = original.IndexOfAny([',', '(']);
        if (corte > 0)
        {
            var nombre = SinPais(Clave(original[..corte]), paises);
            var pista = SinPais(Clave(original[(corte + 1)..]), paises);
            if (nombre.Length > 0 && pista.Length > 0)
            {
                yield return (nombre, pista);
            }
        }

        foreach (var departamento in filas.Select(f => f.Departamento).Where(d => d.Length > 0).Distinct().OrderByDescending(d => d.Length))
        {
            if (clave.Length > departamento.Length + 1 && clave.EndsWith(" " + departamento, StringComparison.Ordinal))
            {
                yield return (clave[..^(departamento.Length + 1)], departamento);
            }
        }
    }

    private static string SinPais(string clave, List<string> paises)
    {
        foreach (var pais in paises)
        {
            if (clave.Length > pais.Length + 1 && clave.EndsWith(" " + pais, StringComparison.Ordinal))
            {
                return clave[..^(pais.Length + 1)];
            }
        }

        return clave;
    }

    private static clsResolucionMunicipio Ninguna(string texto) => new(texto, CoincidenciaMunicipio.Ninguna, [], 0);

    private static clsResolucionMunicipio Con(string texto, List<clsFila> filas, bool aproximada, clsFila? primero = null)
    {
        if (filas.Count == 0)
        {
            return Ninguna(texto);
        }

        var candidatos = filas
            .OrderBy(f => ReferenceEquals(f, primero) ? 0 : 1)
            .ThenBy(f => f.Municipio, StringComparer.Ordinal)
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

    private sealed record clsFila(clsMunicipio Original, string Municipio, string Departamento, string Pais);

    private sealed record clsBusqueda(List<clsFila> Filas, bool Aproximada, bool PorPalabra, clsFila? Primero = null);
}
