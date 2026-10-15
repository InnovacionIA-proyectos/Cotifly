using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
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
/// <item><b>Catálogos del cubicador</b> (HU-023.1, #156, con caché): <c>tipo_vehiculo</c> (<c>GET /tipos-vehiculo</c>, P1) y
/// <c>carroceria</c> (<c>GET /tipos-carroceria</c>) aceptan los códigos activos del cubicador (<c>2S2</c>, <c>CAMA_BAJA</c>...). Si el
/// código no está ahí (o el cubicador no está configurado o no responde), se valida como el vehículo digitado del respaldo: contra el
/// catálogo maestro de Nexo (vehículo) o la lista fija <see cref="clsCatalogosCaptura.Carrocerias"/> (carrocería).</item>
/// <item><b>En código</b>: <c>tipologia</c> (enum de 7 valores) y la lista provisional de extradimensionado de <c>clase_contenedor</c>
/// (G15, hasta que Nexo la formalice).</item>
/// <item><b>Catálogo maestro de Nexo</b> (con caché): <c>tipo_vehiculo</c> que no es del cubicador (<c>codigo</c> de
/// <c>tipos-vehiculo</c>), <c>clase_viaje</c> (<c>descripcion</c> de <c>clases-viaje</c>) y <c>clase_contenedor</c> (<c>descripcion</c>
/// de <c>clases-contenedor</c>; HU-023.1 quitó la lista fija de 5 códigos, lo que le quedaba a HU-015.1). Solo se consulta si hace
/// falta.</item>
/// <item>Comparación exacta (ordinal): <c>3s3</c> no es <c>3S3</c>. Un valor con espacios al borde se compara recortado, igual que
/// lo guarda la cotización.</item>
/// </list>
/// </remarks>
public sealed class clsValidadorCatalogosCaptura(
    IClsCatalogosMaestrosProvider maestros, ILogger<clsValidadorCatalogosCaptura> logger, IClsCubicador? cubicador = null)
    : IClsValidadorCatalogosCaptura
{
    /// <summary>Códigos activos de vehículo del cubicador leídos en esta petición (<c>null</c> dentro = no disponibles).</summary>
    private (IReadOnlySet<string>? Codigos, bool Leido) _vehiculosCubicador;

    /// <summary>Códigos activos de carrocería del cubicador leídos en esta petición.</summary>
    private (IReadOnlySet<string>? Codigos, bool Leido) _carroceriasCubicador;

    public const string MensajeSinCatalogo =
        "No se pudo validar el tipo de vehículo, la clase de viaje o la clase de contenedor contra los catálogos de Nexo: intenta de nuevo más tarde.";

    public async Task<clsErrorDominio?> ValidarAsync(Guid usuarioId, clsLineaSolicitudCosteo linea, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(linea);

        // Primero lo que se valida en código o con los catálogos del cubicador: si ya falla, no hace falta consultar Nexo.
        var cubicadorCodigos = await CodigosCubicadorAsync([linea], cancellationToken);
        if (ErroresEnCodigo(linea, cubicadorCodigos) is [var local, ..])
        {
            return AErrorDominio(local);
        }

        if (!UsaCatalogoMaestro(linea, cubicadorCodigos))
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

        return ErroresMaestros(linea, catalogos.Valor, cubicadorCodigos) is [var maestro, ..] ? AErrorDominio(maestro) : null;
    }

    public async Task<clsValidacionCatalogoLineas> ValidarLineasAsync(
        Guid usuarioId, IReadOnlyList<clsLineaSolicitudCosteo> lineas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        clsCatalogosMaestros? catalogos = null;
        clsErrorDominio? noDisponible = null;
        var cubicadorCodigos = await CodigosCubicadorAsync(lineas, cancellationToken);
        if (lineas.Any(l => l is not null && UsaCatalogoMaestro(l, cubicadorCodigos)))
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
            : [.. ErroresEnCodigo(l, cubicadorCodigos), .. catalogos is null ? [] : ErroresMaestros(l, catalogos, cubicadorCodigos)])];
        return new clsValidacionCatalogoLineas(errores, noDisponible);
    }

    /// <summary>Tipología (enum) y carrocería (catálogo del cubicador o, para el vehículo digitado del respaldo, la lista fija).</summary>
    private static List<clsErrorCampo> ErroresEnCodigo(clsLineaSolicitudCosteo linea, clsCodigosCubicador cubicadorCodigos)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.Tipologia) is { } tipologia && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Tipologias, tipologia))
        {
            errores.Add(NoEsCodigo("tipologia", tipologia, string.Join(", ", clsCatalogosCaptura.Tipologias)));
        }

        if (Recortar(linea.Carroceria) is { } carroceria && cubicadorCodigos.Carrocerias?.Contains(carroceria) != true
            && !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.Carrocerias, carroceria))
        {
            errores.Add(NoEsCodigo("carroceria", carroceria, cubicadorCodigos.Carrocerias is { Count: > 0 } delCubicador
                ? string.Join(", ", delCubicador.Order(StringComparer.Ordinal))
                : string.Join(", ", clsCatalogosCaptura.Carrocerias)));
        }

        return errores;
    }

    /// <summary>
    /// Tipo de vehículo que no es del cubicador (<c>codigo</c>), clase de viaje (<c>descripcion</c>) y clase de contenedor
    /// (<c>descripcion</c> de <c>clases-contenedor</c>, o la lista provisional G15) contra el catálogo maestro de Nexo.
    /// </summary>
    private static List<clsErrorCampo> ErroresMaestros(clsLineaSolicitudCosteo linea, clsCatalogosMaestros catalogos, clsCodigosCubicador cubicadorCodigos)
    {
        var errores = new List<clsErrorCampo>();
        if (Recortar(linea.TipoVehiculo) is { } tipoVehiculo && cubicadorCodigos.Vehiculos?.Contains(tipoVehiculo) != true
            && !EnCatalogo(catalogos.TiposVehiculo, tipoVehiculo))
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

    private static bool UsaCatalogoMaestro(clsLineaSolicitudCosteo linea, clsCodigosCubicador cubicadorCodigos) =>
        (Recortar(linea.TipoVehiculo) is { } tipo && cubicadorCodigos.Vehiculos?.Contains(tipo) != true)
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

    /// <summary>
    /// Los códigos activos de vehículo y de carrocería del cubicador (solo si alguna línea trae <c>tipo_vehiculo</c> o <c>carroceria</c>
    /// y el cubicador está configurado). Un catálogo que no responde queda en <c>null</c> (se valida con el respaldo) con un aviso en el
    /// log. Se lee una vez por petición.
    /// </summary>
    private async Task<clsCodigosCubicador> CodigosCubicadorAsync(IEnumerable<clsLineaSolicitudCosteo?> lineas, CancellationToken cancellationToken)
    {
        var lista = lineas.OfType<clsLineaSolicitudCosteo>().ToList();
        var pideVehiculo = lista.Any(l => Recortar(l.TipoVehiculo) is not null);
        var pideCarroceria = lista.Any(l => Recortar(l.Carroceria) is not null);
        if (cubicador is not { EstaConfigurado: true } || (!pideVehiculo && !pideCarroceria))
        {
            return new clsCodigosCubicador(null, null);
        }

        if (pideVehiculo && !_vehiculosCubicador.Leido)
        {
            HashSet<string>? vehiculos = null;
            var catalogo = await cubicador.ListarTiposVehiculoAsync(cancellationToken);
            if (catalogo.EsFallo)
            {
                logger.LogInformation("El catálogo de vehículos del cubicador no está disponible ({Codigo}): tipo_vehiculo se valida contra Nexo.", catalogo.Error.Codigo);
            }
            else
            {
                vehiculos = catalogo.Valor.Where(v => v.Activo).Select(v => v.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            _vehiculosCubicador = (vehiculos, true);
        }

        if (pideCarroceria && !_carroceriasCubicador.Leido)
        {
            HashSet<string>? carrocerias = null;
            var catalogo = await cubicador.ListarTiposCarroceriaAsync(cancellationToken);
            if (catalogo.EsFallo)
            {
                logger.LogInformation("El catálogo de carrocerías del cubicador no está disponible ({Codigo}): carroceria se valida con la lista fija.", catalogo.Error.Codigo);
            }
            else
            {
                carrocerias = catalogo.Valor.Where(c => c.Activo).Select(c => c.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            _carroceriasCubicador = (carrocerias, true);
        }

        return new clsCodigosCubicador(_vehiculosCubicador.Codigos, _carroceriasCubicador.Codigos);
    }

    /// <summary>Códigos activos del cubicador (<c>null</c> = catálogo no disponible o no consultado).</summary>
    private sealed record clsCodigosCubicador(IReadOnlySet<string>? Vehiculos, IReadOnlySet<string>? Carrocerias);

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
