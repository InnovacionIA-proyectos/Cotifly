using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Costeo de una cotización del comercial en la consola (HU-005.7, ADR-015 §5): verlo (solo precios, nunca el costo de Nexo),
/// elegir los servicios conexos que se incluyen y generar el PDF con el módulo de HU-003.5. Solo cotizaciones propias
/// (<c>COTIZACION_NO_ENCONTRADA</c> → 404). Las mutaciones exigen la versión de la fila (<c>If-Match</c>, ADR-018 §8) y siguen la
/// regla del PDF (ADR-018 §11, <see cref="clsReglaPdfCosteo"/>).
/// </summary>
public interface IClsCosteoCotizacionService
{
    Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Quita conexos o cambia su cantidad (HU-005.7; por ruta desde HU-021.8, #135, decisión 3; ver <see cref="clsCambioServiciosConexos"/>).
    /// Quitar un conexo BORRA su fila de la ruta y queda auditado (<see cref="HerramientaConexos"/>); la ruta y la cabecera se recalculan
    /// con el margen de la ruta. Errores: <c>CONFLICTO_CONCURRENCIA</c> (409) si <paramref name="versionEsperada"/> no es la de la fila;
    /// <c>COSTEO_NO_LISTO</c> (409) sin precio; <c>PDF_YA_ENVIADO</c> (409) ya enviada o con resultado; <c>SOLICITUD_INVALIDA</c> (400,
    /// forma: las dos formas juntas, cantidad fuera de rango o id repetido); <c>RUTA_REQUERIDA</c> y <c>RUTA_INEXISTENTE</c> (400);
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (400) con un id que no es conexo del costeo (o de la ruta). Si algo cambia, el PDF vigente queda
    /// invalidado (ADR-018 §11) y sube la versión; sin cambios responde igual sin tocar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken = default);

    /// <summary>
    /// PDF de la cotización con el flete a <c>precioFinal</c> y solo los servicios conexos incluidos, cada uno con su precio.
    /// <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_GENERADO</c> (PDF vigente), <c>PDF_YA_ENVIADO</c> y <c>CONFLICTO_CONCURRENCIA</c> (409: si viene
    /// <paramref name="versionEsperada"/> y no es la vigente, o si la cotización cambió mientras se generaba, aunque no venga), y los
    /// errores del módulo PDF. Con el PDF desactualizado, el anterior se archiva y se genera uno nuevo en el mismo folio.
    /// </summary>
    Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, int? versionEsperada = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>CosteoCotizacion</c>: estado del costeo y precios (sin costos de Nexo; HU-021: <see cref="PrecioFinal"/> es la suma del precio de
/// las rutas, que ya incluye sus conexos), el margen sugerido y su origen (HU-013.2), si el PDF
/// quedó desactualizado (ADR-018 §11), la versión de la fila (<c>row_version</c>, también en el <c>ETag</c>), la TRM fijada al crear
/// (<see cref="TrmAplicada"/>, HU-013.7; en USD los precios ya vienen convertidos con esa TRM) y el asunto y la introducción guardados
/// para el PDF (<c>textos</c>, HU-013.5; <c>null</c> si todavía no hay).
/// </summary>
public sealed record clsCosteoCotizacion(
    string Folio,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    DateTimeOffset? CosteoListoAt,
    decimal? PrecioFinal,
    decimal? MargenAplicado,
    string Moneda,
    IReadOnlyList<clsServicioConexoPrecio> ServiciosConexos,
    string? PdfUrl,
    decimal? MargenSugerido,
    string? OrigenMargen,
    bool PdfDesactualizado,
    string? RowVersion,
    clsTextosCotizacion? Textos = null) : IClsConVersionFila
{
    /// <summary>HU-013.7: TRM fijada al crear la cotización (COP por USD) con que se convierten sus precios; 0 en COP.</summary>
    public decimal TrmAplicada { get; init; }

    /// <summary>
    /// HU-021.8 (#135): las rutas en orden, cada una con su margen (aplicado, sugerido y origen), su precio y sus conexos. El ajuste de
    /// margen y los cambios de conexos dicen a cuál aplican con <c>linea_orden</c> o <c>linea_solicitud_id</c>. Nunca trae costos.
    /// </summary>
    public IReadOnlyList<clsRutaCosteo> Rutas { get; init; } = [];
}

/// <summary>
/// Servicio conexo tal como lo ve el comercial: precio con el margen de su ruta y si va en el PDF (HU-021: siempre). <see cref="LineaOrden"/>
/// es la ruta a la que pertenece (HU-021.8).
/// </summary>
public sealed record clsServicioConexoPrecio(
    string Id, string? Nombre, string Categoria, decimal Cantidad, decimal Precio, bool Incluido, int? LineaOrden = null)
{
    /// <summary>
    /// Decisión 10 (HU-021.9): estado del envío diferido del conexo a la cotización de Nexo: <c>pendiente</c>, <c>enviado</c>,
    /// <c>incierto</c> (no se pudo confirmar: revisar Nexo; no se reintenta solo) o <c>fallido_definitivo</c>. Solo en
    /// <c>GET .../costeo</c>; <c>null</c> en las respuestas de las mutaciones y antes de enviar la solicitud a Nexo.
    /// </summary>
    public string? EstadoNexo { get; init; }
}

/// <summary>
/// <c>RutaCosteo</c> (HU-021.8, #135): una ruta del costeo como la ve el comercial, sin costos. <see cref="MargenSugerido"/> es el de SAP
/// TM o, si no hay, el de la política de su línea (<c>margenSAPSugerido</c> o <c>margenPoliticaSugerido</c>): por debajo de él el ajuste
/// de ESTA ruta exige justificación.
/// </summary>
public sealed record clsRutaCosteo(
    int LineaOrden,
    Guid LineaSolicitudId,
    string? Origen,
    string? Destino,
    string? EstadoCosteo,
    decimal? PrecioFinalLinea,
    decimal? MargenAplicado,
    decimal? MargenSugerido,
    string? OrigenMargen,
    IReadOnlyList<clsServicioConexoPrecio> ServiciosConexos)
{
    /// <summary>HU-021.9 (#136): línea de negocio de la ruta (id de <c>MargenesPolitica</c>; define su margen).</summary>
    public int? LineaNegocioId { get; init; }

    /// <summary>HU-021.9: peso total que declaró el comercial para la ruta.</summary>
    public decimal? PesoKg { get; init; }

    /// <summary>
    /// HU-021.9: los camiones de la ruta en su orden estable (el primero vigente lleva los conexos en Nexo), sin costos ni ids de Nexo.
    /// Los <c>descartada</c> vienen con su estado: no cuentan en el precio.
    /// </summary>
    public IReadOnlyList<clsCamionCosteo> Camiones { get; init; } = [];

    /// <summary>HU-021.9: los camiones agrupados como los imprime el PDF ("2 × 3S3 - FURGON (34000 KG)"); <c>null</c> sin datos.</summary>
    public string? Vehiculos { get; init; }

    /// <summary>HU-021.9: las mercancías de la ruta, en su orden.</summary>
    public IReadOnlyList<clsMercanciaCosteo> Mercancias { get; init; } = [];

    /// <summary>
    /// Decisión 10 (HU-021.9): aviso cuando algún conexo de la ruta quedó <c>incierto</c> o <c>fallido_definitivo</c> en su envío a Nexo
    /// (el comercial o la Mesa deben revisarlo en Nexo); <c>null</c> si no hay nada que revisar. Solo en <c>GET .../costeo</c>.
    /// </summary>
    public string? AvisoConexosNexo { get; init; }
}

/// <summary>
/// <c>CamionCosteo</c> (HU-021.9, #136): un camión de la ruta como lo ve el comercial. Vehículo y carrocería vacíos = sin selección
/// automática (los resuelve la Mesa de Costeo de Nexo, decisión 7). Sin costos ni ids de Nexo.
/// </summary>
public sealed record clsCamionCosteo(string? TipoVehiculo, string? Carroceria, decimal? PesoKg, string? EstadoCosteo);

/// <summary><c>MercanciaCosteo</c> (HU-021.9, #136): una mercancía de la ruta (tipo del catálogo del cubicador, nombre, medidas y peso).</summary>
public sealed record clsMercanciaCosteo(
    int Orden, Guid? IdTipoMercancia, string? NombreMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);

/// <summary>PDF del costeo generado (<c>SalidaPdf</c>) y la versión nueva de la fila (va en el <c>ETag</c>, no en el cuerpo).</summary>
public sealed record clsPdfCosteoGenerado(clsSalidaPdf Pdf, string? RowVersion);

/// <summary>Implementación de <see cref="IClsCosteoCotizacionService"/>.</summary>
public sealed class clsCosteoCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsUsuarioWebRepository usuarios,
    IClsGeneradorPdf generador,
    IClsAlmacenPdf almacen,
    IClsUnitOfWork unidadTrabajo,
    IClsCalendarioNegocio calendario,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsCosteoCotizacionService> logger,
    IClsEnvioConexosNexoService? envioConexos = null) : IClsCosteoCotizacionService
{
    public const string HerramientaPdf = "generar_pdf_costeo";

    /// <summary>Nombres <c>{folio}-hN.pdf</c> que se prueban al archivar un PDF invalidado.</summary>
    private const int MaximoIntentosArchivo = 20;

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> al elegir conexos de una cotización ya entregada.</summary>
    public const string MotivoVersionConexos = "Cambio de servicios conexos después del envío";

    public async Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        // Decisión 10 (HU-021.9): el estado del envío diferido de cada conexo a Nexo (incierto o fallido definitivo quedan a la vista).
        var estadosNexo = envioConexos is null ? null : await envioConexos.EstadosAsync(cotizacion, cancellationToken);
        return Vista(cotizacion, opcionesPdf.Value.BaseUrl, estadosNexo);
    }

    /// <summary>Herramienta de la auditoría de los cambios de conexos (<c>EventosHerramienta.herramienta</c>, HU-021.8).</summary>
    public const string HerramientaConexos = "cambiar_servicios_conexos";

    public async Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cambio);
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var (resultado, movimientos, pdfInvalidado) = await CambiarConexosAsync(cotizacion, usuarioId, cambio, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaConexos,
            new
            {
                folio = cotizacion.Folio,
                linea_orden = cambio.Ruta.LineaOrden,
                linea_solicitud_id = cambio.Ruta.LineaSolicitudId,
                incluidos = cambio.Incluidos,
                conexos = cambio.Conexos?.Select(c => new { id = c?.Id, cantidad = c?.Cantidad }).ToList(),
            },
            resultado.EsExitoso
                ? (object)new
                {
                    // Sin montos: qué conexo de qué ruta se quitó (fila borrada) o cambió de cantidad.
                    movimientos = movimientos.Select(m => new
                    {
                        linea_orden = m.Orden,
                        linea_solicitud_id = m.RutaId,
                        id = m.Movimiento.IdServicioConexo,
                        nombre = m.Movimiento.Nombre,
                        cantidad_anterior = m.Movimiento.CantidadAnterior,
                        cantidad_nueva = m.Movimiento.CantidadNueva,
                        quitado = m.Movimiento.Quitado,
                    }).ToList(),
                    pdf_invalidado = pdfInvalidado,
                }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, IReadOnlyList<(int Orden, Guid RutaId, clsMovimientoConexo Movimiento)> Movimientos, bool PdfInvalidado)> CambiarConexosAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsCambioServiciosConexos cambio, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        if (SinCosteo(cotizacion) is { } sinCosteo)
        {
            return (sinCosteo, [], false);
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersionConexos) is { } entregada)
        {
            return (entregada, [], false);
        }

        // HU-021 (ADR-019): los conexos son filas de CotizacionServiciosConexos en sus rutas; un conexo excluido se BORRA de la ruta (la tabla
        // no tiene "incluido": la fila es lo que va en el precio y en el PDF). Para volver a tenerlo se agrega de nuevo con el POST.
        var plan = PlanConexos(cotizacion, cambio);
        if (plan.EsFallo)
        {
            return (plan.Error, [], false);
        }

        var ahora = cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt;
        var movimientos = new List<(int, Guid, clsMovimientoConexo)>();
        foreach (var (ruta, cantidades) in plan.Valor)
        {
            var ajustados = cotizacion.AjustarConexosRuta(ruta.Orden, cantidades, ahora);
            if (ajustados.EsFallo)
            {
                return (ajustados.Error, [], false);
            }

            movimientos.AddRange(ajustados.Valor.Select(m => (ruta.Orden, ruta.Id, m)));
        }

        if (movimientos.Count == 0)
        {
            // La misma selección y las mismas cantidades no cambian nada (ni invalidan el PDF): idempotente.
            return (Vista(cotizacion, opcionesPdf.Value.BaseUrl), [], false);
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? (Vista(cotizacion, opcionesPdf.Value.BaseUrl), movimientos, pdfInvalidado)
            : (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
    }

    /// <summary>
    /// Qué conexos deben quedar en cada ruta afectada (id → cantidad), sin tocar la cotización: valida la forma, la ruta y que cada id
    /// sea un conexo de la ruta (o del costeo, con <c>incluidos</c> sin ruta).
    /// </summary>
    public static clsResultado<IReadOnlyList<(clsLineaSolicitud Ruta, IReadOnlyDictionary<string, decimal> Cantidades)>> PlanConexos(
        clsCotizacionSolicitud cotizacion, clsCambioServiciosConexos cambio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cambio);
        if (cambio.Conexos is not null && cambio.Incluidos is not null)
        {
            return Invalida("Envía 'conexos' (la ruta con sus cantidades) o 'incluidos' (los que se conservan), no los dos.");
        }

        if (cambio.Conexos is null && cambio.Incluidos is null)
        {
            // Sin ninguna de las dos formas no se adivina: un cuerpo como {"linea_orden": 2} no borra los conexos de la ruta.
            return Invalida("Envía 'conexos' (la ruta con sus cantidades) o 'incluidos' (los que se conservan; [] los quita todos).");
        }

        var indicada = cambio.Ruta.LineaOrden is not null || cambio.Ruta.LineaSolicitudId is not null;
        if (cambio.Conexos is null && !indicada)
        {
            // HU-005.7: 'incluidos' sin ruta conserva esos ids en TODAS las rutas.
            var incluidos = cambio.Incluidos!;
            var todos = cotizacion.Rutas.SelectMany(r => r.Conexos).ToList();
            if (Ajenos(incluidos, todos) is { Count: > 0 } desconocidos)
            {
                return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                    $"Estos servicios conexos no son del costeo de la cotización {cotizacion.Folio}: {string.Join(", ", desconocidos)}.");
            }

            return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito(
                [.. cotizacion.RutasOrdenadas.Select(r => (r, Conservar(r, incluidos)))]);
        }

        var resuelta = cotizacion.ResolverRuta(cambio.Ruta.LineaOrden, cambio.Ruta.LineaSolicitudId);
        if (resuelta.EsFallo)
        {
            return resuelta.Error;
        }

        var ruta = resuelta.Valor;
        if (cambio.Conexos is null)
        {
            var incluidos = cambio.Incluidos ?? [];
            if (Ajenos(incluidos, ruta.Conexos) is { Count: > 0 } ajenos)
            {
                return NoDeLaRuta(cotizacion, ruta, ajenos);
            }

            return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito([(ruta, Conservar(ruta, incluidos))]);
        }

        var cantidades = new Dictionary<string, decimal>(ComparadorIdConexo);
        for (var i = 0; i < cambio.Conexos.Count; i++)
        {
            var item = cambio.Conexos[i];
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
            {
                return Invalida($"conexos[{i}].id es obligatorio.");
            }

            if (item.Cantidad is not { } cantidad || cantidad < clsServiciosConexosCosteoService.CantidadMinima
                || cantidad > clsServiciosConexosCosteoService.CantidadMaxima || decimal.Round(cantidad, 2) != cantidad)
            {
                return Invalida($"conexos[{i}].cantidad debe estar entre {clsServiciosConexosCosteoService.CantidadMinima.ToString(System.Globalization.CultureInfo.InvariantCulture)} y {clsServiciosConexosCosteoService.CantidadMaxima.ToString(System.Globalization.CultureInfo.InvariantCulture)}, con hasta 2 decimales.");
            }

            if (!cantidades.TryAdd(item.Id.Trim(), cantidad))
            {
                return Invalida($"conexos[{i}].id está repetido.");
            }
        }

        if (Ajenos(cantidades.Keys, ruta.Conexos) is { Count: > 0 } noEstan)
        {
            return NoDeLaRuta(cotizacion, ruta, noEstan);
        }

        return clsResultado<IReadOnlyList<(clsLineaSolicitud, IReadOnlyDictionary<string, decimal>)>>.Exito([(ruta, cantidades)]);
    }

    /// <summary>
    /// Un solo criterio para comparar ids de conexos en la validación y en la aplicación: <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// (el mismo del dominio, <c>clsLineaSolicitud.AjustarConexos</c>). Así un id que pasa la validación es el mismo que se conserva.
    /// </summary>
    public static readonly StringComparer ComparadorIdConexo = StringComparer.OrdinalIgnoreCase;

    private static List<string> Ajenos(IEnumerable<string> ids, IEnumerable<clsServicioConexoRuta> conexos)
    {
        var propios = conexos.Select(c => c.IdServicioConexo).ToHashSet(ComparadorIdConexo);
        return [.. ids.Where(id => !propios.Contains(id))];
    }

    /// <summary>Los conexos de la ruta que están en <paramref name="incluidos"/>, con su cantidad actual (los demás se quitan).</summary>
    private static IReadOnlyDictionary<string, decimal> Conservar(clsLineaSolicitud ruta, IReadOnlyCollection<string> incluidos)
    {
        var conservar = incluidos.ToHashSet(ComparadorIdConexo);
        return ruta.Conexos.Where(c => conservar.Contains(c.IdServicioConexo))
            .ToDictionary(c => c.IdServicioConexo, c => c.Cantidad, ComparadorIdConexo);
    }

    private static clsErrorDominio NoDeLaRuta(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IEnumerable<string> ids) =>
        new(clsCodigosError.SolicitudCosteoInvalida,
            $"Estos servicios conexos no son de la ruta {ruta.Orden} de la cotización {cotizacion.Folio}: {string.Join(", ", ids)}. Para agregar uno nuevo usa el POST.");

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);

    public async Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, int? versionEsperada = null, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var resultado = await GenerarAsync(cotizacion, usuarioId, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaPdf,
            new { folio = cotizacion.Folio },
            resultado.EsExitoso ? (object)resultado.Valor.Pdf : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<clsResultado<clsPdfCosteoGenerado>> GenerarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, int? versionEsperada, CancellationToken cancellationToken)
    {
        // ADR-018 §8: aunque el cliente no envíe If-Match, el PDF se confirma solo sobre la versión que se cargó (no queda vigente
        // un PDF con un precio que otro flujo cambió mientras se generaba).
        var versionCargada = cotizacion.VersionNegocio;
        if (versionEsperada is { } esperada && !cotizacion.TieneVersion(esperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        if (Bloqueo(cotizacion) is { } bloqueo)
        {
            return bloqueo;
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario is null)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "No se encontró el comercial autenticado en UsuariosWeb.");
        }

        // HU-021.9 (#136): la línea de negocio es de cada ruta. El título y el asunto llevan la de la solicitud si todas sus rutas tienen
        // la misma; en el pliego, cada sección lleva la de su ruta.
        var nombres = await clsLineasNegocioRutas.NombresAsync(cotizacion, politicas, cancellationToken);
        var lineaNegocio = clsLineasNegocioRutas.Comun(cotizacion, nombres);
        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;

        // Todas las rutas con precio y el total = la suma de sus precios (con los conexos), en una ruta y en el pliego.
        if (ValidarTotal(cotizacion, moneda) is { } noListo)
        {
            return noListo;
        }

        // HU-021.9: el layout de pliego es para una solicitud de más de una ruta (formulario o licitación); con una ruta, el PDF de la ruta.
        var esPliego = EsPliego(cotizacion);
        var payload = esPliego ? PayloadPliego(cotizacion, lineaNegocio, moneda, nombres) : Payload(cotizacion, lineaNegocio, moneda);
        AplicarTextos(payload, cotizacion);
        var invalido = esPliego
            ? clsValidadorCotizacionPdfPayload.Validar(payload, MaximoLineasPliego, MaximoLineasPliego)
            : clsValidadorCotizacionPdfPayload.Validar(payload);
        if (invalido is not null)
        {
            return invalido;
        }

        // En el pliego cada fila tiene su propio precio: aquí solo la moneda. En una ruta, la tarifa impresa es el precio del transporte
        // (fletes, retorno y levante con el margen de la ruta) y los conexos van aparte con su precio (HU-021).
        var primera = cotizacion.PrimeraRuta;
        var transporte = esPliego || primera is null ? null : PrecioTransporte(cotizacion, primera);
        var traza = new clsTrazabilidadCotizacion(
            esPliego ? null : primera?.CostoTransporte, primera?.MargenPoliticaSugerido, primera?.MargenAplicado, null, primera?.MetodoCalculo,
            transporte, cotizacion.NitCliente, null, moneda, cotizacion.TrmAplicada, cotizacion.SolicitudNexoId);
        if (clsGenerarPdfService.ValidarCoherencia(payload, traza, primera?.MetodoCalculo ?? "margen", moneda) is { } incoherente)
        {
            return incoherente;
        }

        var firma = new clsFirmaDocumento(usuario.NombreCompleto ?? usuario.NombreUsuario ?? string.Empty, null, usuario.Email, clsTextoSeguro.LimpiarLinea(usuario.Telefono));
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, firma, calendario.Hoy, moneda, cotizacion.TrmAplicada);
        if (documento.EsFallo)
        {
            return documento.Error;
        }

        var aRenderizar = esPliego ? DocumentoPliego(documento.Valor, cotizacion, moneda) : documento.Valor;

        clsPdfGenerado pdf;
        try
        {
            pdf = await generador.GenerarAsync(aRenderizar, cancellationToken);
        }
        catch (clsRenderPdfOcupadoException)
        {
            // El turno de render no llegó a tiempo (#92/#95): ya quedó en el log del generador; es transitorio (503).
            return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "El generador de PDF está ocupado. Reintenta en unos segundos.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo renderizar el PDF del costeo del folio {Folio}.", cotizacion.Folio);
            return new clsErrorDominio(clsCodigosError.PdfRenderNoDisponible, "No se pudo generar el PDF de la cotización. Reintenta más tarde o contacta a soporte.");
        }

        var nombre = cotizacion.Folio + ".pdf";
        if (cotizacion.PdfDesactualizado)
        {
            // ADR-018 §11: el PDF invalidado ocupa el nombre del folio; se archiva (sin borrarlo) para generar el nuevo con ese nombre.
            await ArchivarPdfAnteriorAsync(cotizacion, nombre, cancellationToken);
        }

        var ruta = await almacen.GuardarNuevoAsync(nombre, pdf.Contenido, cancellationToken);
        if (ruta is null)
        {
            return new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"Ya existe el PDF {nombre} en el almacén: genera una versión nueva.");
        }

        cotizacion.AsignarPdf(ruta);
        cotizacion.IncrementarVersionNegocio();
        bool guardado;
        try
        {
            guardado = await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionCargada, CancellationToken.None);
        }
        catch
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            throw;
        }

        if (!guardado)
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        return new clsPdfCosteoGenerado(
            new clsSalidaPdf(cotizacion.Id, cotizacion.Folio, clsUrlPdf.Construir(opcionesPdf.Value.BaseUrl, nombre), RegistradaEnBd: true),
            VersionEtag(cotizacion));
    }

    /// <summary>
    /// Archiva el PDF invalidado que ocupa <paramref name="nombre"/>: lo copia como <c>{folio}-hN.pdf</c>, actualiza su referencia en el
    /// historial (lo guarda de inmediato, antes del PDF nuevo) y libera el nombre. Si el archivo ya no está, no hay nada que archivar.
    /// </summary>
    private async Task ArchivarPdfAnteriorAsync(clsCotizacionSolicitud cotizacion, string nombre, CancellationToken cancellationToken)
    {
        var historial = await cotizaciones.ListarHistorialPdfAsync(cotizacion.Id, cancellationToken);
        var ocupante = historial.LastOrDefault(h => string.Equals(clsUrlPdf.NombreArchivo(h.PdfPath), nombre, StringComparison.Ordinal));
        var contenido = ocupante is null ? null : await almacen.LeerPorReferenciaAsync(ocupante.PdfPath, cancellationToken);
        if (ocupante is null || contenido is null)
        {
            return;
        }

        string? archivado = null;
        for (var n = historial.Count; archivado is null && n < historial.Count + MaximoIntentosArchivo; n++)
        {
            archivado = await almacen.GuardarNuevoAsync($"{cotizacion.Folio}-h{n}.pdf", contenido, cancellationToken);
        }

        if (archivado is null)
        {
            return; // sin nombre libre: el PDF nuevo responderá PDF_YA_GENERADO y el anterior queda intacto.
        }

        ocupante.Archivar(archivado);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        await almacen.EliminarAsync(nombre, cancellationToken);
    }

    /// <summary>Versión de negocio como texto (el valor opaco del <c>ETag</c> y de <c>row_version</c>).</summary>
    public static string VersionEtag(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.VersionNegocio.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Asunto e introducción que imprime el PDF de la cotización sin textos de la IA ni del comercial: los de tarifa única (con la
    /// línea) o, en un pliego (<see cref="EsPliego"/>), el asunto de licitación y la introducción de <c>MultiSeccion</c>. Son el
    /// respaldo del redactor (HU-013.5): lo que ve el comercial es exactamente lo que sale en el PDF.
    /// </summary>
    public static clsTextosBase TextosEstandar(clsCotizacionSolicitud cotizacion, string? lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var esPliego = EsPliego(cotizacion);
        return new clsTextosBase(
            esPliego ? clsTextosEstandar.AsuntoLicitacion(null, cotizacion.Rutas.Count) : clsTextosEstandar.AsuntoEstandar(lineaNegocio),
            clsTextosEstandar.IntroduccionEstandar(esPliego));
    }

    /// <summary>
    /// HU-013.5 (ADR-018 §2a): el asunto y la introducción guardados (de la IA ya validados, o del comercial) reemplazan los del PDF.
    /// Sin textos, o con los estándar sin editar, el PDF usa los suyos (los de <see cref="clsTextosEstandar"/> de su variante). Nunca
    /// llama a la IA: el PDF siempre sale. Las condiciones, las notas legales y la nota de TRM no cambian.
    /// </summary>
    public static void AplicarTextos(clsCotizacionPdfPayload payload, clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion) is not { } textos || !textos.ReemplazanLosDelPdf())
        {
            return;
        }

        payload.Asunto = textos.Asunto;
        payload.Textos ??= new clsTextosPayload();
        payload.Textos.Intro = [.. textos.Parrafos()];
    }

    /// <summary>
    /// Payload del PDF de una ruta armado desde la cotización (nada del LLM ni del cliente): una fila con el origen, el destino, los
    /// camiones y el precio del transporte de la ruta (fletes, retorno y levante con su margen); los servicios conexos de la ruta, cada uno
    /// con su precio (HU-021: el precio de la ruta suma los dos).
    /// </summary>
    public static clsCotizacionPdfPayload Payload(clsCotizacionSolicitud cotizacion, string? lineaNegocio, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ruta = cotizacion.PrimeraRuta;
        var conexos = ruta is null ? [] : ConexosConPrecio(cotizacion, ruta);
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio,
            MetodoCalculo = ruta?.MetodoCalculo,
            Tarifa = new clsTarifaPayload
            {
                Titulo = lineaNegocio is null ? "COTIZACIÓN DE TRANSPORTE" : $"COTIZACIÓN DE TRANSPORTE {lineaNegocio.ToUpperInvariant()}",
                Origen = ruta?.OrigenNombre,
                Filas =
                [
                    new clsFilaTarifaPayload
                    {
                        Destino = ruta?.DestinoNombre,
                        VehiculoPeso = ruta is null ? null : Camiones(ruta),
                        Tarifa = ruta is null ? null : PrecioTransporte(cotizacion, ruta),
                        Moneda = moneda,
                    },
                ],
            },
            ServiciosConexos = conexos.Count == 0
                ? null
                : [.. conexos.Select(s => new clsServicioConexoPayload { Descripcion = s.Nombre ?? s.Categoria, Precio = s.Precio, Moneda = moneda })],
        };
    }

    /// <summary>
    /// Payload del PDF de un pliego (HU-011.5; por ruta desde HU-021.9, #136): una sección por ruta, en su orden, con su título (y la
    /// línea de negocio de la ruta si se conoce: <paramref name="lineasPorId"/>, si no <paramref name="lineaNegocio"/>), su origen y una
    /// fila con el destino, sus camiones agrupados y la tarifa del transporte de la ruta (sin conexos: van aparte, con su precio, para no
    /// cobrarlos dos veces; el precio de la ruta los incluye). Nada del LLM ni del cliente.
    /// </summary>
    public static clsCotizacionPdfPayload PayloadPliego(
        clsCotizacionSolicitud cotizacion, string? lineaNegocio, string moneda, IReadOnlyDictionary<int, string>? lineasPorId = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        var conexos = rutas.SelectMany(r => ConexosConPrecio(cotizacion, r)).ToList();
        var secciones = rutas
            .Select(r =>
            {
                var linea = (lineasPorId is null ? null : clsLineasNegocioRutas.DeRuta(r, lineasPorId)) ?? lineaNegocio;
                return new clsTarifaPayload
                {
                    Titulo = $"RUTA {r.Orden}: " + (linea is null ? "PLIEGO DE TRANSPORTE" : $"PLIEGO DE TRANSPORTE {linea.ToUpperInvariant()}"),
                    Origen = r.OrigenNombre,
                    Filas =
                    [
                        new clsFilaTarifaPayload
                        {
                            Destino = r.DestinoNombre,
                            VehiculoPeso = Camiones(r),
                            Tarifa = PrecioTransporte(cotizacion, r),
                            Moneda = moneda,
                        },
                    ],
                };
            })
            .ToList();
        // Cabecera del pliego: la línea común, o "Varias" si las rutas tienen líneas distintas (cada sección lleva la de su ruta).
        var lineasDeRutas = lineasPorId is null
            ? []
            : rutas.Select(r => clsLineasNegocioRutas.DeRuta(r, lineasPorId)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio ?? (lineasDeRutas.Count > 1 ? Cotizaciones.DTOs.clsCotizacionConLinea.Varias : lineasDeRutas.FirstOrDefault()),
            MetodoCalculo = cotizacion.PrimeraRuta?.MetodoCalculo,
            Asunto = clsTextosEstandar.AsuntoLicitacion(null, rutas.Count),
            Secciones = secciones,
            ServiciosConexos = conexos.Count == 0
                ? null
                : [.. conexos.Select(s => new clsServicioConexoPayload { Descripcion = s.Nombre ?? s.Categoria, Precio = s.Precio, Moneda = moneda })],
        };
    }

    /// <summary>
    /// Resumen del pliego: una fila por ruta, en orden, con la tarifa del transporte, y el total (la suma de esas tarifas, sin servicios
    /// conexos; HU-021: <c>PrecioFinal</c> sí los incluye, por eso no se usa aquí).
    /// </summary>
    public static clsResumenPliegoDocumento ResumenPliego(clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        var tarifas = rutas.Select(r => PrecioTransporte(cotizacion, r)).ToList();
        var filas = rutas.Select((r, i) => new clsFilaPliegoDocumento(
            r.Orden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clsTextoSeguro.LimpiarLinea($"{r.OrigenNombre} → {r.DestinoNombre}") ?? string.Empty,
            Camiones(r) ?? "—",
            tarifas[i] is { } precio ? clsFormatoMoneda.Formatear(precio, moneda) : "—")).ToList();
        return new clsResumenPliegoDocumento(
            "RESUMEN DEL PLIEGO",
            filas,
            $"TOTAL DEL PLIEGO ({filas.Count} RUTAS, SIN SERVICIOS CONEXOS)",
            clsFormatoMoneda.Formatear(tarifas.Sum(t => t ?? 0m), moneda),
            "Tarifas por viaje de cada ruta; el total es la suma de un viaje por ruta, sin servicios conexos, y es solo de referencia.");
    }

    /// <summary>Filas que admite el PDF de pliego: las mismas 1000 líneas de una licitación.</summary>
    public const int MaximoLineasPliego = Licitaciones.Validators.clsValidadorLicitacion.MaximoLineas;

    /// <summary>
    /// Por encima de estas rutas el pliego sale COMPACTO: solo el resumen (que ya trae todas las rutas con su tarifa), sin la sección de
    /// cada ruta, que repetiría las mismas filas en decenas de páginas (el máximo de tablas de un PDF de tarifas).
    /// </summary>
    public const int UmbralRutasCompacto = clsValidadorCotizacionPdfPayload.MaxSecciones;

    /// <summary>¿El PDF sale con el layout de pliego? HU-021.9 (#136): cuando la solicitud tiene más de una ruta (formulario o licitación).</summary>
    public static bool EsPliego(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > 1;
    }

    /// <summary>¿Las rutas del pliego pasan del umbral del layout completo (una sección por ruta)?</summary>
    public static bool EsPliegoCompacto(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > UmbralRutasCompacto;
    }

    /// <summary>
    /// Condiciones del PDF, de una ruta o de pliego (409 <c>COSTEO_NO_LISTO</c> si no se cumplen; HU-021.9): la solicitud tiene rutas,
    /// TODAS tienen precio (el ajuste de margen es por ruta, pero el PDF exige todas) y el total de la solicitud es la suma de esos precios
    /// (<c>precioFinal</c> = Σ <c>precioFinalLinea</c>, con los conexos incluidos: nada se inventa en el PDF). En USD (ADR-014 §5) los
    /// precios ya vienen convertidos con la <c>trmAplicada</c>.
    /// </summary>
    public static clsErrorDominio? ValidarTotal(clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (string.IsNullOrWhiteSpace(moneda))
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, $"La cotización {cotizacion.Folio} no trae la moneda.");
        }

        if (cotizacion.Rutas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} no tiene rutas.");
        }

        if (cotizacion.RutasOrdenadas.FirstOrDefault(r => r.PrecioFinalLinea is null) is { } sinPrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La ruta {sinPrecio.Orden} de la cotización {cotizacion.Folio} todavía no tiene precio.");
        }

        var suma = cotizacion.Rutas.Sum(r => r.PrecioFinalLinea!.Value);
        return suma == cotizacion.PrecioFinal
            ? null
            : new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"El total de la cotización {cotizacion.Folio} ({cotizacion.PrecioFinal}) todavía no es la suma de sus rutas ({suma}): espera la siguiente consulta del costeo.");
    }

    /// <summary>
    /// Documento del pliego: la variante <see cref="VarianteDocumento.Pliego"/> con el resumen y, en modo compacto
    /// (<see cref="EsPliegoCompacto"/>), sin las secciones de cada ruta.
    /// </summary>
    public static clsDocumentoCotizacion DocumentoPliego(clsDocumentoCotizacion documento, clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(documento);
        var compacto = EsPliegoCompacto(cotizacion);
        var resumen = ResumenPliego(cotizacion, moneda);
        return documento with
        {
            Variante = VarianteDocumento.Pliego,
            Pliego = compacto
                ? resumen with { Nota = resumen.Nota + $" Con más de {UmbralRutasCompacto} rutas el PDF solo trae este resumen." }
                : resumen,
            Secciones = compacto ? [] : documento.Secciones,
        };
    }

    /// <summary>
    /// Camiones de la ruta para el PDF (HU-021.9): los que cuentan (un camión descartado en Nexo no se imprime), agrupando los iguales. Con
    /// uno, "vehículo - carrocería - peso KG"; con varios, cada vehículo con cuántos van y el peso total de la ruta
    /// ("2 × 3S3 - FURGON, 1 × Vehículo por definir (34000 KG)"). <c>null</c> si no hay nada que mostrar.
    /// </summary>
    public static string? Camiones(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var camiones = ruta.VehiculosQueCuentan.OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToList();
        if (camiones.Count <= 1)
        {
            var unico = camiones.Count == 1 ? camiones[0] : null;
            return VehiculoPeso(unico?.TipoVehiculo, unico?.Carroceria, unico?.PesoKg ?? ruta.PesoKg);
        }

        var grupos = camiones
            .GroupBy(c => VehiculoPeso(c.TipoVehiculo, c.Carroceria, null) ?? VehiculoPorDefinir, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} × {g.Key}");
        var pesoCamiones = camiones.All(c => c.PesoKg is not null) ? camiones.Sum(c => c.PesoKg!.Value) : (decimal?)null;
        var peso = (pesoCamiones ?? ruta.PesoKg) is { } total ? $" ({total.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG)" : string.Empty;
        return string.Join(", ", grupos) + peso;
    }

    /// <summary>Texto de un camión sin vehículo ni carrocería (los resuelve la Mesa de Costeo de Nexo, decisión 7).</summary>
    public const string VehiculoPorDefinir = "Vehículo por definir";

    /// <summary>
    /// Precio del transporte de la ruta (fletes, retorno y levante con su margen), en la moneda de la solicitud; <c>null</c> sin costo.
    /// Es la tarifa que ven el PDF y el Excel: los conexos van aparte (<see cref="ConexosConPrecio"/>), así que la tarifa + los conexos
    /// tienen que dar exactamente <c>precioFinalLinea</c>. Cada precio se redondea por separado; la diferencia de redondeo (a lo sumo
    /// una unidad por conexo) la absorbe la tarifa: <c>precioFinalLinea − conexos</c>. Si la diferencia es mayor, la ruta no incluye
    /// los conexos en su precio (cotización migrada antes de HU-021) y la tarifa es el transporte calculado.
    /// </summary>
    public static decimal? PrecioTransporte(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        if (ruta.PrecioDe(ruta.CostoTransporte, cotizacion.Moneda, cotizacion.TrmAplicada, out var precio) is not null || precio is not { } transporte)
        {
            return null;
        }

        var conexos = ConexosConPrecio(cotizacion, ruta);
        if (ruta.PrecioFinalLinea is { } total && conexos.Count > 0)
        {
            // Tolerancia por moneda (HU-021.9): cada precio se redondea a la unidad de su moneda (1 peso en COP, 0,01 en USD), así que la
            // diferencia entre la ruta y transporte + conexos es a lo sumo media unidad por redondeo: (n + 1) / 2 unidades con n conexos,
            // que nunca pasa de n unidades. Una tolerancia de 1 por conexo en USD dejaba pasar un dólar entero por conexo.
            var ajustado = total - conexos.Sum(c => c.Precio);
            var unidad = Dominio.Precio.clsConversorMoneda.EsPesos(cotizacion.Moneda) ? 1m : 0.01m;
            if (Math.Abs(ajustado - transporte) <= conexos.Count * unidad)
            {
                return ajustado;
            }
        }

        return transporte;
    }

    /// <summary>Conexos de la ruta como los ve el comercial: precio con el margen de la ruta (nunca el costo de Nexo); todos incluidos.</summary>
    public static IReadOnlyList<clsServicioConexoPrecio> ConexosConPrecio(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        return [.. ruta.Conexos.OrderBy(c => c.CreatedAt).ThenBy(c => c.IdServicioConexo, StringComparer.Ordinal)
            .Select(c => (Conexo: c, Error: ruta.PrecioDe(c.Costo, cotizacion.Moneda, cotizacion.TrmAplicada, out var precio), Precio: precio))
            .Where(x => x.Error is null && x.Precio is not null)
            .Select(x => new clsServicioConexoPrecio(x.Conexo.IdServicioConexo, x.Conexo.Nombre, CategoriaConexo, x.Conexo.Cantidad, x.Precio!.Value, true, ruta.Orden))];
    }

    /// <summary>Categoría con la que se muestra un conexo (la tabla de HU-021 no la guarda).</summary>
    public const string CategoriaConexo = "logistico";

    private static string? VehiculoPeso(string? tipoVehiculo, string? carroceria, decimal? pesoKg)
    {
        var texto = string.Join(" - ", new[]
        {
            tipoVehiculo, carroceria,
            pesoKg is { } peso ? $"{peso.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG" : null,
        }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return texto.Length == 0 ? null : texto;
    }

    private async Task<clsCotizacionSolicitud?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    /// <summary><c>COSTEO_NO_LISTO</c> si la cotización todavía no tiene el costeo de Nexo con precio.</summary>
    private static clsErrorDominio? SinCosteo(clsCotizacionSolicitud cotizacion) => cotizacion.TieneCostoDisponible
        ? null
        : new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado.");

    /// <summary>Condiciones para generar el PDF: costeo con precio, sin PDF vigente (<c>PDF_YA_GENERADO</c>) y sin envío (<c>PDF_YA_ENVIADO</c>).</summary>
    private static clsErrorDominio? Bloqueo(clsCotizacionSolicitud cotizacion)
    {
        if (SinCosteo(cotizacion) is { } sinCosteo)
        {
            return sinCosteo;
        }

        if (cotizacion.PdfPath is not null)
        {
            return new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"La cotización {cotizacion.Folio} ya tiene PDF: genera una versión nueva para cambiarla.");
        }

        return cotizacion.ValidarCambioEtapaFinal();
    }

    /// <summary>
    /// <c>CosteoCotizacion</c> de la cotización (sin costos de Nexo). La comparten el costeo, el ajuste de margen y las demás acciones de
    /// la etapa final.
    /// </summary>
    public static clsCosteoCotizacion Vista(
        clsCotizacionSolicitud cotizacion, string? baseUrlPdf, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);

        // HU-021: el margen y su sugerido de primer nivel son los de la ruta 1 (compatibilidad); cada ruta trae los suyos en `rutas`
        // (HU-021.8, #135). El precio es la suma de las rutas.
        var ruta = cotizacion.PrimeraRuta;
        return new clsCosteoCotizacion(
            cotizacion.Folio,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.CosteoListoAt,
            cotizacion.PrecioFinal,
            cotizacion.PrecioFinal is null ? null : ruta?.MargenAplicado,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            [.. cotizacion.RutasOrdenadas.SelectMany(r => ConEstadoNexo(r, ConexosConPrecio(cotizacion, r), estadosNexo))],
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            ruta is null ? null : ruta.MargenSAPSugerido ?? ruta.MargenPoliticaSugerido,
            ruta?.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            cotizacion.PdfDesactualizado,
            VersionEtag(cotizacion),
            clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion))
        {
            TrmAplicada = cotizacion.TrmAplicada,
            Rutas = [.. cotizacion.RutasOrdenadas.Select(r => RutaCosteo(cotizacion, r, estadosNexo))],
        };
    }

    /// <summary>
    /// <c>RutaCosteo</c> (HU-021.8; rehecha en HU-021.9, #136): la ruta con su margen, su precio, sus conexos, sus camiones y sus
    /// mercancías, como la ve el comercial (nunca costos de Nexo).
    /// </summary>
    public static clsRutaCosteo RutaCosteo(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        var conexos = ConEstadoNexo(ruta, ConexosConPrecio(cotizacion, ruta), estadosNexo);
        var revisar = conexos
            .Where(c => c.EstadoNexo is clsEnvioConexosNexoService.VisibleIncierto or clsEnvioConexosNexoService.VisibleFallidoDefinitivo)
            .Select(c => $"«{c.Nombre ?? c.Id}»")
            .ToList();
        return new clsRutaCosteo(
            ruta.Orden,
            ruta.Id,
            ruta.OrigenNombre,
            ruta.DestinoNombre,
            ruta.EstadoCosteo is { } estadoRuta ? clsCodigoEnum.ACodigo(estadoRuta) : null,
            ruta.PrecioFinalLinea,
            ruta.PrecioFinalLinea is null ? null : ruta.MargenAplicado,
            ruta.MargenSAPSugerido ?? ruta.MargenPoliticaSugerido,
            ruta.OrigenMargen is { } origenRuta ? clsCodigoEnum.ACodigo(origenRuta) : null,
            conexos)
        {
            AvisoConexosNexo = revisar.Count == 0
                ? null
                : $"Revisa en la cotización de Nexo los servicios conexos {string.Join(", ", revisar)}: Cotifly no pudo confirmar que llegaron "
                  + "y no los reenvía solo (Nexo no tiene DELETE). El precio en Cotifly ya los incluye.",
            LineaNegocioId = ruta.LineaNegocioId,
            PesoKg = ruta.PesoKg,
            Camiones = [.. ruta.VehiculosOrdenados.Select(v => new clsCamionCosteo(
                v.TipoVehiculo, v.Carroceria, v.PesoKg, v.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null))],
            Vehiculos = clsCosteoCotizacionService.Camiones(ruta),
            Mercancias = [.. ruta.MercanciasOrdenadas.Select(m => new clsMercanciaCosteo(
                m.Orden, m.IdTipoMercancia, m.NombreMercancia, m.Cantidad, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg))],
        };
    }

    /// <summary>Los conexos de la ruta con el estado de su envío a Nexo (decisión 10), si se conoce; sin estados, tal cual.</summary>
    private static IReadOnlyList<clsServicioConexoPrecio> ConEstadoNexo(
        clsLineaSolicitud ruta, IReadOnlyList<clsServicioConexoPrecio> conexos, IReadOnlyDictionary<(Guid Ruta, string Conexo), string>? estadosNexo) =>
        estadosNexo is null || estadosNexo.Count == 0
            ? conexos
            : [.. conexos.Select(c => c with
            {
                EstadoNexo = estadosNexo.TryGetValue((ruta.Id, c.Id), out var estado) ? estado : clsEnvioConexosNexoService.VisiblePendiente,
            })];

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
