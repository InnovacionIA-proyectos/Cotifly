namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// ¿Nexo conoce un código de vehículo o de carrocería? HU-014 (#188, decisión del usuario del 2026-10-19: "enviar solo códigos
/// conocidos"). Nexo pone en cuarentena (S5-HU-18.4) un código que no está en su catálogo y compara los nombres sin tildes, sin
/// mayúsculas y con <c>_</c>/<c>-</c> equivalentes a espacio (<c>COTIFLY-CONTRATO.md</c> §3.4). Aquí se usa la MISMA equivalencia que el
/// resto de Cotifly (<see cref="clsVehiculoLineaSolicitud.MismoCodigo"/>, D2) y se devuelve el código TAL COMO ESTÁ en el catálogo de
/// Nexo: así Nexo lo reconoce sin depender de su normalización.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Primero la coincidencia exacta (ordinal). Si no hay, la única entrada equivalente.</item>
/// <item>Si hay DOS o más entradas equivalentes distintas (por ejemplo <c>C-2</c> y <c>C 2</c>) y ninguna es exacta, el código es ambiguo
/// y se trata como desconocido: mejor una línea sin vehículo (la resuelve la Mesa) que un vehículo equivocado.</item>
/// <item>Solo el <c>codigo</c> del catálogo (nunca la descripción): emparejar por descripción ("Camión 2 ejes") podría asignar un
/// vehículo equivocado y cambiar el precio.</item>
/// <item>Sin cultura: <see cref="clsVehiculoLineaSolicitud.MismoCodigo"/> usa <c>ToUpperInvariant</c> y quita los diacríticos.</item>
/// </list>
/// </remarks>
public static class clsCodigosConocidosNexo
{
    /// <summary>
    /// El código de <paramref name="catalogo"/> que corresponde a <paramref name="codigo"/> (exacto o equivalente y sin ambigüedad), o
    /// <c>null</c> si no es conocido, viene vacío o el catálogo no está disponible (<c>null</c>).
    /// </summary>
    public static string? Resolver(IEnumerable<string?>? catalogo, string? codigo)
    {
        if (catalogo is null || string.IsNullOrWhiteSpace(codigo))
        {
            return null;
        }

        var buscado = codigo.Trim();
        var candidatos = catalogo.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (candidatos.Contains(buscado, StringComparer.Ordinal))
        {
            return buscado;
        }

        var equivalentes = candidatos.Where(c => clsVehiculoLineaSolicitud.MismoCodigo(c, buscado)).Take(2).ToList();
        return equivalentes.Count == 1 ? equivalentes[0] : null;
    }

    /// <summary>
    /// La carrocería de Nexo que corresponde a <paramref name="codigo"/> en <see cref="clsCatalogosCaptura.Carrocerias"/> (Nexo no publica
    /// un catálogo de carrocerías: es la lista fija de Cotifly), o <c>null</c>. <c>FURGON_REFRIGERADO</c> → <c>FURGON REFRIGERADO</c>.
    /// </summary>
    public static string? Carroceria(string? codigo) => Resolver(clsCatalogosCaptura.Carrocerias, codigo);
}
