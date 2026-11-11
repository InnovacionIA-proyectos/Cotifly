using System.Globalization;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// HU-044.9 (#292): datos metidos en otra columna. ANTES de <c>sugerir-mapeo</c> (#253) y <c>aplicar-mapeo</c> (#254), Cotifly revisa las
/// columnas de texto de la hoja y, cuando reconoce un vehículo dentro de un texto («Venezuela Mula») o un contenedor con un peso o un rango
/// («C20 de 0 a 8 Ton»), agrega columnas derivadas <b>con nombre fijo</b> al final, para que NEXO las mapee como cualquier otra. NEXO no
/// parte el texto de una celda; esto lo hace Cotifly sin tocar la celda original.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Las columnas se agregan <b>solo si al menos una fila</b> tiene un valor reconocido para esa columna, en el orden fijo
/// <see cref="EncabezadoVehiculo"/>, <see cref="EncabezadoContenedor"/>, <see cref="EncabezadoPeso"/>. Un archivo sin esos textos mantiene su
/// firma de plantilla (los mismos encabezados en el mismo orden).</item>
/// <item><b>Vehículo detectado:</b> el vehículo reconocido con el <b>diccionario de alias de NEXO</b> (<c>GET /api/catalogos/alias</c>, el mismo
/// de <c>GET /web/alias/resolver</c>). Se devuelve el texto reconocido tal cual aparece («Mula»); NEXO lo resuelve de nuevo al convertir. Sin el
/// diccionario (NEXO caído) no se detecta vehículo, sin error.</item>
/// <item><b>Contenedor detectado:</b> del patrón <c>C20</c>/<c>C40</c>/<c>20'</c>/<c>40'</c>, normalizado a <c>20'</c> o <c>40'</c>.</item>
/// <item><b>Peso detectado (kg):</b> un peso o un rango en toneladas o kg. <b>Del rango se toma el valor más alto</b> (decisión 2026-11-09:
/// «C20 de 0 a 8 Ton» → 8.000 kg); un solo valor se toma tal cual; <c>t</c>/<c>ton</c>/<c>tons</c>/<c>toneladas</c> se convierten a kg y
/// <c>kg</c> queda igual. Se emite en kg como número simple (sin separador de miles).</item>
/// <item>Si un texto no se reconoce, la celda derivada de esa fila queda vacía, sin error (el comercial la completa en la revisión). Fuera de
/// alcance: las rutas con varias paradas.</item>
/// </list>
/// </remarks>
public interface IClsColumnasDerivadasService
{
    /// <summary>
    /// Enriquece una hoja (encabezados + filas, el caso de <c>sugerir-mapeo</c>): devuelve los encabezados y las filas con las columnas
    /// derivadas al final. Si no se reconoce ninguna, devuelve los encabezados y las filas sin cambios.
    /// </summary>
    Task<clsHojaEnriquecida> EnriquecerAsync(
        Guid usuarioId, IReadOnlyList<string> encabezados, IReadOnlyList<IReadOnlyList<string>> filas, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enriquece solo las filas (el caso de <c>aplicar-mapeo</c>, que no manda encabezados): agrega las mismas columnas derivadas al final de
    /// cada fila. Si alguna fila es <c>null</c> (petición mal formada que valida <c>aplicar-mapeo</c>) o no se reconoce ninguna columna, las
    /// filas vuelven sin cambios.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<string?>?>> EnriquecerFilasAsync(
        Guid usuarioId, IReadOnlyList<IReadOnlyList<string?>?> filas, CancellationToken cancellationToken = default);
}

/// <summary>Una hoja con las columnas derivadas agregadas (o los mismos encabezados y filas si no se reconoció ninguna).</summary>
public sealed record clsHojaEnriquecida(IReadOnlyList<string> Encabezados, IReadOnlyList<IReadOnlyList<string>> Filas);

/// <inheritdoc cref="IClsColumnasDerivadasService"/>
public sealed class clsColumnasDerivadasService(IClsAliasNexoProvider alias) : IClsColumnasDerivadasService
{
    /// <summary>Nombre fijo de la columna derivada del vehículo. La firma de la plantilla depende de él: no cambia.</summary>
    public const string EncabezadoVehiculo = "Vehículo detectado";

    /// <summary>Nombre fijo de la columna derivada del contenedor.</summary>
    public const string EncabezadoContenedor = "Contenedor detectado";

    /// <summary>Nombre fijo de la columna derivada del peso (en kg).</summary>
    public const string EncabezadoPeso = "Peso detectado (kg)";

    /// <summary>El tope de columnas de NEXO (el mismo de <c>sugerir-mapeo</c> y <c>aplicar-mapeo</c>): no se agregan derivadas que lo pasen.</summary>
    public const int MaximoColumnas = 100;

    public async Task<clsHojaEnriquecida> EnriquecerAsync(
        Guid usuarioId, IReadOnlyList<string> encabezados, IReadOnlyList<IReadOnlyList<string>> filas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encabezados);
        ArgumentNullException.ThrowIfNull(filas);
        if (encabezados.Count == 0 || filas.Count == 0)
        {
            return new clsHojaEnriquecida(encabezados, filas);
        }

        var anchoBase = Math.Max(encabezados.Count, filas.Max(f => f?.Count ?? 0));
        var deteccion = await DetectarAsync(usuarioId, filas, cancellationToken);
        var columnas = ColumnasAAgregar(deteccion, anchoBase);
        if (columnas.Count == 0)
        {
            return new clsHojaEnriquecida(encabezados, filas);
        }

        var encabezadosNuevos = new List<string>(encabezados);
        encabezadosNuevos.AddRange(columnas.Select(c => c.Encabezado));
        var filasNuevas = filas.Select((f, i) => CombinarFila(f, deteccion[i], columnas, anchoBase)).ToList();
        return new clsHojaEnriquecida(encabezadosNuevos, filasNuevas);
    }

    public async Task<IReadOnlyList<IReadOnlyList<string?>?>> EnriquecerFilasAsync(
        Guid usuarioId, IReadOnlyList<IReadOnlyList<string?>?> filas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filas);
        // Una fila null es una petición mal formada: no se enriquece, para que aplicar-mapeo la rechace con FILA_INVALIDA.
        if (filas.Count == 0 || filas.Any(f => f is null))
        {
            return filas;
        }

        var limpias = filas.Select(f => (IReadOnlyList<string>)[.. f!.Select(c => c ?? string.Empty)]).ToList();
        var anchoBase = limpias.Count == 0 ? 0 : limpias.Max(f => f.Count);
        var deteccion = await DetectarAsync(usuarioId, limpias, cancellationToken);
        var columnas = ColumnasAAgregar(deteccion, anchoBase);
        if (columnas.Count == 0)
        {
            return filas;
        }

        return limpias.Select((f, i) => (IReadOnlyList<string?>?)CombinarFila(f, deteccion[i], columnas, anchoBase)).ToList();
    }

    /// <summary>La detección por fila (vehículo, contenedor, peso), con el diccionario de alias de NEXO leído una sola vez.</summary>
    private async Task<IReadOnlyList<clsFilaDetectada>> DetectarAsync(
        Guid usuarioId, IReadOnlyList<IReadOnlyList<string>> filas, CancellationToken cancellationToken)
    {
        var esVehiculo = await ResolutorVehiculoAsync(usuarioId, cancellationToken);
        return [.. filas.Select(f => DetectarFila(f, esVehiculo))];
    }

    /// <summary>
    /// El predicado "este texto es un vehículo" sobre el <b>diccionario de alias de NEXO</b> (<c>GET /api/catalogos/alias</c>, el mismo de
    /// <c>GET /web/alias/resolver</c>), con memoria por texto. Se busca en el diccionario de alias, no en el catálogo de códigos oficiales: así
    /// un número suelto de una celda («2», «3») nunca se confunde con un código de vehículo. Se exige además que el texto tenga al menos una
    /// letra (un alias de vehículo es una palabra). Si NEXO no entrega el diccionario, el predicado siempre es <c>false</c>: no se detecta
    /// vehículo y no se agrega la columna, sin error.
    /// </summary>
    private async Task<Func<string, bool>> ResolutorVehiculoAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var aliasNexo = await alias.ObtenerAsync(usuarioId, cancellationToken);
        if (aliasNexo.EsFallo)
        {
            return _ => false;
        }

        var diccionario = aliasNexo.Valor;
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        return texto =>
        {
            if (!cache.TryGetValue(texto, out var es))
            {
                es = texto.Any(char.IsLetter) && diccionario.Resolver(TipoAliasNexo.Vehiculo, texto) is not null;
                cache[texto] = es;
            }

            return es;
        };
    }

    /// <summary>Las columnas derivadas a agregar: solo las que tienen al menos un valor reconocido, en orden fijo y sin pasar el tope de NEXO.</summary>
    private static IReadOnlyList<clsColumnaDerivada> ColumnasAAgregar(IReadOnlyList<clsFilaDetectada> deteccion, int anchoBase)
    {
        var columnas = new List<clsColumnaDerivada>(3);
        if (deteccion.Any(d => d.Vehiculo is not null))
        {
            columnas.Add(new clsColumnaDerivada(EncabezadoVehiculo, d => d.Vehiculo));
        }

        if (deteccion.Any(d => d.Contenedor is not null))
        {
            columnas.Add(new clsColumnaDerivada(EncabezadoContenedor, d => d.Contenedor));
        }

        if (deteccion.Any(d => d.Peso is not null))
        {
            columnas.Add(new clsColumnaDerivada(EncabezadoPeso, d => d.Peso));
        }

        // Nunca se pasa de 100 columnas (el tope de NEXO): si no caben todas, se agregan las que quepan en el mismo orden fijo.
        var caben = Math.Max(0, MaximoColumnas - anchoBase);
        return caben >= columnas.Count ? columnas : [.. columnas.Take(caben)];
    }

    /// <summary>Una fila con las columnas derivadas al final, rellenando a <paramref name="anchoBase"/> para que los índices coincidan.</summary>
    private static IReadOnlyList<string> CombinarFila(
        IReadOnlyList<string>? fila, clsFilaDetectada deteccion, IReadOnlyList<clsColumnaDerivada> columnas, int anchoBase)
    {
        var celdas = new List<string>(anchoBase + columnas.Count);
        celdas.AddRange(fila ?? []);
        while (celdas.Count < anchoBase)
        {
            celdas.Add(string.Empty);
        }

        celdas.AddRange(columnas.Select(c => c.Valor(deteccion) ?? string.Empty));
        return celdas;
    }

    /// <summary>Reconoce el vehículo, el contenedor y el peso de una fila (el primer valor de izquierda a derecha por cada columna).</summary>
    private static clsFilaDetectada DetectarFila(IReadOnlyList<string> fila, Func<string, bool> esVehiculo)
    {
        string? vehiculo = null, contenedor = null, peso = null;
        foreach (var celda in fila)
        {
            if (string.IsNullOrWhiteSpace(celda))
            {
                continue;
            }

            vehiculo ??= DetectarVehiculo(celda, esVehiculo);
            contenedor ??= DetectarContenedor(celda);
            peso ??= DetectarPeso(celda);
        }

        return new clsFilaDetectada(vehiculo, contenedor, peso);
    }

    /// <summary>El vehículo reconocido dentro del texto (el texto reconocido tal cual aparece) o <c>null</c>: la ventana más larga que resuelve.</summary>
    private static string? DetectarVehiculo(string celda, Func<string, bool> esVehiculo)
    {
        var tokens = celda.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(RecortarPuntuacion).Where(t => t.Length > 0).Take(16).ToList();
        for (var tamano = Math.Min(3, tokens.Count); tamano >= 1; tamano--)
        {
            for (var i = 0; i + tamano <= tokens.Count; i++)
            {
                var ventana = string.Join(' ', tokens.Skip(i).Take(tamano));
                if (esVehiculo(ventana))
                {
                    return ventana;
                }
            }
        }

        return null;
    }

    /// <summary>La clase de contenedor normalizada a <c>20'</c> o <c>40'</c> del patrón C20/C40/20'/40', o <c>null</c>.</summary>
    private static string? DetectarContenedor(string celda)
    {
        var match = _contenedor.Match(celda);
        if (!match.Success)
        {
            return null;
        }

        var tamano = match.Groups["a"].Success ? match.Groups["a"].Value : match.Groups["b"].Value;
        return tamano + "'";
    }

    /// <summary>El peso en kg (el más alto del rango, tal cual si es un solo valor) o <c>null</c>.</summary>
    private static string? DetectarPeso(string celda)
    {
        decimal? maximo = null;
        foreach (Match match in _peso.Matches(celda))
        {
            var factor = match.Groups["u"].Value is { Length: > 0 } u && (u[0] is 't' or 'T') ? 1000m : 1m;
            foreach (var grupo in new[] { "n1", "n2" })
            {
                if (match.Groups[grupo].Success && ParsearNumero(match.Groups[grupo].Value) is { } valor)
                {
                    var kg = valor * factor;
                    maximo = maximo is { } m ? Math.Max(m, kg) : kg;
                }
            }
        }

        return maximo?.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Un número escrito como en Colombia: la coma es decimal; el punto es separador de miles cuando va seguido de tres dígitos (<c>8.000</c> =
    /// 8000) y decimal en otro caso (<c>4.5</c> = 4,5). <c>null</c> si no es un número.
    /// </summary>
    private static decimal? ParsearNumero(string texto)
    {
        var limpio = texto.Trim();
        if (limpio.Length == 0)
        {
            return null;
        }

        string normalizado;
        if (limpio.Contains(',', StringComparison.Ordinal))
        {
            normalizado = limpio.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }
        else if (_miles.IsMatch(limpio))
        {
            normalizado = limpio.Replace(".", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            normalizado = limpio;
        }

        return decimal.TryParse(normalizado, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var valor) ? valor : null;
    }

    /// <summary>Quita la puntuación de los extremos de un token, conservando la interna (<c>T.B</c>, <c>4.5</c>).</summary>
    private static string RecortarPuntuacion(string token)
    {
        var inicio = 0;
        var fin = token.Length;
        while (inicio < fin && !char.IsLetterOrDigit(token[inicio]))
        {
            inicio++;
        }

        while (fin > inicio && !char.IsLetterOrDigit(token[fin - 1]))
        {
            fin--;
        }

        return token[inicio..fin];
    }

    private static readonly Regex _contenedor = new(
        @"(?:\bc\s*(?<a>20|40)(?!\d)|\b(?<b>20|40)\s*(?:['’′]|(?:ft|pies|pie)\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex _peso = new(
        @"(?<n1>\d[\d.,]*)\s*(?:(?:a|al|hasta|entre|-|–|—)\s*(?<n2>\d[\d.,]*)\s*)?(?<u>kgs|kg|kilos|kilo|toneladas|tonelada|tons|ton|t)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex _miles = new(
        @"^\d{1,3}(\.\d{3})+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>La detección de una fila.</summary>
    private sealed record clsFilaDetectada(string? Vehiculo, string? Contenedor, string? Peso);

    /// <summary>Una columna derivada por agregar: su encabezado fijo y de dónde sale su valor en cada fila.</summary>
    private sealed record clsColumnaDerivada(string Encabezado, Func<clsFilaDetectada, string?> Valor);
}
