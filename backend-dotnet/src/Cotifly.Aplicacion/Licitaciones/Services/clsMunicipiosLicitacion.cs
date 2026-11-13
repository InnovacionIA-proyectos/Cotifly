using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Ids y nombres de municipio de Nexo de las rutas (id obligatorio y copia del nombre). Lo usan
/// las licitaciones y el formulario, con el catálogo de <c>GET /web/municipios</c> (con caché) y la regla determinista de
/// <see cref="clsResolvedorMunicipios"/>. Nunca adivina. Dos modos:
/// <list type="bullet">
/// <item><b>Captura nueva</b> (<c>captura = true</c>): origen, destino y las dos ciudades del contenedor. Con id, el id debe estar en el
/// catálogo (si no, <c>MUNICIPIO_INEXISTENTE</c> en el campo del id) y el nombre SIEMPRE se copia del catálogo; un nombre distinto que
/// mande el cliente se ignora y queda en <c>avisos</c>. Sin id, el nombre se resuelve: una sola coincidencia no aproximada
/// (<see cref="CoincidenciaMunicipio.Unica"/>) da el id y el nombre del catálogo; varias, o una aproximada, <c>MUNICIPIO_AMBIGUO</c>
/// con los candidatos; ninguna, <c>MUNICIPIO_NO_ENCONTRADO</c>.</item>
/// <item><b>Revalidación de lo guardado</b> (<c>captura = false</c>, reintento y re-solicitud): solo el origen y el destino sin id se
/// resuelven por el nombre guardado (que no se cambia); un id guardado se respeta sin consultar el catálogo.</item>
/// </list>
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

    /// <summary>
    /// ¿Alguna fila necesita el catálogo? En una captura nueva, cualquier municipio que venga (por id o por nombre: el nombre siempre
    /// sale del catálogo); al revalidar, solo un origen o destino con nombre y sin id.
    /// </summary>
    public static bool NecesitaCatalogo(IReadOnlyList<clsLineaLicitacionEntrada?> lineas, bool captura = false)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        return lineas.Any(l => l?.Linea is { } linea && _campos.Any(c => captura
            ? c.Id(linea) is > 0 || PorResolver(c.Texto(linea), null)
            : !c.EsCiudad && PorResolver(c.Texto(linea), c.Id(linea))));
    }

    /// <summary>
    /// Las líneas con los ids y nombres resueltos (las que no necesitan nada quedan iguales) y los errores por fila de lo que no se
    /// pudo resolver. Un mismo texto se resuelve una sola vez. En <paramref name="avisos"/> (si viene) quedan los nombres que mandó el
    /// cliente y se reemplazaron por los del catálogo.
    /// </summary>
    public static (IReadOnlyList<clsLineaLicitacionEntrada?> Lineas, IReadOnlyList<clsErrorFilaLicitacion> Errores) Resolver(
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas, IReadOnlyList<clsMunicipio> catalogo, bool captura = false, ICollection<string>? avisos = null)
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
            string? NombreCatalogo(int id) =>
                porId.TryGetValue(id, out var m) && !string.IsNullOrWhiteSpace(m.Municipio) ? m.Municipio.Trim() : null;

            (int? Id, string? Texto) Resolver(clsCampo campo)
            {
                var texto = campo.Texto(linea);
                var id = campo.Id(linea);
                if (captura && id is > 0)
                {
                    if (NombreCatalogo(id.Value) is not { } nombre)
                    {
                        errores.Add(new clsErrorFilaLicitacion(i, fila, campo.CampoId, clsCodigosLineaSolicitud.MunicipioInexistente,
                            $"El id de municipio {id} de '{campo.Nombre}' no existe en el catálogo de municipios de Nexo (GET /web/municipios)."));
                        return (id, texto);
                    }

                    if (!string.IsNullOrWhiteSpace(texto) && clsResolvedorMunicipios.Clave(texto) != clsResolvedorMunicipios.Clave(nombre))
                    {
                        avisos?.Add($"Fila {i + 1}, {campo.Nombre}: se ignoró el nombre '{texto.Trim()}' y se copió '{nombre}' del municipio {id}.");
                    }

                    return (id, nombre);
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
                    return (resuelto.Id, captura ? NombreCatalogo(resuelto.Id) ?? resuelto.Municipio : texto);
                }

                errores.Add(Error(i, fila, campo, clave, resolucion));
                return (null, texto);
            }

            var origen = Resolver(_campos[0]);
            var destino = Resolver(_campos[1]);
            var devolucion = captura ? Resolver(_campos[2]) : (linea.CiudadDevolucionMunicipioId, linea.CiudadDevolucionContenedor);
            var levante = captura ? Resolver(_campos[3]) : (linea.CiudadLevanteMunicipioId, linea.CiudadLevanteContenedor);
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
