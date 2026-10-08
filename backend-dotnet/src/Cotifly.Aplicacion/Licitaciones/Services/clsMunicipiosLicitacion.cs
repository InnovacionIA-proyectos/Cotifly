using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Ids y nombres de municipio de Nexo de las rutas de una captura (G6 de HU-013.11; HU-021.6, decisión 2: id obligatorio y copia del
/// nombre). Lo usan las licitaciones y el formulario. Nexo resuelve el texto por coincidencia aproximada si no recibe el id; por eso
/// Cotifly lo resuelve antes de guardar, con el catálogo de <c>GET /web/municipios</c> (con caché) y la misma regla determinista de
/// <see cref="clsResolvedorMunicipios"/>, y nunca adivina:
/// <list type="bullet">
/// <item>Con id y nombre, se respetan y no se consulta el catálogo (el frontend los tomó de <c>GET /web/municipios</c>).</item>
/// <item>Solo el id: el nombre se copia del catálogo; un id que no está en el catálogo es <c>MUNICIPIO_NO_ENCONTRADO</c>.</item>
/// <item>Solo el nombre: una sola coincidencia no aproximada (<see cref="CoincidenciaMunicipio.Unica"/>) da el id; varias, o una
/// aproximada, <c>MUNICIPIO_AMBIGUO</c> con los candidatos; ninguna, <c>MUNICIPIO_NO_ENCONTRADO</c>.</item>
/// </list>
/// Con <c>ciudades</c> se resuelven también la ciudad de devolución y la de levante del contenedor (captura nueva, HU-021.6); al
/// revalidar lo guardado solo el origen y el destino (las ciudades guardadas antes de HU-021 no traen id y no se les exige). El texto
/// que trae la fila no se cambia: se guarda y viaja tal como vino (recortado), junto al id.
/// </summary>
public static class clsMunicipiosLicitacion
{
    private sealed record clsCampo(
        string Nombre, string CampoId, Func<clsLineaSolicitudCosteo, string?> Texto, Func<clsLineaSolicitudCosteo, int?> Id, bool EsCiudad);

    private static readonly clsCampo[] _campos =
    [
        new("origen", "origen_municipio_id", l => l.Origen, l => l.OrigenMunicipioId, false),
        new("destino", "destino_municipio_id", l => l.Destino, l => l.DestinoMunicipioId, false),
        new("ciudad_devolucion_contenedor", "ciudad_devolucion_municipio_id", l => l.CiudadDevolucionContenedor, l => l.CiudadDevolucionMunicipioId, true),
        new("ciudad_levante_contenedor", "ciudad_levante_municipio_id", l => l.CiudadLevanteContenedor, l => l.CiudadLevanteMunicipioId, true),
    ];

    /// <summary>¿Alguna fila necesita el catálogo (un municipio con nombre y sin id, o con id y sin nombre)?</summary>
    public static bool NecesitaCatalogo(IReadOnlyList<clsLineaLicitacionEntrada?> lineas, bool ciudades = false)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        return lineas.Any(l => l?.Linea is { } linea
            && _campos.Any(c => (ciudades || !c.EsCiudad) && (PorResolver(c.Texto(linea), c.Id(linea)) || SinNombre(c.Texto(linea), c.Id(linea)))));
    }

    /// <summary>
    /// Las líneas con los ids y nombres resueltos (las que no necesitan nada quedan iguales) y los errores por fila de lo que no se
    /// pudo resolver. Un mismo texto se resuelve una sola vez por captura.
    /// </summary>
    public static (IReadOnlyList<clsLineaLicitacionEntrada?> Lineas, IReadOnlyList<clsErrorFilaLicitacion> Errores) Resolver(
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas, IReadOnlyList<clsMunicipio> catalogo, bool ciudades = false)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(catalogo);
        var resueltos = new Dictionary<string, clsResolucionMunicipio>(StringComparer.Ordinal);
        var porId = catalogo.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
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
            (int? Id, string? Texto) Resolver(clsCampo campo)
            {
                var texto = campo.Texto(linea);
                var id = campo.Id(linea);
                if (SinNombre(texto, id))
                {
                    if (porId.TryGetValue(id!.Value, out var municipio) && !string.IsNullOrWhiteSpace(municipio.Municipio))
                    {
                        return (id, municipio.Municipio.Trim());
                    }

                    errores.Add(new clsErrorFilaLicitacion(i, fila, campo.CampoId, clsCodigosLineaSolicitud.MunicipioNoEncontrado,
                        $"El id de municipio {id} de '{campo.Nombre}' no está en el catálogo de municipios de Nexo (GET /web/municipios)."));
                    return (id, texto);
                }

                if (!PorResolver(texto, id))
                {
                    return (id, texto);
                }

                var clave = texto!.Trim();
                if (!resueltos.TryGetValue(clave, out var resolucion))
                {
                    resolucion = clsResolvedorMunicipios.Resolver(clave, catalogo);
                    resueltos[clave] = resolucion;
                }

                if (resolucion.Resuelto is { } resuelto)
                {
                    return (resuelto.Id, texto);
                }

                errores.Add(Error(i, fila, campo, clave, resolucion));
                return (null, texto);
            }

            var origen = Resolver(_campos[0]);
            var destino = Resolver(_campos[1]);
            var devolucion = ciudades ? Resolver(_campos[2]) : (linea.CiudadDevolucionMunicipioId, linea.CiudadDevolucionContenedor);
            var levante = ciudades ? Resolver(_campos[3]) : (linea.CiudadLevanteMunicipioId, linea.CiudadLevanteContenedor);
            var nueva = linea with
            {
                OrigenMunicipioId = origen.Id,
                Origen = origen.Texto ?? linea.Origen,
                DestinoMunicipioId = destino.Id,
                Destino = destino.Texto ?? linea.Destino,
                CiudadDevolucionMunicipioId = devolucion.Item1,
                CiudadDevolucionContenedor = devolucion.Item2,
                CiudadLevanteMunicipioId = levante.Item1,
                CiudadLevanteContenedor = levante.Item2,
            };
            salida.Add(nueva == linea ? entrada : entrada with { Linea = nueva });
        }

        return (salida, errores);
    }

    /// <summary>Texto presente y dentro del largo de Nexo (si no, ya es un error de la fila), sin id.</summary>
    private static bool PorResolver(string? texto, int? id) =>
        id is null && !string.IsNullOrWhiteSpace(texto) && texto.Trim().Length <= clsSolicitudCosteo.LargoCiudad;

    /// <summary>Id válido sin nombre: el nombre se copia del catálogo.</summary>
    private static bool SinNombre(string? texto, int? id) => id is > 0 && string.IsNullOrWhiteSpace(texto);

    private static clsErrorFilaLicitacion Error(int indice, int? fila, clsCampo campo, string texto, clsResolucionMunicipio resolucion)
    {
        // "El origen" / "La ciudad de levante": el artículo y el género siguen al campo.
        var (sujeto, ambiguo, corrigelo) = campo.Nombre switch
        {
            "ciudad_devolucion_contenedor" => ("La ciudad de devolución", "ambigua", "corrígela"),
            "ciudad_levante_contenedor" => ("La ciudad de levante", "ambigua", "corrígela"),
            _ => ($"El {campo.Nombre}", "ambiguo", "corrígelo"),
        };
        if (resolucion.Coincidencia == CoincidenciaMunicipio.Ninguna)
        {
            return new clsErrorFilaLicitacion(indice, fila, campo.Nombre, clsCodigosLineaSolicitud.MunicipioNoEncontrado,
                $"{sujeto} '{texto}' no está en el catálogo de municipios de Nexo (GET /web/municipios): {corrigelo} o envía {campo.CampoId}.");
        }

        var candidatos = string.Join("; ", resolucion.Candidatos.Select(c => $"{c.Canonico()} (id {c.Id})"));
        var mas = resolucion.Total > resolucion.Candidatos.Count ? $" y {resolucion.Total - resolucion.Candidatos.Count} más" : string.Empty;
        return new clsErrorFilaLicitacion(indice, fila, campo.Nombre, clsCodigosLineaSolicitud.MunicipioAmbiguo,
            $"{sujeto} '{texto}' es {ambiguo} en el catálogo de municipios de Nexo (candidatos: {candidatos}{mas}): escribe el municipio con "
            + $"su departamento o envía {campo.CampoId}.");
    }
}
