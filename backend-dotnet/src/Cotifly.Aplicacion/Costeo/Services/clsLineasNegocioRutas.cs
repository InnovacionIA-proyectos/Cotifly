using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Líneas de negocio de las rutas de una solicitud (HU-021.9, #136): cada ruta tiene la suya (decisión 8), así que el PDF, los textos y
/// "Mis cotizaciones" ya no la leen de la cabecera. <see cref="NombresAsync"/> lee los nombres de <c>MargenesPolitica</c> una vez por
/// línea distinta.
/// </summary>
public static class clsLineasNegocioRutas
{
    /// <summary>Nombre de cada línea de negocio de las rutas (id de <c>MargenesPolitica</c> → nombre); las que no existen no vienen.</summary>
    public static async Task<IReadOnlyDictionary<int, string>> NombresAsync(
        clsCotizacionSolicitud cotizacion, IClsMargenPoliticaRepository politicas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(politicas);
        var nombres = new Dictionary<int, string>();
        foreach (var id in cotizacion.RutasOrdenadas.Select(r => r.LineaNegocioId).OfType<int>().Distinct())
        {
            if (await politicas.ObtenerPorIdAsync(id, cancellationToken) is { } politica)
            {
                nombres[id] = politica.LineaNegocio;
            }
        }

        return nombres;
    }

    /// <summary>Nombre de la línea de negocio de <paramref name="ruta"/>, o <c>null</c> si no tiene o no existe.</summary>
    public static string? DeRuta(clsLineaSolicitud ruta, IReadOnlyDictionary<int, string> nombres)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        ArgumentNullException.ThrowIfNull(nombres);
        return ruta.LineaNegocioId is { } id ? nombres.GetValueOrDefault(id) : null;
    }

    /// <summary>
    /// La línea de negocio de TODA la solicitud: la de sus rutas si todas las que tienen línea tienen la misma (una ruta sin línea no
    /// cuenta), o <c>null</c> si tienen varias (o ninguna). Es la que imprime el PDF en el título y en el asunto estándar; con varias,
    /// cada sección del pliego lleva la de su ruta.
    /// </summary>
    public static string? Comun(clsCotizacionSolicitud cotizacion, IReadOnlyDictionary<int, string> nombres)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var distintas = cotizacion.RutasOrdenadas.Select(r => DeRuta(r, nombres)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distintas.Count == 1 ? distintas[0] : null;
    }

    /// <summary>
    /// Las líneas de negocio de la solicitud para el contexto de la IA (sin montos): la única, o las distintas en el orden de las rutas
    /// separadas por coma. <c>null</c> sin ninguna.
    /// </summary>
    public static string? ParaRedaccion(clsCotizacionSolicitud cotizacion, IReadOnlyDictionary<int, string> nombres)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var distintas = cotizacion.RutasOrdenadas.Select(r => DeRuta(r, nombres)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distintas.Count == 0 ? null : string.Join(", ", distintas);
    }
}
