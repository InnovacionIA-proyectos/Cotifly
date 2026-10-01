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
/// (<c>COTIZACION_NO_ENCONTRADA</c> → 404).
/// </summary>
public interface IClsCosteoCotizacionService
{
    Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marca como incluidos exactamente los servicios <paramref name="incluidos"/>. <c>COSTEO_NO_LISTO</c> (409) sin precio;
    /// <c>PDF_YA_GENERADO</c> (409) con PDF; <c>SOLICITUD_COSTEO_INVALIDA</c> (400) con un id que no es del costeo.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, CancellationToken cancellationToken = default);

    /// <summary>
    /// PDF de la cotización con el flete a <c>precioFinal</c> y solo los servicios conexos incluidos, cada uno con su precio.
    /// <c>COSTEO_NO_LISTO</c> y <c>PDF_YA_GENERADO</c> (409), y los errores del módulo PDF.
    /// </summary>
    Task<clsResultado<clsSalidaPdf>> GenerarPdfAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);
}

/// <summary><c>CosteoCotizacion</c>: estado del costeo y precios (sin costos de Nexo).</summary>
public sealed record clsCosteoCotizacion(
    string Folio,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    DateTimeOffset? CosteoListoAt,
    decimal? PrecioFinal,
    decimal? MargenAplicado,
    string Moneda,
    IReadOnlyList<clsServicioConexoPrecio> ServiciosConexos,
    string? PdfUrl);

/// <summary>Servicio conexo tal como lo ve el comercial: precio con margen y si va en el PDF.</summary>
public sealed record clsServicioConexoPrecio(string Id, string? Nombre, string Categoria, decimal Cantidad, decimal Precio, bool Incluido);

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

    public async Task<clsResultado<clsCosteoCotizacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        return cotizacion is null ? NoEncontrada() : Vista(cotizacion);
    }

    public async Task<clsResultado<clsCosteoCotizacion>> SeleccionarServiciosConexosAsync(
        Guid usuarioId, string folio, IReadOnlyCollection<string> incluidos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incluidos);
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        if (Bloqueo(cotizacion) is { } error)
        {
            return error;
        }

        var servicios = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos);
        var desconocidos = incluidos.Where(id => servicios.All(s => s.Id != id)).ToList();
        if (desconocidos.Count > 0)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"Estos servicios conexos no son del costeo de la cotización {cotizacion.Folio}: {string.Join(", ", desconocidos)}.");
        }

        var seleccion = incluidos.ToHashSet(StringComparer.Ordinal);
        cotizacion.SeleccionarServiciosConexos(clsServiciosConexosJson.Escribir([.. servicios.Select(s => s with { Incluido = seleccion.Contains(s.Id) })]));
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return Vista(cotizacion);
    }

    public async Task<clsResultado<clsSalidaPdf>> GenerarPdfAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var resultado = await GenerarAsync(cotizacion, usuarioId, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaPdf,
            new { folio = cotizacion.Folio },
            resultado.EsExitoso ? (object)resultado.Valor : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<clsResultado<clsSalidaPdf>> GenerarAsync(clsCotizacion cotizacion, Guid usuarioId, CancellationToken cancellationToken)
    {
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

        // HU-011.5 (ADR-017 §6): una cotización de varias líneas (licitación) sale con el layout de pliego.
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var esPliego = lineas.Count > 1;
        var payload = esPliego ? PayloadPliego(cotizacion, lineas, linea?.LineaNegocio, moneda) : Payload(cotizacion, linea?.LineaNegocio, moneda);
        if (clsValidadorCotizacionPdfPayload.Validar(payload) is { } invalido)
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

        if (esPliego && CoherenciaPliego(cotizacion, lineas) is { } pliegoIncoherente)
        {
            return pliegoIncoherente;
        }

        var firma = new clsFirmaDocumento(usuario.NombreCompleto ?? usuario.NombreUsuario ?? string.Empty, null, usuario.Email, clsTextoSeguro.LimpiarLinea(usuario.Telefono));
        var documento = clsConstructorDocumentoCotizacion.Construir(payload, cotizacion.Folio, firma, calendario.Hoy, moneda, cotizacion.TrmAplicada);
        if (documento.EsFallo)
        {
            return documento.Error;
        }

        var aRenderizar = esPliego
            ? documento.Valor with { Variante = VarianteDocumento.Pliego, Pliego = ResumenPliego(cotizacion, lineas, moneda) }
            : documento.Valor;

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
        var ruta = await almacen.GuardarNuevoAsync(nombre, pdf.Contenido, cancellationToken);
        if (ruta is null)
        {
            return new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"Ya existe el PDF {nombre} en el almacén: genera una versión nueva.");
        }

        cotizacion.AsignarPdf(ruta);
        try
        {
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }
        catch
        {
            await almacen.EliminarAsync(nombre, CancellationToken.None);
            throw;
        }

        return new clsSalidaPdf(cotizacion.Id, cotizacion.Folio, clsUrlPdf.Construir(opcionesPdf.Value.BaseUrl, nombre), RegistradaEnBd: true);
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
            $"TOTAL DEL PLIEGO ({filas.Count} RUTAS)",
            clsFormatoMoneda.Formatear(cotizacion.PrecioFinal ?? 0m, moneda),
            "Tarifas por viaje de cada ruta; el total es la suma de un viaje por ruta y es solo de referencia.");
    }

    /// <summary>Cada línea del pliego tiene precio y el total de la cotización es la suma de esos precios (nada se inventa en el PDF).</summary>
    private static clsErrorDominio? CoherenciaPliego(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        if (lineas.FirstOrDefault(l => l.PrecioFinal is null) is { } sinPrecio)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La línea {sinPrecio.Orden} del pliego {cotizacion.Folio} todavía no tiene precio.");
        }

        var suma = lineas.Sum(l => l.PrecioFinal!.Value);
        return suma == cotizacion.PrecioFinal
            ? null
            : new clsErrorDominio(clsCodigosError.PdfPayloadInvalido,
                $"El total del pliego ({cotizacion.PrecioFinal}) no es la suma de sus líneas ({suma}): espera la siguiente consulta del costeo.");
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

    private static clsErrorDominio? Bloqueo(clsCotizacion cotizacion)
    {
        if (cotizacion.PrecioFinal is null || cotizacion.EstadoCosteo is not { } estado || !clsDerivacionEstadoCosteo.TieneCosteo(estado))
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado.");
        }

        return cotizacion.PdfPath is null
            ? null
            : new clsErrorDominio(clsCodigosError.PdfYaGenerado, $"La cotización {cotizacion.Folio} ya tiene PDF: genera una versión nueva para cambiarla.");
    }

    private clsCosteoCotizacion Vista(clsCotizacion cotizacion) => new(
        cotizacion.Folio,
        cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
        cotizacion.MotivoEstadoCosteo,
        cotizacion.CosteoListoAt,
        cotizacion.PrecioFinal,
        cotizacion.PrecioFinal is null ? null : cotizacion.MargenAplicado,
        cotizacion.Moneda ?? clsCotizacion.MonedaPesos,
        [.. clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).Select(s => new clsServicioConexoPrecio(s.Id, s.Nombre, s.Categoria, s.Cantidad, s.Precio, s.Incluido))],
        clsUrlPdf.DesdeReferencia(opcionesPdf.Value.BaseUrl, cotizacion.PdfPath));

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
