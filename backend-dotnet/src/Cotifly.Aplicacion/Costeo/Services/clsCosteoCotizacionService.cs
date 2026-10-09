using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
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
    /// Marca como incluidos exactamente los servicios <paramref name="incluidos"/>. <c>CONFLICTO_CONCURRENCIA</c> (409) si
    /// <paramref name="versionEsperada"/> no es la de la fila; <c>COSTEO_NO_LISTO</c> (409) sin precio; <c>PDF_YA_ENVIADO</c> (409) ya
    /// enviada o con resultado; <c>SOLICITUD_COSTEO_INVALIDA</c> (400) con un id que no es del costeo. Si la selección cambia, el PDF
    /// vigente queda invalidado (ADR-018 §11).
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, int versionEsperada, CancellationToken cancellationToken = default);

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
}

/// <summary>Servicio conexo tal como lo ve el comercial: precio con margen y si va en el PDF.</summary>
public sealed record clsServicioConexoPrecio(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal Precio, bool Incluido);

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
    ILogger<clsCosteoCotizacionService> logger) : IClsCosteoCotizacionService
{
    public const string HerramientaPdf = "generar_pdf_costeo";

    /// <summary>Nombres <c>{folio}-hN.pdf</c> que se prueban al archivar un PDF invalidado.</summary>
    private const int MaximoIntentosArchivo = 20;

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> al elegir conexos de una cotización ya entregada.</summary>
    public const string MotivoVersionConexos = "Cambio de servicios conexos después del envío";

    public async Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        return cotizacion is null ? NoEncontrada() : Vista(cotizacion, opcionesPdf.Value.BaseUrl);
    }

    public async Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, int versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incluidos);
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        if (SinCosteo(cotizacion) is { } sinCosteo)
        {
            return sinCosteo;
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersionConexos) is { } entregada)
        {
            return entregada;
        }

        // HU-021 (ADR-019): los conexos de la solicitud son filas de CotizacionServiciosConexos en sus rutas; un conexo excluido se QUITA de
        // la ruta (la tabla no tiene "incluido": la fila es lo que va en el precio y en el PDF). Excluido no se puede volver a incluir por
        // aquí: se agrega de nuevo con "Agregar conexo" (adaptación hasta los conexos en la captura, #134).
        var conexos = cotizacion.Rutas.SelectMany(r => r.Conexos).ToList();
        var desconocidos = incluidos.Where(id => conexos.All(s => s.IdServicioConexo != id)).ToList();
        if (desconocidos.Count > 0)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"Estos servicios conexos no son del costeo de la cotización {cotizacion.Folio}: {string.Join(", ", desconocidos)}.");
        }

        var seleccion = incluidos.ToHashSet(StringComparer.Ordinal);
        var quitados = cotizacion.ConservarConexos(seleccion, cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt);
        if (quitados.EsFallo)
        {
            return quitados.Error;
        }

        if (quitados.Valor == 0)
        {
            // La misma selección no cambia nada (ni invalida el PDF): idempotente.
            return Vista(cotizacion, opcionesPdf.Value.BaseUrl);
        }

        clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? Vista(cotizacion, opcionesPdf.Value.BaseUrl)
            : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
    }

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

        var linea = cotizacion.PrimeraRuta?.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, cancellationToken) : null;
        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;

        // HU-011.5 (ADR-017 §6): una licitación (aunque tenga una sola ruta) o una solicitud de varias rutas sale con el layout de pliego.
        var esPliego = EsPliego(cotizacion);
        if (esPliego && ValidarPliego(cotizacion, moneda) is { } pliegoNoListo)
        {
            return pliegoNoListo;
        }

        var payload = esPliego ? PayloadPliego(cotizacion, linea?.LineaNegocio, moneda) : Payload(cotizacion, linea?.LineaNegocio, moneda);
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
    /// Payload del PDF de un pliego (HU-011.5): una tabla por origen (en el orden del pliego) con una fila por ruta, sus camiones y la
    /// tarifa del transporte de la ruta (sin conexos: van aparte, con su precio, para no cobrarlos dos veces; HU-021 el precio de la ruta
    /// los incluye). Nada del LLM ni del cliente.
    /// </summary>
    public static clsCotizacionPdfPayload PayloadPliego(clsCotizacionSolicitud cotizacion, string? lineaNegocio, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        var titulo = lineaNegocio is null ? "PLIEGO DE TRANSPORTE" : $"PLIEGO DE TRANSPORTE {lineaNegocio.ToUpperInvariant()}";
        var conexos = rutas.SelectMany(r => ConexosConPrecio(cotizacion, r)).ToList();
        var secciones = rutas
            .GroupBy(r => r.OrigenNombre ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => new clsTarifaPayload
            {
                Titulo = titulo,
                Origen = g.First().OrigenNombre,
                Filas = [.. g.Select(r => new clsFilaTarifaPayload
                {
                    Destino = $"{r.Orden}. {r.DestinoNombre}",
                    VehiculoPeso = Camiones(r),
                    Tarifa = PrecioTransporte(cotizacion, r),
                    Moneda = moneda,
                })],
            })
            .ToList();
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio,
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
    /// Por encima de estas rutas u orígenes el pliego sale COMPACTO: solo el resumen (que ya trae todas las rutas con su tarifa), sin
    /// las tablas por origen, que repetirían las mismas filas en decenas de páginas.
    /// </summary>
    public const int UmbralLineasCompacto = clsValidadorCotizacionPdfPayload.MaxFilas;

    public const int UmbralOrigenesCompacto = clsValidadorCotizacionPdfPayload.MaxSecciones;

    /// <summary>¿El PDF sale con el layout de pliego? Toda licitación con rutas, y cualquier solicitud de dos o más rutas.</summary>
    public static bool EsPliego(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > 1 || (cotizacion.Rutas.Count == 1 && cotizacion.TipoSolicitud == TipoSolicitud.Licitacion);
    }

    /// <summary>¿Las rutas u orígenes del pliego pasan del umbral del layout completo?</summary>
    public static bool EsPliegoCompacto(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.Rutas.Count > UmbralLineasCompacto
               || cotizacion.Rutas.Select(r => r.OrigenNombre ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count() > UmbralOrigenesCompacto;
    }

    /// <summary>
    /// Condiciones del PDF de pliego (409 <c>COSTEO_NO_LISTO</c> si no se cumplen): cada ruta tiene precio y el total de la solicitud es la
    /// suma de esos precios (nada se inventa en el PDF). En USD (ADR-014 §5) los precios ya vienen convertidos con la <c>trmAplicada</c>.
    /// </summary>
    public static clsErrorDominio? ValidarPliego(clsCotizacionSolicitud cotizacion, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (string.IsNullOrWhiteSpace(moneda))
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, $"El pliego {cotizacion.Folio} no trae la moneda de la cotización.");
        }

        if (cotizacion.RutasOrdenadas.FirstOrDefault(r => r.PrecioFinalLinea is null) is { } sinPrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La línea {sinPrecio.Orden} del pliego {cotizacion.Folio} todavía no tiene precio.");
        }

        var suma = cotizacion.Rutas.Sum(r => r.PrecioFinalLinea!.Value);
        return suma == cotizacion.PrecioFinal
            ? null
            : new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"El total del pliego ({cotizacion.PrecioFinal}) todavía no es la suma de sus líneas ({suma}): espera la siguiente consulta del costeo.");
    }

    /// <summary>
    /// Documento del pliego: la variante <see cref="VarianteDocumento.Pliego"/> con el resumen y, en modo compacto
    /// (<see cref="EsPliegoCompacto"/>), sin las tablas por origen.
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
                ? resumen with { Nota = resumen.Nota + $" Con más de {UmbralLineasCompacto} rutas o {UmbralOrigenesCompacto} orígenes el PDF solo trae este resumen." }
                : resumen,
            Secciones = compacto ? [] : documento.Secciones,
        };
    }

    /// <summary>
    /// Camiones de la ruta para el PDF: con uno, "vehículo - carrocería - peso KG" (como antes de HU-021); con varios, cada vehículo con
    /// cuántos van ("2 × 3S3 - FURGON"). <c>null</c> si no hay nada que mostrar.
    /// </summary>
    public static string? Camiones(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var camiones = ruta.VehiculosOrdenados;
        if (camiones.Count <= 1)
        {
            var unico = camiones.Count == 1 ? camiones[0] : null;
            return VehiculoPeso(unico?.TipoVehiculo, unico?.Carroceria, unico?.PesoKg ?? ruta.PesoKg);
        }

        var grupos = camiones
            .GroupBy(c => VehiculoPeso(c.TipoVehiculo, c.Carroceria, null) ?? "Vehículo por definir", StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} × {g.Key}");
        var peso = ruta.PesoKg is { } total ? $" ({total.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG)" : string.Empty;
        return string.Join(", ", grupos) + peso;
    }

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
            var ajustado = total - conexos.Sum(c => c.Precio);
            if (Math.Abs(ajustado - transporte) <= conexos.Count)
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
            .Select(x => new clsServicioConexoPrecio(x.Conexo.IdServicioConexo, x.Conexo.Nombre, CategoriaConexo, x.Conexo.Cantidad, x.Precio!.Value, true))];
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
    public static clsCosteoCotizacion Vista(clsCotizacionSolicitud cotizacion, string? baseUrlPdf)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);

        // HU-021: el margen y su sugerido son de la ruta 1 (el contrato por ruta llega con #138); el precio es la suma de las rutas.
        var ruta = cotizacion.PrimeraRuta;
        return new clsCosteoCotizacion(
            cotizacion.Folio,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.CosteoListoAt,
            cotizacion.PrecioFinal,
            cotizacion.PrecioFinal is null ? null : ruta?.MargenAplicado,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            [.. cotizacion.RutasOrdenadas.SelectMany(r => ConexosConPrecio(cotizacion, r))],
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            ruta is null ? null : ruta.MargenSAPSugerido ?? ruta.MargenPoliticaSugerido,
            ruta?.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            cotizacion.PdfDesactualizado,
            VersionEtag(cotizacion),
            clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion))
        {
            TrmAplicada = cotizacion.TrmAplicada,
        };
    }

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
