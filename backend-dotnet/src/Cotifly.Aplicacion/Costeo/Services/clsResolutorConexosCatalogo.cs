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
/// catálogo de activos (<see cref="IClsServiciosConexosNexo.BuscarAsync"/>), solo si hay UNA coincidencia. Un conexo sin resolver no
/// viene en el resultado: el llamador lo aísla.
/// </summary>
public interface IClsResolutorConexosCatalogo
{
    /// <summary>
    /// Para cada conexo (id guardado y nombre) el servicio del catálogo con su id, nombre y costo unitario, por el id guardado. Error solo
    /// si el catálogo de Nexo no responde.
    /// </summary>
    Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> ResolverAsync(
        Guid usuarioId, IReadOnlyCollection<(string Id, string? Nombre)> conexos, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsResolutorConexosCatalogo"/>.</summary>
public sealed class clsResolutorConexosCatalogo(IClsCostosConexosNexo costos, IClsServiciosConexosNexo catalogo) : IClsResolutorConexosCatalogo
{
    /// <summary>Tamaño de la página del catálogo con que se busca un nombre.</summary>
    public const int TamanoBusqueda = 50;

    public async Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> ResolverAsync(
        Guid usuarioId, IReadOnlyCollection<(string Id, string? Nombre)> conexos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conexos);
        var resueltos = new Dictionary<string, clsCostoConexoNexo>(StringComparer.OrdinalIgnoreCase);
        if (conexos.Count == 0)
        {
            return clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(resueltos);
        }

        // 1. Por id: los capturados ya traen el del catálogo.
        var porId = conexos.Select(c => c.Id).Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (porId.Count > 0)
        {
            var leidos = await costos.CostosAsync(usuarioId, porId, cancellationToken);
            if (leidos.EsFallo)
            {
                return leidos.Error;
            }

            foreach (var (id, costo) in leidos.Valor)
            {
                resueltos[id] = costo;
            }
        }

        // 2. Por nombre exacto (una sola coincidencia activa): los que se guardaron con el id de la fila de Nexo.
        var porNombre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id guardado → id del catálogo
        foreach (var grupo in conexos.Where(c => !resueltos.ContainsKey(c.Id) && !string.IsNullOrWhiteSpace(c.Nombre))
                     .GroupBy(c => c.Nombre!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var pagina = await catalogo.BuscarAsync(usuarioId, new clsFiltroCatalogoConexos(grupo.Key, null, 1, TamanoBusqueda), cancellationToken);
            if (pagina.EsFallo)
            {
                return pagina.Error;
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
                return leidos.Error;
            }

            var delCatalogo = new Dictionary<string, clsCostoConexoNexo>(leidos.Valor, StringComparer.OrdinalIgnoreCase);
            foreach (var (guardado, idCatalogo) in porNombre)
            {
                if (delCatalogo.TryGetValue(idCatalogo, out var costo))
                {
                    resueltos[guardado] = costo;
                }
            }
        }

        return clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(resueltos);
    }
}
