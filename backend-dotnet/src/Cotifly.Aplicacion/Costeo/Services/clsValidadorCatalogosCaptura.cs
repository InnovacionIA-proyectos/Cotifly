using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Validación por código de catálogo de la captura hacia Nexo (HU-013.10, #106, G2 y G11). <c>POST /api/captura-solicitudes</c> de
/// Nexo no valida contra sus catálogos: resuelve el texto por coincidencia aproximada y, si no encuentra el tipo de vehículo, crea
/// una fila nueva de catálogo (duplicados). Por eso Cotifly solo envía códigos de catálogo.
/// </summary>
public interface IClsValidadorCatalogosCaptura
{
    /// <summary>
    /// <c>null</c> si <c>tipo_vehiculo</c>, <c>carroceria</c>, <c>clase_viaje</c>, <c>clase_contenedor</c> y <c>tipologia</c> (los
    /// que vienen) son códigos de catálogo. Si no, <c>SOLICITUD_COSTEO_INVALIDA</c> con el campo en el mensaje; si hace falta el
    /// catálogo maestro de Nexo y no hay una copia utilizable, <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> (nada se guarda ni se envía).
    /// </summary>
    Task<clsErrorDominio?> ValidarAsync(Guid usuarioId, clsLineaSolicitudCosteo linea, CancellationToken cancellationToken = default);

    /// <summary>
    /// G11: advierte (log, nunca bloquea) si <c>MargenesPolitica.mapeoLineasNexo</c> de <paramref name="politica"/> no aparece en la
    /// última copia de <c>GET /api/catalogos/lineas-negocio</c>. Sin copia en memoria no consulta Nexo. Devuelve <c>false</c> solo si
    /// pudo revisarlo y no está.
    /// </summary>
    bool RevisarMapeoLineaNexo(clsMargenPolitica? politica);
}

/// <summary>Implementación de <see cref="IClsValidadorCatalogosCaptura"/>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>En código</b> (no van a Nexo): <c>carroceria</c> (<see cref="clsCatalogosCaptura.Carrocerias"/>), <c>tipologia</c>
/// (enum de 7 valores) y <c>clase_contenedor</c> (5 códigos más la lista provisional de extradimensionado, G15, más las
/// descripciones de <c>GET /api/catalogos/clases-contenedor</c> si la copia está en memoria).</item>
/// <item><b>Catálogo maestro de Nexo</b> (con caché): <c>tipo_vehiculo</c> (<c>codigo</c> de <c>tipos-vehiculo</c>) y
/// <c>clase_viaje</c> (<c>descripcion</c> de <c>clases-viaje</c>). Solo se consulta si alguno de los dos viene.</item>
/// <item>Comparación exacta (ordinal): <c>3s3</c> no es <c>3S3</c>. Un valor con espacios al borde se compara recortado, igual que
/// lo guarda la cotización.</item>
/// </list>
/// </remarks>
public sealed class clsValidadorCatalogosCaptura(IClsCatalogosMaestrosProvider maestros, ILogger<clsValidadorCatalogosCaptura> logger)
    : IClsValidadorCatalogosCaptura
{
    public const string MensajeSinCatalogo =
        "No se pudo validar el tipo de vehículo o la clase de viaje contra los catálogos de Nexo: intenta de nuevo más tarde.";

    public async Task<clsErrorDominio?> ValidarAsync(Guid usuarioId, clsLineaSolicitudCosteo linea, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(linea);
        var tipologia = Recortar(linea.Tipologia);
        var carroceria = Recortar(linea.Carroceria);
        var claseContenedor = Recortar(linea.ClaseContenedor);
        var tipoVehiculo = Recortar(linea.TipoVehiculo);
        var claseViaje = Recortar(linea.ClaseViaje);

        if (tipologia is not null && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Tipologias, tipologia))
        {
            return NoEsCodigo("tipologia", tipologia, string.Join(", ", clsCatalogosCaptura.Tipologias));
        }

        if (carroceria is not null && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Carrocerias, carroceria))
        {
            return NoEsCodigo("carroceria", carroceria, string.Join(", ", clsCatalogosCaptura.Carrocerias));
        }

        if (claseContenedor is not null && !EsClaseContenedor(claseContenedor))
        {
            return NoEsCodigo("clase_contenedor", claseContenedor,
                string.Join(", ", clsCatalogosCaptura.ClasesContenedor.Concat(clsCatalogosCaptura.ClasesContenedorProvisionales)));
        }

        if (tipoVehiculo is null && claseViaje is null)
        {
            return null;
        }

        var catalogos = await maestros.ObtenerAsync(usuarioId, cancellationToken);
        if (catalogos.EsFallo)
        {
            logger.LogWarning("La solicitud de costeo no se validó contra los catálogos maestros de Nexo ({Codigo}): se rechaza.", catalogos.Error.Codigo);
            return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, MensajeSinCatalogo);
        }

        if (tipoVehiculo is not null && !EnCatalogo(catalogos.Valor.TiposVehiculo, tipoVehiculo))
        {
            return NoEsCodigo("tipo_vehiculo", tipoVehiculo, null);
        }

        return claseViaje is not null && !EnCatalogo(catalogos.Valor.ClasesViaje, claseViaje)
            ? NoEsCodigo("clase_viaje", claseViaje, null)
            : null;
    }

    public bool RevisarMapeoLineaNexo(clsMargenPolitica? politica)
    {
        var mapeo = Recortar(politica?.MapeoLineasNexo);
        if (mapeo is null || maestros.UltimaCopia is not { LineasNegocio.Count: > 0 } copia)
        {
            return true;
        }

        // Nexo resuelve la línea por coincidencia aproximada contra su enum (PuertoAInterior, Nacional...) y su catálogo trae
        // descripciones largas ("Oferta de Expedición Puerto a interior"): se compara sin tildes, espacios ni mayúsculas, por contención.
        var buscado = Normalizar(mapeo);
        if (copia.LineasNegocio.Any(l => Normalizar(l.Valor).Contains(buscado, StringComparison.Ordinal)))
        {
            return true;
        }

        logger.LogWarning(
            "MargenesPolitica.mapeoLineasNexo '{Mapeo}' (línea {Linea}) no aparece en GET /api/catalogos/lineas-negocio de Nexo: revisar el mapeo.",
            mapeo, politica!.LineaNegocio);
        return false;
    }

    private bool EsClaseContenedor(string valor) =>
        clsCatalogosCaptura.Contiene(clsCatalogosCaptura.ClasesContenedor, valor)
        || clsCatalogosCaptura.Contiene(clsCatalogosCaptura.ClasesContenedorProvisionales, valor)
        || (maestros.UltimaCopia is { } copia && EnCatalogo(copia.ClasesContenedor, valor));

    private static bool EnCatalogo(IReadOnlyList<clsOpcionCatalogo> catalogo, string valor) =>
        catalogo.Any(o => string.Equals(o.Valor, valor, StringComparison.Ordinal));

    private static clsErrorDominio NoEsCodigo(string campo, string valor, string? opciones) =>
        new(clsCodigosError.SolicitudCosteoInvalida,
            $"El campo '{campo}' debe ser un código del catálogo de Nexo (GET /web/catalogos-flete); '{valor}' no lo es."
            + (opciones is null ? string.Empty : $" Opciones: {opciones}."));

    private static string? Recortar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static string Normalizar(string valor)
    {
        var descompuesto = valor.Normalize(System.Text.NormalizationForm.FormD);
        return new string([.. descompuesto
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)]);
    }
}
