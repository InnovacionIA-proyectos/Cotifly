using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Una línea de negocio traducida a la de Cotifly (ADR-029). <paramref name="EsResuelta"/> solo si hay UNA línea de Cotifly:
/// <paramref name="LineaCotifly"/> es esa línea (la que va en <c>lineas[].linea_negocio</c>). Si no se resuelve,
/// <paramref name="LineaCotifly"/> es <c>null</c> y <paramref name="Motivo"/> dice por qué (ninguna línea la mapea, está inactiva, su mapeo no
/// es válido o NEXO la reconoce para varias; <paramref name="Candidatas"/> trae esas varias). <paramref name="LineaNexo"/> es la línea de NEXO
/// de la que sale (su forma canónica), o <c>null</c> si el texto no es ninguna de las seis de NEXO.
/// </summary>
public sealed record clsLineaNegocioTraducida(
    bool EsResuelta,
    string? LineaCotifly,
    string? LineaNexo,
    IReadOnlyList<string>? Candidatas,
    string? Motivo);

/// <summary>
/// Traducción de una línea de negocio (el texto que devuelve NEXO en <c>aplicar-mapeo</c>, o el nombre de una línea de Cotifly) a la línea de
/// negocio DE COTIFLY (ADR-029), con la misma regla de <c>normalizar-valores</c>: en una licitación cada ruta lleva la línea de Cotifly
/// que tiene margen y a NEXO viaja su <c>mapeoLineasNexo</c>, así que el front nunca hace el mapeo inverso.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><see cref="PorNombreDeCotifly"/>: un texto que ya es el nombre de una línea de Cotifly (misma regla que el alta,
/// <see cref="clsReglasMargen.MismaLinea"/>) se resuelve aquí. Inactiva o con un mapeo fuera de las seis de NEXO: sin resolver con el
/// motivo del alta.</item>
/// <item><see cref="DesdeLineaNexo"/>: una línea de NEXO, validada con la lista cerrada de la fase 0 (<see cref="clsLineasNegocioNexo"/>),
/// se lleva a la línea ACTIVA de Cotifly cuyo <c>mapeoLineasNexo</c> es esa: una → resuelta; ninguna (p. ej. <c>Licitaciones</c>) → sin
/// resolver; varias → sin resolver con las candidatas. Nunca se elige una entre varias.</item>
/// </list>
/// </remarks>
public static class clsTraductorLineaNegocio
{
    /// <summary>Nota de una línea de Cotifly sin línea en NEXO (mapeo <c>NULL</c>, p. ej. <c>Distribucion</c>).</summary>
    public const string NotaSinLineaNexo = "Línea de Cotifly sin línea en NEXO: la ruta viaja sin línea de negocio.";

    /// <summary>
    /// <see cref="PorNombreDeCotifly"/> y, si el texto no es el nombre de una línea de Cotifly, <see cref="DesdeLineaNexo"/>. Es la regla
    /// completa para un texto suelto (el <c>lineaNegocio</c> de una fila de <c>aplicar-mapeo</c>).
    /// </summary>
    public static clsLineaNegocioTraducida Traducir(string texto, IReadOnlyList<clsMargenPolitica> lineas) =>
        PorNombreDeCotifly(texto, lineas) ?? DesdeLineaNexo(texto, lineas);

    /// <summary>
    /// La línea de Cotifly con ese nombre, o <c>null</c> si el texto no es el nombre de ninguna (entonces se busca como línea de NEXO). Misma
    /// elección que el alta (<c>clsMargenResolverService</c>): la que coincide exacto sin mayúsculas y luego la primera por nombre.
    /// </summary>
    public static clsLineaNegocioTraducida? PorNombreDeCotifly(string texto, IReadOnlyList<clsMargenPolitica> lineas)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        var linea = lineas
            .Where(p => clsReglasMargen.MismaLinea(p.LineaNegocio, texto))
            .OrderBy(p => string.Equals(p.LineaNegocio, texto, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.LineaNegocio, StringComparer.Ordinal)
            .FirstOrDefault();
        if (linea is null)
        {
            return null;
        }

        if (!linea.Activa)
        {
            return SinResolver(null, clsMargenResolverService.LineaInactiva(linea.LineaNegocio).Mensaje);
        }

        if (clsMargenResolverService.LineaNexoInvalida(linea) is { } invalida)
        {
            return SinResolver(null, invalida.Mensaje);
        }

        var lineaNexo = clsLineasNegocioNexo.Canonica(linea.MapeoLineasNexo);
        return new clsLineaNegocioTraducida(true, linea.LineaNegocio, lineaNexo, null, linea.MapeoLineasNexo is null ? NotaSinLineaNexo : null);
    }

    /// <summary>
    /// La línea de NEXO (el <paramref name="texto"/>) llevada a la línea activa de Cotifly que la mapea. Fuera de las seis de NEXO: sin
    /// resolver, con la lista de las seis.
    /// </summary>
    public static clsLineaNegocioTraducida DesdeLineaNexo(string texto, IReadOnlyList<clsMargenPolitica> lineas)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        var lineaNexo = clsLineasNegocioNexo.Canonica(texto);
        if (lineaNexo is null)
        {
            return SinResolver(null,
                $"NEXO propuso '{texto}', que no es una de sus líneas de negocio ({clsLineasNegocioNexo.Lista}).");
        }

        var deCotifly = lineas
            .Where(p => p.Activa && clsLineasNegocioNexo.Canonica(p.MapeoLineasNexo) == lineaNexo)
            .Select(p => p.LineaNegocio)
            .Order(StringComparer.Ordinal)
            .ToList();
        return deCotifly.Count switch
        {
            1 => new clsLineaNegocioTraducida(true, deCotifly[0], lineaNexo, null, null),
            0 => SinResolver(lineaNexo, SinLineaDeCotifly(lineaNexo, lineas)),
            _ => new clsLineaNegocioTraducida(false, null, lineaNexo, deCotifly,
                $"NEXO la reconoce como '{lineaNexo}', que corresponde a varias líneas de Cotifly: elige una."),
        };
    }

    /// <summary>
    /// Por qué una línea de NEXO no tiene línea de Cotifly. <c>Licitaciones</c> usa el mensaje del alta
    /// (<see cref="clsMargenResolverService.LineaDesconocida"/> con <c>esLicitacion</c>): es la línea de NEXO y no da margen.
    /// </summary>
    private static string SinLineaDeCotifly(string lineaNexo, IReadOnlyList<clsMargenPolitica> lineas) =>
        lineaNexo == clsMargenResolverService.LineaNexoLicitaciones
            ? clsMargenResolverService.LineaDesconocida(lineaNexo,
                [.. lineas.Where(p => p.Activa).Select(p => p.LineaNegocio).Order(StringComparer.Ordinal)], esLicitacion: true).Mensaje
            : $"NEXO la reconoce como '{lineaNexo}', pero ninguna línea de negocio activa de Cotifly corresponde a esa línea: elígela a mano.";

    private static clsLineaNegocioTraducida SinResolver(string? lineaNexo, string motivo) =>
        new(false, null, lineaNexo, null, motivo);
}
