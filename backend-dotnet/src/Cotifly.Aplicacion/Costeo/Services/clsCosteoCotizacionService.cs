using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Pdf.Validators;
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
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, byte[] versionEsperada, CancellationToken cancellationToken = default);

    /// <summary>
    /// PDF de la cotización con el flete a <c>precioFinal</c> y solo los servicios conexos incluidos, cada uno con su precio.
    /// <c>COSTEO_NO_LISTO</c>, <c>PDF_YA_GENERADO</c> (PDF vigente), <c>PDF_YA_ENVIADO</c> y <c>CONFLICTO_CONCURRENCIA</c> (409, solo si
    /// viene <paramref name="versionEsperada"/>), y los errores del módulo PDF. Con el PDF desactualizado, el anterior se archiva y se
    /// genera uno nuevo en el mismo folio.
    /// </summary>
    Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, byte[]? versionEsperada = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>CosteoCotizacion</c>: estado del costeo y precios (sin costos de Nexo), el margen sugerido y su origen (HU-013.2), si el PDF
/// quedó desactualizado (ADR-018 §11) y la versión de la fila (<c>row_version</c>, también en el <c>ETag</c>).
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
    string? RowVersion) : IClsConVersionFila;

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
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, byte[] versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incluidos);
        ArgumentNullException.ThrowIfNull(versionEsperada);
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

        var servicios = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos);
        var desconocidos = incluidos.Where(id => servicios.All(s => s.Id != id)).ToList();
        if (desconocidos.Count > 0)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"Estos servicios conexos no son del costeo de la cotización {cotizacion.Folio}: {string.Join(", ", desconocidos)}.");
        }

        var seleccion = incluidos.ToHashSet(StringComparer.Ordinal);
        var json = clsServiciosConexosJson.Escribir([.. servicios.Select(s => s with { Incluido = seleccion.Contains(s.Id) })]);
        if (json == cotizacion.ServiciosConexos)
        {
            // La misma selección no cambia nada (ni invalida el PDF): idempotente.
            return Vista(cotizacion, opcionesPdf.Value.BaseUrl);
        }

        cotizacion.SeleccionarServiciosConexos(json);
        clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? Vista(cotizacion, opcionesPdf.Value.BaseUrl)
            : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
    }

    public async Task<clsResultado<clsPdfCosteoGenerado>> GenerarPdfAsync(
        Guid usuarioId, string folio, byte[]? versionEsperada = null, CancellationToken cancellationToken = default)
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
        clsCotizacion cotizacion, Guid usuarioId, byte[]? versionEsperada, CancellationToken cancellationToken)
    {
        if (versionEsperada is not null && !cotizacion.TieneVersion(versionEsperada))
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

        var linea = cotizacion.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, cancellationToken) : null;
        var moneda = cotizacion.Moneda ?? clsCotizacion.MonedaPesos;

        // HU-011.5 (ADR-017 §6): una licitación (aunque tenga una sola línea: sus columnas de línea en Cotizaciones son nulas) o una
        // cotización de varias líneas sale con el layout de pliego.
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var esPliego = EsPliego(cotizacion, lineas);
        if (esPliego && ValidarPliego(cotizacion, lineas, moneda) is { } pliegoNoListo)
        {
            return pliegoNoListo;
        }

        var payload = esPliego ? PayloadPliego(cotizacion, lineas, linea?.LineaNegocio, moneda) : Payload(cotizacion, linea?.LineaNegocio, moneda);
        var invalido = esPliego
            ? clsValidadorCotizacionPdfPayload.Validar(payload, MaximoLineasPliego, MaximoLineasPliego)
            : clsValidadorCotizacionPdfPayload.Validar(payload);
        if (invalido is not null)
        {
            return invalido;
        }

        // En el pliego cada fila tiene su propio precio: la coherencia se verifica línea por línea (CoherenciaPliego) y aquí solo la moneda.
        var traza = new clsTrazabilidadCotizacion(
            esPliego ? null : cotizacion.Costo, cotizacion.MargenPolitica, cotizacion.MargenAplicado, null, cotizacion.MetodoCalculo,
            esPliego ? null : cotizacion.PrecioFinal, cotizacion.NitCliente, null, moneda, cotizacion.TrmAplicada, cotizacion.CosteoId);
        if (clsGenerarPdfService.ValidarCoherencia(payload, traza, cotizacion.MetodoCalculo ?? "margen", moneda) is { } incoherente)
        {
            return incoherente;
        }

        var firma = new clsFirmaDocumento(usuario.NombreCompleto ?? usuario.NombreUsuario ?? string.Empty, null, usuario.Email, clsTextoSeguro.LimpiarLinea(usuario.Telefono));
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, firma, calendario.Hoy, moneda, cotizacion.TrmAplicada);
        if (documento.EsFallo)
        {
            return documento.Error;
        }

        var aRenderizar = esPliego ? DocumentoPliego(documento.Valor, cotizacion, lineas, moneda) : documento.Valor;

        clsPdfGenerado pdf;
        try
        {
            pdf = generador.Generar(aRenderizar);
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
        bool guardado;
        try
        {
            guardado = versionEsperada is null
                ? await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None) >= 0
                : await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None);
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
            VersionBase64(cotizacion));
    }

    /// <summary>
    /// Archiva el PDF invalidado que ocupa <paramref name="nombre"/>: lo copia como <c>{folio}-hN.pdf</c>, actualiza su referencia en el
    /// historial (lo guarda de inmediato, antes del PDF nuevo) y libera el nombre. Si el archivo ya no está, no hay nada que archivar.
    /// </summary>
    private async Task ArchivarPdfAnteriorAsync(clsCotizacion cotizacion, string nombre, CancellationToken cancellationToken)
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

    /// <summary><c>Cotizaciones.rowVersion</c> en base64 (el valor del <c>ETag</c> y de <c>row_version</c>), o <c>null</c>.</summary>
    public static string? VersionBase64(clsCotizacion cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return cotizacion.RowVersion is { Length: > 0 } version ? Convert.ToBase64String(version) : null;
    }

    /// <summary>
    /// Payload del PDF armado desde la cotización (nada del LLM ni del cliente): una fila con el origen, el destino, el vehículo y el
    /// precio final; los servicios conexos incluidos, cada uno con su precio (el PDF solo lista esos).
    /// </summary>
    public static clsCotizacionPdfPayload Payload(clsCotizacion cotizacion, string? lineaNegocio, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var vehiculo = string.Join(" - ", new[]
        {
            cotizacion.TipoVehiculo, cotizacion.Carroceria,
            cotizacion.PesoKg is { } peso ? $"{peso.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG" : null,
        }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var incluidos = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Where(s => s.Incluido).ToList();
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio,
            MetodoCalculo = cotizacion.MetodoCalculo,
            Tarifa = new clsTarifaPayload
            {
                Titulo = lineaNegocio is null ? "COTIZACIÓN DE TRANSPORTE" : $"COTIZACIÓN DE TRANSPORTE {lineaNegocio.ToUpperInvariant()}",
                Origen = cotizacion.Origen,
                Filas =
                [
                    new clsFilaTarifaPayload
                    {
                        Destino = cotizacion.Destino,
                        VehiculoPeso = vehiculo.Length == 0 ? null : vehiculo,
                        Tarifa = cotizacion.PrecioFinal,
                        Moneda = moneda,
                    },
                ],
            },
            ServiciosConexos = incluidos.Count == 0
                ? null
                : [.. incluidos.Select(s => new clsServicioConexoPayload { Descripcion = s.Nombre ?? s.Categoria, Precio = s.Precio, Moneda = moneda })],
        };
    }

    /// <summary>
    /// Payload del PDF de un pliego (HU-011.5): una tabla por origen (en el orden del pliego) con una fila por destino, su vehículo y
    /// peso y el precio final de la línea; solo los servicios conexos incluidos, con su precio (mismo criterio de "solo lo realmente
    /// incluido" de HU-003.5). Nada del LLM ni del cliente.
    /// </summary>
    public static clsCotizacionPdfPayload PayloadPliego(
        clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string? lineaNegocio, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        var titulo = lineaNegocio is null ? "PLIEGO DE TRANSPORTE" : $"PLIEGO DE TRANSPORTE {lineaNegocio.ToUpperInvariant()}";
        var incluidos = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Where(s => s.Incluido).ToList();
        var secciones = lineas.OrderBy(l => l.Orden)
            .GroupBy(l => l.Origen, StringComparer.OrdinalIgnoreCase)
            .Select(g => new clsTarifaPayload
            {
                Titulo = titulo,
                Origen = g.First().Origen,
                Filas = [.. g.Select(l => new clsFilaTarifaPayload
                {
                    Destino = $"{l.Orden}. {l.Destino}",
                    VehiculoPeso = VehiculoPeso(l.TipoVehiculo, l.Carroceria, l.PesoKg),
                    Tarifa = l.PrecioFinal,
                    Moneda = moneda,
                })],
            })
            .ToList();
        return new clsCotizacionPdfPayload
        {
            Cliente = cotizacion.ClienteNombre,
            LineaNegocio = lineaNegocio,
            MetodoCalculo = cotizacion.MetodoCalculo,
            Asunto = cotizacion.Referencia is null
                ? $"presentación oferta de servicios de Transporte para su licitación ({lineas.Count} rutas)"
                : $"presentación oferta de servicios de Transporte para la licitación {cotizacion.Referencia} ({lineas.Count} rutas)",
            Secciones = secciones,
            ServiciosConexos = incluidos.Count == 0
                ? null
                : [.. incluidos.Select(s => new clsServicioConexoPayload { Descripcion = s.Nombre ?? s.Categoria, Precio = s.Precio, Moneda = moneda })],
        };
    }

    /// <summary>Resumen del pliego: una fila por línea, en orden, y el total (la suma de los precios de las líneas).</summary>
    public static clsResumenPliegoDocumento ResumenPliego(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        var filas = lineas.OrderBy(l => l.Orden).Select(l => new clsFilaPliegoDocumento(
            l.Orden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            clsTextoSeguro.LimpiarLinea($"{l.Origen} → {l.Destino}") ?? string.Empty,
            VehiculoPeso(l.TipoVehiculo, l.Carroceria, l.PesoKg) ?? "—",
            l.PrecioFinal is { } precio ? clsFormatoMoneda.Formatear(precio, moneda) : "—")).ToList();
        return new clsResumenPliegoDocumento(
            cotizacion.Referencia is null ? "RESUMEN DEL PLIEGO" : $"RESUMEN DEL PLIEGO {cotizacion.Referencia.ToUpperInvariant()}",
            filas,
            $"TOTAL DEL PLIEGO ({filas.Count} RUTAS, SIN SERVICIOS CONEXOS)",
            clsFormatoMoneda.Formatear(cotizacion.PrecioFinal ?? 0m, moneda),
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

    /// <summary>¿El PDF sale con el layout de pliego? Toda licitación con líneas, y cualquier cotización de dos o más líneas.</summary>
    public static bool EsPliego(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        return lineas.Count > 1 || (lineas.Count == 1 && cotizacion.TipoSolicitud == TipoSolicitud.Licitacion);
    }

    /// <summary>¿Las rutas u orígenes del pliego pasan del umbral del layout completo?</summary>
    public static bool EsPliegoCompacto(IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        return lineas.Count > UmbralLineasCompacto
               || lineas.Select(l => l.Origen).Distinct(StringComparer.OrdinalIgnoreCase).Count() > UmbralOrigenesCompacto;
    }

    /// <summary>
    /// Condiciones del PDF de pliego (409 <c>COSTEO_NO_LISTO</c> si no se cumplen): cada línea tiene precio y el total de la cotización
    /// es la suma de esos precios (nada se inventa en el PDF). En USD se rechaza (<c>PDF_PAYLOAD_INVALIDO</c>) hasta que el costeo
    /// integre la TRM (HU-012): hoy los precios de las líneas están en pesos.
    /// </summary>
    public static clsErrorDominio? ValidarPliego(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string moneda)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        if (!string.Equals(moneda, clsCotizacion.MonedaPesos, StringComparison.OrdinalIgnoreCase))
        {
            return new clsErrorDominio(clsCodigosError.PdfPayloadInvalido,
                $"El PDF de pliego en {moneda} no está disponible: el costeo de las licitaciones es solo en COP hasta integrar la TRM (HU-012).");
        }

        if (lineas.FirstOrDefault(l => l.PrecioFinal is null) is { } sinPrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La línea {sinPrecio.Orden} del pliego {cotizacion.Folio} todavía no tiene precio.");
        }

        var suma = lineas.Sum(l => l.PrecioFinal!.Value);
        return suma == cotizacion.PrecioFinal
            ? null
            : new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"El total del pliego ({cotizacion.PrecioFinal}) todavía no es la suma de sus líneas ({suma}): espera la siguiente consulta del costeo.");
    }

    /// <summary>
    /// Documento del pliego: la variante <see cref="VarianteDocumento.Pliego"/> con el resumen y, en modo compacto
    /// (<see cref="EsPliegoCompacto"/>), sin las tablas por origen.
    /// </summary>
    public static clsDocumentoCotizacion DocumentoPliego(
        clsDocumentoCotizacion documento, clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, string moneda)
    {
        ArgumentNullException.ThrowIfNull(documento);
        var compacto = EsPliegoCompacto(lineas);
        var resumen = ResumenPliego(cotizacion, lineas, moneda);
        return documento with
        {
            Variante = VarianteDocumento.Pliego,
            Pliego = compacto
                ? resumen with { Nota = resumen.Nota + $" Con más de {UmbralLineasCompacto} rutas o {UmbralOrigenesCompacto} orígenes el PDF solo trae este resumen." }
                : resumen,
            Secciones = compacto ? [] : documento.Secciones,
        };
    }

    private static string? VehiculoPeso(string? tipoVehiculo, string? carroceria, decimal? pesoKg)
    {
        var texto = string.Join(" - ", new[]
        {
            tipoVehiculo, carroceria,
            pesoKg is { } peso ? $"{peso.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} KG" : null,
        }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return texto.Length == 0 ? null : texto;
    }

    private async Task<clsCotizacion?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    /// <summary><c>COSTEO_NO_LISTO</c> si la cotización todavía no tiene el costeo de Nexo con precio.</summary>
    private static clsErrorDominio? SinCosteo(clsCotizacion cotizacion) => cotizacion.TieneCostoDisponible
        ? null
        : new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado.");

    /// <summary>Condiciones para generar el PDF: costeo con precio, sin PDF vigente (<c>PDF_YA_GENERADO</c>) y sin envío (<c>PDF_YA_ENVIADO</c>).</summary>
    private static clsErrorDominio? Bloqueo(clsCotizacion cotizacion)
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
    public static clsCosteoCotizacion Vista(clsCotizacion cotizacion, string? baseUrlPdf)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return new clsCosteoCotizacion(
            cotizacion.Folio,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.CosteoListoAt,
            cotizacion.PrecioFinal,
            cotizacion.PrecioFinal is null ? null : cotizacion.MargenAplicado,
            cotizacion.Moneda ?? clsCotizacion.MonedaPesos,
            [.. clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Select(s => new clsServicioConexoPrecio(s.Id, s.Nombre, s.Categoria, s.Cantidad, s.Precio, s.Incluido))],
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            cotizacion.MargenSugerido,
            cotizacion.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            cotizacion.PdfDesactualizado,
            VersionBase64(cotizacion));
    }

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
