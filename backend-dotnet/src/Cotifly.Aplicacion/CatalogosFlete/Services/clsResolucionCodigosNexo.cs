using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.CatalogosFlete.Services;

/// <summary>De dónde salió el código oficial de un texto (HU-031.1, #194).</summary>
public enum FuenteCodigoNexo
{
    /// <summary>El texto ya es un código del catálogo oficial de Nexo (exacto o con la equivalencia de <see cref="clsCodigosConocidosNexo"/>).</summary>
    Catalogo,

    /// <summary>El texto es un alias del diccionario de Nexo (NEXO #321).</summary>
    Alias,
}

/// <summary>Un texto resuelto a un código oficial de Nexo y de dónde salió.</summary>
public sealed record clsCodigoResueltoNexo(string Codigo, FuenteCodigoNexo Fuente);

/// <summary>
/// HU-031.1 (#194): resuelve un tipo de vehículo o una clase de contenedor escritos a mano a un código oficial de Nexo, en este orden:
/// (1) el catálogo oficial (<c>GET /api/catalogos/tipos-vehiculo</c>, o <c>clases-contenedor</c> más la lista provisional G15); (2) el
/// diccionario de alias (NEXO #321). Lo usan el envío a Nexo (<c>clsCosteoNexo</c>), la validación de la captura
/// (<c>clsValidadorCatalogosCaptura</c>) y <c>GET /web/alias/resolver</c>, para que los tres respondan lo mismo.
/// </summary>
/// <remarks>
/// El código de un alias se devuelve escrito como en el catálogo oficial si está en él (<c>2l2</c> → <c>2L2</c>). Si no está (el catálogo
/// no está disponible, o es uno de los códigos que el diccionario de Nexo acepta sin estar en el catálogo), se devuelve el del alias: Nexo
/// solo deja crear un alias que apunte a un código oficial (<c>CODIGO_NO_RECONOCIDO</c>) y su captura trata como oficial lo que resuelve
/// por alias. La carrocería no pasa por aquí: el diccionario de Nexo no la cubre (<c>tipo</c> = vehiculo | contenedor).
/// </remarks>
public static class clsResolucionCodigosNexo
{
    /// <summary>El código de vehículo oficial de <paramref name="texto"/>, o <c>null</c> si ni el catálogo ni el diccionario lo conocen.</summary>
    public static clsCodigoResueltoNexo? Vehiculo(IEnumerable<string>? catalogo, clsDiccionarioAliasNexo? alias, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        // HU-044.8 (#291): el código NEXO (C2, C3) es el canónico de SICE-TAC/Plaza; «2»/«3» del catálogo de SAP se
        // traducen a él con la única fuente de equivalencia (tbl_mapeo_vehiculo_tarifario), antes del catálogo y del alias,
        // para no mandar nunca «2»/«3» a Nexo (cuarentena) ni dejar que el alias (C2 → «2») los devuelva al código de SAP.
        if (clsEquivalenciaVehiculoNexo.ACodigoNexo(texto) is { } codigoNexo)
        {
            return new clsCodigoResueltoNexo(codigoNexo, FuenteCodigoNexo.Catalogo);
        }

        var lista = catalogo?.ToList();
        if (clsCodigosConocidosNexo.Resolver(lista, texto) is { } oficial)
        {
            return new clsCodigoResueltoNexo(oficial, FuenteCodigoNexo.Catalogo);
        }

        return alias?.Resolver(TipoAliasNexo.Vehiculo, texto) is { } codigo
            ? new clsCodigoResueltoNexo(clsCodigosConocidosNexo.Resolver(lista, codigo) ?? codigo, FuenteCodigoNexo.Alias)
            : null;
    }

    /// <summary>
    /// La clase de contenedor oficial de <paramref name="texto"/>: exacta en <paramref name="catalogo"/> (la <c>descripcion</c> de
    /// <c>clases-contenedor</c>, que es el código) o en la lista provisional G15; si no, el alias. Un alias cuyo código no está ni en el
    /// catálogo ni en la provisional solo se acepta si el catálogo está vacío (no disponible): con catálogo, se exige que el código esté.
    /// </summary>
    public static clsCodigoResueltoNexo? ClaseContenedor(IReadOnlyCollection<string>? catalogo, clsDiccionarioAliasNexo? alias, string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var valor = texto.Trim();
        if (EsClaseOficial(catalogo, valor))
        {
            return new clsCodigoResueltoNexo(valor, FuenteCodigoNexo.Catalogo);
        }

        if (alias?.Resolver(TipoAliasNexo.Contenedor, valor) is not { } codigo)
        {
            return null;
        }

        var oficial = catalogo?.FirstOrDefault(c => string.Equals(c, codigo, StringComparison.OrdinalIgnoreCase))
                      ?? clsCatalogosCaptura.ClasesContenedorProvisionales.FirstOrDefault(c => string.Equals(c, codigo, StringComparison.OrdinalIgnoreCase));
        return oficial is not null || catalogo is null || catalogo.Count == 0
            ? new clsCodigoResueltoNexo(oficial ?? codigo, FuenteCodigoNexo.Alias)
            : null;
    }

    private static bool EsClaseOficial(IReadOnlyCollection<string>? catalogo, string valor) =>
        clsCatalogosCaptura.Contiene(clsCatalogosCaptura.ClasesContenedorProvisionales, valor)
        || (catalogo is not null && clsCatalogosCaptura.Contiene(catalogo, valor));
}
