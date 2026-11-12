using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.CatalogosFlete.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Validación por código de catálogo de la captura hacia Nexo. <c>POST /api/captura-solicitudes</c> de
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
    /// Las mismas reglas que <see cref="ValidarAsync"/> sobre todas las filas de una licitación, sin detenerse en el primer
    /// error: por cada línea (en el mismo orden) la lista de campos que no son códigos de catálogo (<c>CODIGO_NO_CATALOGO</c>). El
    /// catálogo maestro se consulta una sola vez y solo si alguna fila trae <c>tipo_vehiculo</c> o <c>clase_viaje</c>. Sin copia
    /// utilizable, <see cref="clsValidacionCatalogoLineas.NoDisponible"/> es <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> y los errores traen
    /// igual lo que se valida en código (tipología, carrocería y clase de contenedor): el comercial no los pierde por una caída de Nexo.
    /// </summary>
    Task<clsValidacionCatalogoLineas> ValidarLineasAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, CancellationToken cancellationToken = default);

    /// <summary>
    /// Advierte (log, nunca bloquea) si <c>MargenesPolitica.mapeoLineasNexo</c> de <paramref name="politica"/> no aparece en la
    /// última copia de <c>GET /api/catalogos/lineas-negocio</c>. Sin copia en memoria no consulta Nexo. Devuelve <c>false</c> solo si
    /// pudo revisarlo y no está.
    /// </summary>
    bool RevisarMapeoLineaNexo(clsMargenPolitica? politica);
}

/// <summary>
/// Resultado de <see cref="IClsValidadorCatalogosCaptura.ValidarLineasAsync"/>: por línea, los campos que no son códigos de catálogo, y
/// <see cref="NoDisponible"/> si <c>tipo_vehiculo</c>/<c>clase_viaje</c> no se pudieron validar (catálogo maestro caído y sin copia).
/// <see cref="Canonicas"/>: las mismas líneas con <c>tipo_vehiculo</c> y <c>carroceria</c> escritos como en el
/// catálogo de Nexo (<c>2s2</c>/<c>furgon</c> → <c>2S2</c>/<c>FURGON</c>), para que el camión del respaldo se guarde y se muestre con la
/// misma escritura que viaja a Nexo; <c>null</c> = sin cambios.
/// </summary>
public sealed record clsValidacionCatalogoLineas(
    IReadOnlyList<IReadOnlyList<clsErrorCampo>> Errores, clsErrorDominio? NoDisponible, IReadOnlyList<clsLineaSolicitudCosteo>? Canonicas = null);

/// <summary>Implementación de <see cref="IClsValidadorCatalogosCaptura"/>.</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Solo códigos que Nexo conoce.</b> <c>tipo_vehiculo</c> y
/// <c>carroceria</c> son el vehículo digitado del respaldo (<c>Captura:AceptarVehiculoCliente</c>) y viajan a Nexo: deben ser códigos
/// que NEXO conozca, o la línea nacería en cuarentena. No basta con que estén en el catálogo del cubicador (un código que solo
/// conoce el cubicador cae en cuarentena). <c>tipo_vehiculo</c> se valida contra el <c>codigo</c> de
/// <c>GET /api/catalogos/tipos-vehiculo</c> de Nexo y <c>carroceria</c> contra la lista de carrocerías de Nexo
/// (<see cref="clsCatalogosCaptura.Carrocerias"/>; Nexo no publica ese catálogo). Se rechaza con 400 (<c>CODIGO_NO_CATALOGO</c>) en vez
/// de descartarlo en silencio: el comercial sabe que su vehículo no se usaría, y el emparejamiento sigue comparando el vehículo que
/// viajó.</item>
/// <item><b>Equivalencia</b>: la de Nexo y de <see cref="clsVehiculoLineaSolicitud.MismoCodigo"/> (sin tildes ni mayúsculas, con
/// <c>_</c>/<c>-</c> como espacio; <see cref="clsCodigosConocidosNexo"/>): <c>FURGON_REFRIGERADO</c> es <c>FURGON REFRIGERADO</c> y
/// <c>3s3</c> es <c>3S3</c>. Una descripción (<c>Tractocamión 3 ejes, semi 3</c>) no es un código. El envío a Nexo lo escribe como en
/// el catálogo de Nexo.</item>
/// <item><b>En código</b>: <c>tipologia</c> (enum de 7 valores) y la lista provisional de extradimensionado de <c>clase_contenedor</c>.</item>
/// <item><b>Catálogo maestro de Nexo</b> (con caché): <c>tipo_vehiculo</c>, <c>clase_viaje</c> (<c>descripcion</c> de
/// <c>clases-viaje</c>, exacta) y <c>clase_contenedor</c> (<c>descripcion</c> de <c>clases-contenedor</c>, exacta; sin lista fija
/// de códigos). Solo se consulta si hace falta; sin copia utilizable, <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.</item>
/// <item>Un valor con espacios al borde se compara recortado, igual que lo guarda la cotización.</item>
/// <item><b>Alias de Nexo.</b> Un <c>tipo_vehiculo</c> que el catálogo no conoce, o una <c>clase_contenedor</c> que no
/// está en el catálogo ni en la lista provisional, se busca en el diccionario de alias de Nexo ("TB" → <c>2L2</c>, "40HC" →
/// <c>CNT40.HC</c>) ANTES de rechazarlo: si resuelve a un código oficial, se acepta y <see cref="clsValidacionCatalogoLineas.Canonicas"/>
/// lo guarda con ese código (<see cref="Canonica"/>, como con la escritura de Nexo). Sin diccionario (Nexo caído y sin copia)
/// se valida sin alias, con un warning: nunca se rechaza por el diccionario. Solo se consulta si hace falta.</item>
/// </list>
/// </remarks>
public sealed class clsValidadorCatalogosCaptura(
    IClsCatalogosMaestrosProvider maestros, ILogger<clsValidadorCatalogosCaptura> logger, IClsAliasNexoProvider? alias = null)
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

        var diccionario = await AliasSiHaceFaltaAsync(usuarioId, [linea], catalogos.Valor, cancellationToken);
        return ErroresMaestros(linea, catalogos.Valor, diccionario) is [var maestro, ..] ? AErrorDominio(maestro) : null;
    }

    public async Task<clsValidacionCatalogoLineas> ValidarLineasAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        clsCatalogosMaestros? catalogos = null;
        clsDiccionarioAliasNexo? diccionario = null;
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

                diccionario = await AliasSiHaceFaltaAsync(usuarioId, lineas, catalogos, cancellationToken);
            }
        }

        IReadOnlyList<IReadOnlyList<clsErrorCampo>> errores = [.. lineas.Select(l => l is null
            ? (IReadOnlyList<clsErrorCampo>)[]
            : [.. ErroresEnCodigo(l), .. catalogos is null ? [] : ErroresMaestros(l, catalogos, diccionario)])];
        return new clsValidacionCatalogoLineas(errores, noDisponible, [.. lineas.Select(l => l is null ? l! : Canonica(l, catalogos, diccionario))]);
    }

    /// <summary>
    /// El diccionario de alias de Nexo, solo si alguna línea trae un <c>tipo_vehiculo</c> que el catálogo no conoce o una
    /// <c>clase_contenedor</c> que no está en el catálogo ni en la lista provisional. <c>null</c> si no hace falta o no está disponible (se
    /// valida sin alias; nunca falla por esto).
    /// </summary>
    private async Task<clsDiccionarioAliasNexo?> AliasSiHaceFaltaAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo?> lineas, clsCatalogosMaestros catalogos, CancellationToken cancellationToken)
    {
        var tipos = catalogos.TiposVehiculo.Select(t => t.Valor).ToList();
        var clases = catalogos.ClasesContenedor.Select(c => c.Valor).ToList();
        if (alias is null || !lineas.Any(l => l is not null
                && ((Recortar(l.TipoVehiculo) is { } tipo && clsCodigosConocidosNexo.Resolver(tipos, tipo) is null)
                    || (Recortar(l.ClaseContenedor) is { } clase && clases.Count > 0
                        && clsResolucionCodigosNexo.ClaseContenedor(clases, null, clase) is null))))
        {
            return null;
        }

        try
        {
            var resultado = await alias.ObtenerAsync(usuarioId, cancellationToken);
            if (resultado.EsExitoso)
            {
                return resultado.Valor;
            }

            logger.LogWarning("Sin el diccionario de alias de Nexo ({Codigo}): la captura se valida sin alias.", resultado.Error.Codigo);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Falló la lectura del diccionario de alias de Nexo ({Tipo}): la captura se valida sin alias.", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// La línea con <c>tipo_vehiculo</c> y <c>carroceria</c> escritos como en Nexo (la equivalencia de
    /// <see cref="clsCodigosConocidosNexo"/>), la misma escritura con la que viajan. Con <paramref name="alias"/>, un
    /// <c>tipo_vehiculo</c> o una <c>clase_contenedor</c> que solo conoce el diccionario de alias de Nexo queda con el código oficial
    /// ("TB" → <c>2L2</c>, "40HC" → <c>CNT40.HC</c>). Un código que no se puede resolver (o el tipo sin el catálogo maestro) queda
    /// recortado y tal cual: ya trae su error.
    /// </summary>
    public static clsLineaSolicitudCosteo Canonica(clsLineaSolicitudCosteo linea, clsCatalogosMaestros? catalogos, clsDiccionarioAliasNexo? alias = null)
    {
        ArgumentNullException.ThrowIfNull(linea);
        var tipo = Recortar(linea.TipoVehiculo);
        var carroceria = Recortar(linea.Carroceria);
        var tipoNexo = tipo is null || catalogos is null
            ? tipo
            : clsResolucionCodigosNexo.Vehiculo(catalogos.TiposVehiculo.Select(t => t.Valor), alias, tipo)?.Codigo ?? tipo;
        var carroceriaNexo = carroceria is null ? null : clsCodigosConocidosNexo.Carroceria(carroceria) ?? carroceria;
        var clase = linea.ClaseContenedor;
        if (alias is not null && catalogos is { ClasesContenedor.Count: > 0 } && Recortar(clase) is { } valorClase
            && clsResolucionCodigosNexo.ClaseContenedor([.. catalogos.ClasesContenedor.Select(c => c.Valor)], alias, valorClase) is { Fuente: FuenteCodigoNexo.Alias } claseAlias)
        {
            clase = claseAlias.Codigo;
        }

        return string.Equals(tipoNexo, linea.TipoVehiculo, StringComparison.Ordinal) && string.Equals(carroceriaNexo, linea.Carroceria, StringComparison.Ordinal)
               && string.Equals(clase, linea.ClaseContenedor, StringComparison.Ordinal)
            ? linea
            : linea with { TipoVehiculo = tipoNexo, Carroceria = carroceriaNexo, ClaseContenedor = clase };
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
    /// clase de contenedor (<c>descripcion</c> de <c>clases-contenedor</c>, o la lista provisional) contra el catálogo maestro de Nexo.
    /// </summary>
    private static List<clsErrorCampo> ErroresMaestros(clsLineaSolicitudCosteo linea, clsCatalogosMaestros catalogos, clsDiccionarioAliasNexo? alias)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.TipoVehiculo) is { } tipoVehiculo
            && clsResolucionCodigosNexo.Vehiculo(catalogos.TiposVehiculo.Select(t => t.Valor), alias, tipoVehiculo) is null)
        {
            errores.Add(NoEsCodigo("tipo_vehiculo", tipoVehiculo, null));
        }

        if (Recortar(linea.ClaseViaje) is { } claseViaje && !EnCatalogo(catalogos.ClasesViaje, claseViaje))
        {
            errores.Add(NoEsCodigo("clase_viaje", claseViaje, null));
        }

        if (Recortar(linea.ClaseContenedor) is { } claseContenedor && !EsProvisional(claseContenedor) && catalogos.ClasesContenedor.Count > 0
            && !EnCatalogo(catalogos.ClasesContenedor, claseContenedor)
            && clsResolucionCodigosNexo.ClaseContenedor([.. catalogos.ClasesContenedor.Select(c => c.Valor)], alias, claseContenedor) is null)
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
    /// (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c>), porque no hay lista fija de respaldo y nunca se manda texto libre a Nexo.
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
