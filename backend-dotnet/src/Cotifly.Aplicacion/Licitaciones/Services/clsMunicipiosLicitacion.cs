using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Ids de municipio de Nexo de las filas de una licitación (G6, HU-013.11). El pliego trae el origen y el destino como texto; Nexo
/// los resuelve por coincidencia aproximada si no recibe <c>origenMunicipioId</c>/<c>destinoMunicipioId</c>. Por eso Cotifly los
/// resuelve antes de guardar, con el catálogo de <c>GET /web/municipios</c> y la misma regla determinista de
/// <see cref="clsResolvedorMunicipios"/>, y nunca adivina:
/// <list type="bullet">
/// <item>Si la fila ya trae el id (el frontend lo eligió en <c>GET /web/municipios</c>), se respeta y no se consulta el catálogo.</item>
/// <item>Una sola coincidencia no aproximada (<see cref="CoincidenciaMunicipio.Unica"/>): se usa su id.</item>
/// <item>Varias, o una aproximada: <c>MUNICIPIO_AMBIGUO</c> en la fila, con los candidatos en el mensaje.</item>
/// <item>Ninguna: <c>MUNICIPIO_NO_ENCONTRADO</c> en la fila.</item>
/// </list>
/// El texto de la fila no se cambia: se guarda y viaja tal como vino (recortado), junto al id.
/// </summary>
public static class clsMunicipiosLicitacion
{
    /// <summary>¿Alguna fila necesita el catálogo (trae origen o destino como texto, sin su id)?</summary>
    public static bool NecesitaCatalogo(IReadOnlyList<clsLineaLicitacionEntrada?> lineas)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        return lineas.Any(l => l?.Linea is { } linea && (PorResolver(linea.Origen, linea.OrigenMunicipioId) || PorResolver(linea.Destino, linea.DestinoMunicipioId)));
    }

    /// <summary>
    /// Las líneas con los ids resueltos (las que no necesitan nada quedan iguales) y los errores por fila de lo que no se pudo resolver.
    /// Un mismo texto se resuelve una sola vez por licitación.
    /// </summary>
    public static (IReadOnlyList<clsLineaLicitacionEntrada?> Lineas, IReadOnlyList<clsErrorFilaLicitacion> Errores) Resolver(
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas, IReadOnlyList<clsMunicipio> catalogo)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(catalogo);
        var resueltos = new Dictionary<string, clsResolucionMunicipio>(StringComparer.Ordinal);
        var errores = new List<clsErrorFilaLicitacion>();
        var salida = new List<clsLineaLicitacionEntrada?>(lineas.Count);
        for (var i = 0; i < lineas.Count; i++)
        {
            var entrada = lineas[i];
            if (entrada?.Linea is not { } linea)
            {
                salida.Add(entrada);
                continue;
            }

            var fila = entrada.Fila;
            int? Id(string campo, string texto, int? id)
            {
                if (!PorResolver(texto, id))
                {
                    return id;
                }

                var clave = texto.Trim();
                if (!resueltos.TryGetValue(clave, out var resolucion))
                {
                    resolucion = clsResolvedorMunicipios.Resolver(clave, catalogo);
                    resueltos[clave] = resolucion;
                }

                if (resolucion.Resuelto is { } municipio)
                {
                    return municipio.Id;
                }

                errores.Add(Error(i, fila, campo, clave, resolucion));
                return null;
            }

            var origen = Id("origen", linea.Origen, linea.OrigenMunicipioId);
            var destino = Id("destino", linea.Destino, linea.DestinoMunicipioId);
            salida.Add(origen == linea.OrigenMunicipioId && destino == linea.DestinoMunicipioId
                ? entrada
                : entrada with { Linea = linea with { OrigenMunicipioId = origen, DestinoMunicipioId = destino } });
        }

        return (salida, errores);
    }

    /// <summary>Texto presente y dentro del largo de Nexo (si no, ya es un error de la fila), sin id.</summary>
    private static bool PorResolver(string? texto, int? id) =>
        id is null && !string.IsNullOrWhiteSpace(texto) && texto.Trim().Length <= clsSolicitudCosteo.LargoCiudad;

    private static clsErrorFilaLicitacion Error(int indice, int? fila, string campo, string texto, clsResolucionMunicipio resolucion)
    {
        if (resolucion.Coincidencia == CoincidenciaMunicipio.Ninguna)
        {
            return new clsErrorFilaLicitacion(indice, fila, campo, clsCodigosLineaSolicitud.MunicipioNoEncontrado,
                $"El {campo} '{texto}' no está en el catálogo de municipios de Nexo (GET /web/municipios): corrígelo o envía {campo}_municipio_id.");
        }

        var candidatos = string.Join("; ", resolucion.Candidatos.Select(c => $"{c.Canonico()} (id {c.Id})"));
        var mas = resolucion.Total > resolucion.Candidatos.Count ? $" y {resolucion.Total - resolucion.Candidatos.Count} más" : string.Empty;
        return new clsErrorFilaLicitacion(indice, fila, campo, clsCodigosLineaSolicitud.MunicipioAmbiguo,
            $"El {campo} '{texto}' es ambiguo en el catálogo de municipios de Nexo (candidatos: {candidatos}{mas}): escribe el municipio con su "
            + $"departamento o envía {campo}_municipio_id.");
    }
}
