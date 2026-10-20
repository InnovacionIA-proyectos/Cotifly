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
/// <see cref="Canonicas"/> (D-1019B-2, #188 M1): las mismas líneas con <c>tipo_vehiculo</c> y <c>carroceria</c> escritos como en el
/// catálogo de Nexo (<c>2s2</c>/<c>furgon</c> → <c>2S2</c>/<c>FURGON</c>), para que el camión del respaldo se guarde y se muestre con la
/// misma escritura que viaja a Nexo; <c>null</c> = sin cambios.
/// </summary>
public sealed record clsValidacionCatalogoLineas(
    IReadOnlyList<IReadOnlyList<clsErrorCampo>> Errores, clsErrorDominio? NoDisponible, IReadOnlyList<clsLineaSolicitudCosteo>? Canonicas = null);

/// <summary>Implementación de <see cref="IClsValidadorCatalogosCaptura"/>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>HU-014 (#188, hallazgo M1; decisión del 2026-10-19 "enviar solo códigos conocidos").</b> <c>tipo_vehiculo</c> y
/// <c>carroceria</c> son el vehículo digitado del respaldo (<c>Captura:AceptarVehiculoCliente</c>) y viajan a Nexo: deben ser códigos
/// que NEXO conozca, o la línea nacería en cuarentena (S5-HU-18.4). Ya no basta con que estén en el catálogo del cubicador (HU-023.1 los
/// aceptaba, y un código que solo conocía el cubicador caía en cuarentena). <c>tipo_vehiculo</c> se valida contra el <c>codigo</c> de
/// <c>GET /api/catalogos/tipos-vehiculo</c> de Nexo y <c>carroceria</c> contra la lista de carrocerías de Nexo
/// (<see cref="clsCatalogosCaptura.Carrocerias"/>; Nexo no publica ese catálogo). Se rechaza con 400 (<c>CODIGO_NO_CATALOGO</c>) en vez
/// de descartarlo en silencio: el comercial sabe que su vehículo no se usaría, y el emparejamiento sigue comparando el vehículo que
/// viajó.</item>
/// <item><b>Equivalencia</b>: la de Nexo y de <see cref="clsVehiculoLineaSolicitud.MismoCodigo"/> (sin tildes ni mayúsculas, con
/// <c>_</c>/<c>-</c> como espacio; <see cref="clsCodigosConocidosNexo"/>): <c>FURGON_REFRIGERADO</c> es <c>FURGON REFRIGERADO</c> y
/// <c>3s3</c> es <c>3S3</c>. Una descripción (<c>Tractocamión 3 ejes, semi 3</c>) no es un código. El envío a Nexo lo escribe como en
/// el catálogo de Nexo.</item>
/// <item><b>En código</b>: <c>tipologia</c> (enum de 7 valores) y la lista provisional de extradimensionado de <c>clase_contenedor</c>
/// (G15, hasta que Nexo la formalice).</item>
/// <item><b>Catálogo maestro de Nexo</b> (con caché): <c>tipo_vehiculo</c>, <c>clase_viaje</c> (<c>descripcion</c> de
/// <c>clases-viaje</c>, exacta) y <c>clase_contenedor</c> (<c>descripcion</c> de <c>clases-contenedor</c>, exacta; HU-023.1 quitó la
/// lista fija de 5 códigos). Solo se consulta si hace falta; sin copia utilizable, <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.</item>
/// <item>Un valor con espacios al borde se compara recortado, igual que lo guarda la cotización.</item>
/// </list>
/// </remarks>
public sealed class clsValidadorCatalogosCaptura(IClsCatalogosMaestrosProvider maestros, ILogger<clsValidadorCatalogosCaptura> logger)
    : IClsValidadorCatalogosCaptura
{
    public const string MensajeSinCatalogo =
        "No se pudo validar el tipo de vehículo, la clase de viaje o la clase de contenedor contra los catálogos de Nexo: intenta de nuevo más tarde.";

    public async Task<clsErrorDominio?> ValidarAsync(Guid usuarioId, clsLineaSolicitudCosteo linea, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(linea);

        // Primero lo que se valida en código: si ya falla, no hace falta consultar Nexo.
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

        if (SinClasesContenedor(linea, catalogos.Valor))
        {
            return ClasesContenedorNoDisponibles();
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
                if (lineas.Any(l => l is not null && SinClasesContenedor(l, obtenidos.Valor)))
                {
                    noDisponible = ClasesContenedorNoDisponibles();
                }
            }
        }

        IReadOnlyList<IReadOnlyList<clsErrorCampo>> errores = [.. lineas.Select(l => l is null
            ? (IReadOnlyList<clsErrorCampo>)[]
            : [.. ErroresEnCodigo(l), .. catalogos is null ? [] : ErroresMaestros(l, catalogos)])];
        return new clsValidacionCatalogoLineas(errores, noDisponible, [.. lineas.Select(l => l is null ? l! : Canonica(l, catalogos))]);
    }

    /// <summary>
    /// D-1019B-2 (#188 M1): la línea con <c>tipo_vehiculo</c> y <c>carroceria</c> escritos como en Nexo (la equivalencia de
    /// <see cref="clsCodigosConocidosNexo"/>), la misma escritura con la que viajan. Un código que no se puede resolver (o el tipo sin el
    /// catálogo maestro) queda recortado y tal cual: ya trae su error.
    /// </summary>
    public static clsLineaSolicitudCosteo Canonica(clsLineaSolicitudCosteo linea, clsCatalogosMaestros? catalogos)
    {
        ArgumentNullException.ThrowIfNull(linea);
        var tipo = Recortar(linea.TipoVehiculo);
        var carroceria = Recortar(linea.Carroceria);
        var tipoNexo = tipo is null || catalogos is null ? tipo : clsCodigosConocidosNexo.Resolver(catalogos.TiposVehiculo.Select(t => t.Valor), tipo) ?? tipo;
        var carroceriaNexo = carroceria is null ? null : clsCodigosConocidosNexo.Carroceria(carroceria) ?? carroceria;
        return string.Equals(tipoNexo, linea.TipoVehiculo, StringComparison.Ordinal) && string.Equals(carroceriaNexo, linea.Carroceria, StringComparison.Ordinal)
            ? linea
            : linea with { TipoVehiculo = tipoNexo, Carroceria = carroceriaNexo };
    }

    /// <summary>Tipología (enum) y carrocería (lista de carrocerías de Nexo, con la equivalencia de <see cref="clsCodigosConocidosNexo"/>).</summary>
    private static List<clsErrorCampo> ErroresEnCodigo(clsLineaSolicitudCosteo linea)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.Tipologia) is { } tipologia && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Tipologias, tipologia))
        {
            errores.Add(NoEsCodigo("tipologia", tipologia, string.Join(", ", clsCatalogosCaptura.Tipologias)));
        }

        if (Recortar(linea.Carroceria) is { } carroceria && clsCodigosConocidosNexo.Carroceria(carroceria) is null)
        {
            errores.Add(NoEsCodigo("carroceria", carroceria, string.Join(", ", clsCatalogosCaptura.Carrocerias)));
        }

        return errores;
    }

    /// <summary>
    /// Tipo de vehículo (<c>codigo</c>, con la equivalencia de <see cref="clsCodigosConocidosNexo"/>), clase de viaje (<c>descripcion</c>) y
    /// clase de contenedor (<c>descripcion</c> de <c>clases-contenedor</c>, o la lista provisional G15) contra el catálogo maestro de Nexo.
    /// </summary>
    private static List<clsErrorCampo> ErroresMaestros(clsLineaSolicitudCosteo linea, clsCatalogosMaestros catalogos)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.TipoVehiculo) is { } tipoVehiculo
            && clsCodigosConocidosNexo.Resolver(catalogos.TiposVehiculo.Select(t => t.Valor), tipoVehiculo) is null)
        {
            errores.Add(NoEsCodigo("tipo_vehiculo", tipoVehiculo, null));
        }

        if (Recortar(linea.ClaseViaje) is { } claseViaje && !EnCatalogo(catalogos.ClasesViaje, claseViaje))
        {
            errores.Add(NoEsCodigo("clase_viaje", claseViaje, null));
        }

        if (Recortar(linea.ClaseContenedor) is { } claseContenedor && !EsProvisional(claseContenedor) && catalogos.ClasesContenedor.Count > 0
            && !EnCatalogo(catalogos.ClasesContenedor, claseContenedor))
        {
            errores.Add(NoEsCodigo("clase_contenedor", claseContenedor,
                string.Join(", ", catalogos.ClasesContenedor.Select(c => c.Valor).Concat(clsCatalogosCaptura.ClasesContenedorProvisionales))));
        }

        return errores;
    }

    private static bool UsaCatalogoMaestro(clsLineaSolicitudCosteo linea) =>
        Recortar(linea.TipoVehiculo) is not null
        || Recortar(linea.ClaseViaje) is not null
        || (Recortar(linea.ClaseContenedor) is { } clase && !EsProvisional(clase));

    /// <summary>
    /// ¿La línea trae una clase de contenedor que hay que validar con Nexo y la copia del catálogo maestro llegó sin
    /// <c>clases-contenedor</c>? (Es un catálogo opcional de Nexo: si falló, llega vacío.) Sin él no se valida: la captura no sigue
    /// (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c>), porque ya no hay lista fija de respaldo y nunca se manda texto libre a Nexo.
    /// </summary>
    private static bool SinClasesContenedor(clsLineaSolicitudCosteo linea, clsCatalogosMaestros catalogos) =>
        catalogos.ClasesContenedor.Count == 0 && Recortar(linea.ClaseContenedor) is { } clase && !EsProvisional(clase);

    private clsErrorDominio ClasesContenedorNoDisponibles()
    {
        logger.LogWarning("El catálogo de clases de contenedor de Nexo llegó vacío: la clase de contenedor no se puede validar y se rechaza.");
        return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, MensajeSinCatalogo);
    }

    private static bool EsProvisional(string claseContenedor) =>
        clsCatalogosCaptura.Contiene(clsCatalogosCaptura.ClasesContenedorProvisionales, claseContenedor);

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
