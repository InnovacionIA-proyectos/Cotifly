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
    /// Las mismas reglas que <see cref="ValidarAsync"/> sobre todas las filas de una licitación (HU-013.11), sin detenerse en el primer
    /// error: por cada línea (en el mismo orden) la lista de campos que no son códigos de catálogo (<c>CODIGO_NO_CATALOGO</c>). El
    /// catálogo maestro se consulta una sola vez y solo si alguna fila trae <c>tipo_vehiculo</c> o <c>clase_viaje</c>. Sin copia
    /// utilizable, <see cref="clsValidacionCatalogoLineas.NoDisponible"/> es <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> y los errores traen
    /// igual lo que se valida en código (tipología, carrocería y clase de contenedor): el comercial no los pierde por una caída de Nexo.
    /// </summary>
    Task<clsValidacionCatalogoLineas> ValidarLineasAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, CancellationToken cancellationToken = default);

    /// <summary>
    /// G11: advierte (log, nunca bloquea) si <c>MargenesPolitica.mapeoLineasNexo</c> de <paramref name="politica"/> no aparece en la
    /// última copia de <c>GET /api/catalogos/lineas-negocio</c>. Sin copia en memoria no consulta Nexo. Devuelve <c>false</c> solo si
    /// pudo revisarlo y no está.
    /// </summary>
    bool RevisarMapeoLineaNexo(clsMargenPolitica? politica);
}

/// <summary>
/// Resultado de <see cref="IClsValidadorCatalogosCaptura.ValidarLineasAsync"/>: por línea, los campos que no son códigos de catálogo, y
/// <see cref="NoDisponible"/> si <c>tipo_vehiculo</c>/<c>clase_viaje</c> no se pudieron validar (catálogo maestro caído y sin copia).
/// </summary>
public sealed record clsValidacionCatalogoLineas(IReadOnlyList<IReadOnlyList<clsErrorCampo>> Errores, clsErrorDominio? NoDisponible);

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

        // Primero lo que se valida en código: si ya falla, no hace falta consultar el catálogo maestro de Nexo.
        if (ErroresEnCodigo(linea) is [var local, ..])
        {
            return AErrorDominio(local);
        }

        if (!UsaCatalogoMaestro(linea))
        {
            return null;
        }

        var catalogos = await ObtenerMaestrosAsync(usuarioId, cancellationToken);
        if (catalogos.EsFallo)
        {
            return catalogos.Error;
        }

        return ErroresMaestros(linea, catalogos.Valor) is [var maestro, ..] ? AErrorDominio(maestro) : null;
    }

    public async Task<clsValidacionCatalogoLineas> ValidarLineasAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        clsCatalogosMaestros? catalogos = null;
        clsErrorDominio? noDisponible = null;
        if (lineas.Any(l => l is not null && UsaCatalogoMaestro(l)))
        {
            var obtenidos = await ObtenerMaestrosAsync(usuarioId, cancellationToken);
            if (obtenidos.EsFallo)
            {
                noDisponible = obtenidos.Error;
            }
            else
            {
                catalogos = obtenidos.Valor;
            }
        }

        IReadOnlyList<IReadOnlyList<clsErrorCampo>> errores = [.. lineas.Select(l => l is null
            ? (IReadOnlyList<clsErrorCampo>)[]
            : [.. ErroresEnCodigo(l), .. catalogos is null ? [] : ErroresMaestros(l, catalogos)])];
        return new clsValidacionCatalogoLineas(errores, noDisponible);
    }

    /// <summary>Tipología, carrocería y clase de contenedor: catálogos en código (más las descripciones de la copia en memoria).</summary>
    private List<clsErrorCampo> ErroresEnCodigo(clsLineaSolicitudCosteo linea)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.Tipologia) is { } tipologia && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Tipologias, tipologia))
        {
            errores.Add(NoEsCodigo("tipologia", tipologia, string.Join(", ", clsCatalogosCaptura.Tipologias)));
        }

        if (Recortar(linea.Carroceria) is { } carroceria && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Carrocerias, carroceria))
        {
            errores.Add(NoEsCodigo("carroceria", carroceria, string.Join(", ", clsCatalogosCaptura.Carrocerias)));
        }

        if (Recortar(linea.ClaseContenedor) is { } claseContenedor && !EsClaseContenedor(claseContenedor))
        {
            errores.Add(NoEsCodigo("clase_contenedor", claseContenedor,
                string.Join(", ", clsCatalogosCaptura.ClasesContenedor.Concat(clsCatalogosCaptura.ClasesContenedorProvisionales))));
        }

        return errores;
    }

    /// <summary>Tipo de vehículo (<c>codigo</c>) y clase de viaje (<c>descripcion</c>) contra el catálogo maestro de Nexo.</summary>
    private static List<clsErrorCampo> ErroresMaestros(clsLineaSolicitudCosteo linea, clsCatalogosMaestros catalogos)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.TipoVehiculo) is { } tipoVehiculo && !EnCatalogo(catalogos.TiposVehiculo, tipoVehiculo))
        {
            errores.Add(NoEsCodigo("tipo_vehiculo", tipoVehiculo, null));
        }

        if (Recortar(linea.ClaseViaje) is { } claseViaje && !EnCatalogo(catalogos.ClasesViaje, claseViaje))
        {
            errores.Add(NoEsCodigo("clase_viaje", claseViaje, null));
        }

        return errores;
    }

    private static bool UsaCatalogoMaestro(clsLineaSolicitudCosteo linea) =>
        Recortar(linea.TipoVehiculo) is not null || Recortar(linea.ClaseViaje) is not null;

    private async Task<clsResultado<clsCatalogosMaestros>> ObtenerMaestrosAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        var catalogos = await maestros.ObtenerAsync(usuarioId, cancellationToken);
        if (catalogos.EsFallo)
        {
            logger.LogWarning("La solicitud de costeo no se validó contra los catálogos maestros de Nexo ({Codigo}): se rechaza.", catalogos.Error.Codigo);
            return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, MensajeSinCatalogo);
        }

        return catalogos;
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

    private static clsErrorCampo NoEsCodigo(string campo, string valor, string? opciones) =>
        new(campo, clsCodigosLineaSolicitud.CodigoNoCatalogo,
            $"El campo '{campo}' debe ser un código del catálogo de Nexo (GET /web/catalogos-flete); '{valor}' no lo es."
            + (opciones is null ? string.Empty : $" Opciones: {opciones}."));

    private static clsErrorDominio AErrorDominio(clsErrorCampo error) => new(clsCodigosError.SolicitudCosteoInvalida, error.Mensaje);

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
