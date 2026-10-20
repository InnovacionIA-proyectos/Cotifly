using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Costeo.Interfaces;

/// <summary>
/// Costo unitario de los servicios conexos del catálogo de Nexo (<c>GET /api/servicios-conexos</c>, campo <c>costoUnitario</c>) para
/// guardarlos con la ruta al capturarla (HU-021.6, decisión 3: el precio del conexo es el costo de Nexo). Es INTERNO: el costo nunca
/// va al comercial (el catálogo que ve el frontend, <see cref="IClsServiciosConexosNexo.BuscarAsync"/>, no lo trae). Nexo no tiene
/// <c>GET</c> por id, así que el adaptador recorre las páginas del catálogo de activos hasta encontrar todos los pedidos. Nunca lanza
/// por fallos de Nexo: <c>NEXO_NO_DISPONIBLE</c> (sin configurar, caído, 5xx, 401, sin respuesta) o <c>NEXO_SIN_PERMISO</c> (403).
/// </summary>
public interface IClsCostosConexosNexo
{
    /// <summary>
    /// Los servicios activos de <paramref name="ids"/> que están en el catálogo, con su nombre y su costo unitario. Un id que no aparece
    /// (no existe o está inactivo) simplemente no viene en el resultado: el llamador lo reporta.
    /// </summary>
    Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> CostosAsync(
        Guid usuarioId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}

/// <summary>
/// Un servicio del catálogo de Nexo con su costo unitario (solo interno). HU-014 (#188): <paramref name="Categoria"/> es su categoría en el
/// catálogo (<c>logistico</c>, <c>adicional</c>...; <c>null</c> si Nexo no la trae), que se guarda con el conexo (script 05).
/// </summary>
public sealed record clsCostoConexoNexo(string Id, string? Nombre, decimal CostoUnitario, string? Categoria = null);
