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
        var payload = Payload(cotizacion, linea?.LineaNegocio, moneda);
        if (clsValidadorCotizacionPdfPayload.Validar(payload) is { } invalido)
        {
            return invalido;
        }

        var traza = new clsTrazabilidadCotizacion(
            cotizacion.Costo, cotizacion.MargenPolitica, cotizacion.MargenAplicado, null, cotizacion.MetodoCalculo, cotizacion.PrecioFinal,
            cotizacion.NitCliente, null, moneda, cotizacion.TrmAplicada, cotizacion.CosteoId);
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

        clsPdfGenerado pdf;
        try
        {
            pdf = generador.Generar(documento.Valor);
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
