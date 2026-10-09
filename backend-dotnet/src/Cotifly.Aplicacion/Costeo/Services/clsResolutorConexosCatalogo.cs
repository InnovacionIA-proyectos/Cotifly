using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// El servicio del CATÁLOGO de Nexo de cada conexo de una ruta (HU-021.9, #136). <c>CotizacionServiciosConexos.idServicioConexo</c> guarda
/// dos cosas distintas según de dónde vino el conexo:
/// <list type="bullet">
/// <item>capturado con la ruta (#132) o copiado a una <c>-vN</c>: el id del catálogo (<c>servicioConexoId</c>);</item>
/// <item>agregado con <c>POST .../servicios-conexos</c> o importado desde Nexo (#124, #135): el id de la FILA de la cotización de Nexo
/// (<c>CotizacionConexoDto.id</c>), que no sirve para agregarlo a otra cotización.</item>
/// </list>
/// El esquema no tiene una columna para el id del catálogo (deuda: script ≥ 0011, revisión de @scalderon-bs; ADR-019). Mientras tanto se
/// resuelve así: primero por id en el catálogo (<see cref="IClsCostosConexosNexo"/>, con caché); si no está, por su nombre exacto en el
/// catálogo de activos (<see cref="IClsServiciosConexosNexo.BuscarAsync"/>), solo si hay UNA coincidencia.
/// </summary>
public interface IClsResolutorConexosCatalogo
{
    /// <summary>
    /// Para cada conexo (id guardado y nombre) el servicio del catálogo con su id, nombre y costo unitario, por el id guardado. Nunca falla
    /// el lote completo: un conexo cuya búsqueda no se pudo hacer porque el catálogo de Nexo no respondió queda en
    /// <see cref="clsResolucionConexosCatalogo.SinCatalogo"/> (se espera), y uno que el catálogo SÍ respondió sin encontrarlo (no existe o
    /// el nombre es ambiguo) no aparece en ninguno de los dos (irresoluble).
    /// </summary>
    Task<clsResolucionConexosCatalogo> ResolverAsync(
        Guid usuarioId, IReadOnlyCollection<(string Id, string? Nombre)> conexos, CancellationToken cancellationToken = default);
}

/// <summary>Resultado de <see cref="IClsResolutorConexosCatalogo.ResolverAsync"/>.</summary>
/// <param name="Resueltos">Servicio del catálogo por id guardado.</param>
/// <param name="SinCatalogo">Ids guardados que no se pudieron buscar porque el catálogo de Nexo no respondió: no son irresolubles.</param>
/// <param name="ErrorCatalogo">El primer error del catálogo, si alguna búsqueda falló.</param>
public sealed record clsResolucionConexosCatalogo(
    IReadOnlyDictionary<string, clsCostoConexoNexo> Resueltos, IReadOnlySet<string> SinCatalogo, clsErrorDominio? ErrorCatalogo);

/// <summary>Implementación de <see cref="IClsResolutorConexosCatalogo"/>.</summary>
public sealed class clsResolutorConexosCatalogo(IClsCostosConexosNexo costos, IClsServiciosConexosNexo catalogo) : IClsResolutorConexosCatalogo
{
    /// <summary>Tamaño de la página del catálogo con que se busca un nombre.</summary>
    public const int TamanoBusqueda = 50;

    public async Task<clsResolucionConexosCatalogo> ResolverAsync(
        Guid usuarioId, IReadOnlyCollection<(string Id, string? Nombre)> conexos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conexos);
        var resueltos = new Dictionary<string, clsCostoConexoNexo>(StringComparer.OrdinalIgnoreCase);
        var sinCatalogo = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        clsErrorDominio? error = null;
        if (conexos.Count == 0)
        {
            return new clsResolucionConexosCatalogo(resueltos, sinCatalogo, null);
        }

        // 1. Por id: los capturados ya traen el del catálogo. Si el lote falla, uno por uno: el que está en la caché no depende de que
        // el catálogo responda para los demás.
        var porId = conexos.Select(c => c.Id).Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (porId.Count > 0)
        {
            var leidos = await costos.CostosAsync(usuarioId, porId, cancellationToken);
            if (leidos.EsExitoso)
            {
                Agregar(resueltos, leidos.Valor);
            }
            else
            {
                error = leidos.Error;
                foreach (var id in porId)
                {
                    var uno = porId.Count == 1 ? leidos : await costos.CostosAsync(usuarioId, [id], cancellationToken);
                    if (uno.EsExitoso)
                    {
                        Agregar(resueltos, uno.Valor);
                    }
                    else
                    {
                        sinCatalogo.Add(id);
                    }
                }
            }
        }

        // 2. Por nombre exacto (una sola coincidencia activa): los que se guardaron con el id de la fila de Nexo. Si la búsqueda de un
        // nombre falla, solo sus conexos quedan sin catálogo.
        var porNombre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id guardado → id del catálogo
        foreach (var grupo in conexos.Where(c => !resueltos.ContainsKey(c.Id) && !sinCatalogo.Contains(c.Id) && !string.IsNullOrWhiteSpace(c.Nombre))
                     .GroupBy(c => c.Nombre!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var pagina = await catalogo.BuscarAsync(usuarioId, new clsFiltroCatalogoConexos(grupo.Key, null, 1, TamanoBusqueda), cancellationToken);
            if (pagina.EsFallo)
            {
                error ??= pagina.Error;
                sinCatalogo.UnionWith(grupo.Select(c => c.Id));
                continue;
            }

            var iguales = pagina.Valor.Items
                .Where(s => s.Activo && string.Equals(s.Nombre?.Trim(), grupo.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (iguales.Count == 1)
            {
                foreach (var conexo in grupo)
                {
                    porNombre[conexo.Id] = iguales[0].Id;
                }
            }
        }

        if (porNombre.Count > 0)
        {
            var leidos = await costos.CostosAsync(usuarioId, [.. porNombre.Values.Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);
            if (leidos.EsFallo)
            {
                error ??= leidos.Error;
                sinCatalogo.UnionWith(porNombre.Keys);
            }
            else
            {
                var delCatalogo = new Dictionary<string, clsCostoConexoNexo>(leidos.Valor, StringComparer.OrdinalIgnoreCase);
                foreach (var (guardado, idCatalogo) in porNombre)
                {
                    if (delCatalogo.TryGetValue(idCatalogo, out var costo))
                    {
                        resueltos[guardado] = costo;
                    }
                }
            }
        }

        return new clsResolucionConexosCatalogo(resueltos, sinCatalogo, error);
    }

    private static void Agregar(Dictionary<string, clsCostoConexoNexo> resueltos, IReadOnlyDictionary<string, clsCostoConexoNexo> leidos)
    {
        foreach (var (id, costo) in leidos)
        {
            resueltos[id] = costo;
        }
    }
}
