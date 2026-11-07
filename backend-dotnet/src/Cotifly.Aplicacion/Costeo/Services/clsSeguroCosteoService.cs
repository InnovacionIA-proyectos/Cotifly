using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-045.2 (#264, ADR-029): incluir o quitar el seguro de la mercancía de una ruta en el costeo:
/// <c>PUT /web/cotizaciones/{folio}/costeo/seguro</c> (decisión 3 de HU-045: endpoint propio con <c>If-Match</c>, como el margen).
/// </summary>
public interface IClsSeguroCosteoService
{
    /// <summary>
    /// Incluye (<paramref name="incluir"/>) o quita el seguro de UNA ruta de la cotización del comercial (<paramref name="ruta"/>: su
    /// <c>linea_orden</c> o su id; sin ninguno, la única ruta) y recalcula el precio de esa ruta y el total (el seguro se suma sin margen; en
    /// USD, convertido con la TRM fijada). Al incluirlo toma la tasa vigente de <c>TarifasSeguro</c>. Errores: <c>COTIZACION_NO_ENCONTRADA</c>
    /// (404), <c>CONFLICTO_CONCURRENCIA</c> (409), <c>RUTA_REQUERIDA</c> y <c>RUTA_INEXISTENTE</c> (400), <c>SEGURO_SIN_VALOR_MERCANCIA</c>
    /// (400), <c>ESTADO_NO_PERMITE</c> (409, licitación), <c>RUTA_RECHAZADA</c> y <c>PDF_YA_ENVIADO</c> (409, enviada o finalizada, el mismo
    /// del margen). Marcar una ruta que ya lo tiene (o quitarlo de una que no lo tiene) responde igual sin registrar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> CambiarAsync(
        Guid usuarioId, string folio, clsRutaIndicada ruta, bool incluir, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsSeguroCosteoService"/>, con el patrón de <see cref="clsAjusteMargenCosteoService"/>: el
/// <c>If-Match</c> se verifica antes que la ruta; las invariantes son del dominio (<see cref="clsCotizacionSolicitud.CambiarSeguro"/>); el
/// PDF vigente se invalida y queda en el historial (con el motivo <c>servicios_conexos</c>: el CHECK de <c>CotizacionPdfsHistorial</c> no
/// tiene uno propio del seguro); la versión sube y todo se guarda en una transacción con el control de versión. La acción se audita en
/// <c>EventosHerramienta</c> sin montos ni tasa (solo la ruta, si incluye el seguro antes y después, y si invalidó el PDF).
/// </summary>
public sealed class clsSeguroCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsTarifaSeguroRepository tarifasSeguro,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    IClsEnvioConexosNexoService? envioConexos = null,
    ILogger<clsSeguroCosteoService>? logger = null,
    IClsCategoriasConexos? categoriasConexos = null,
    IClsSituacionLineasNexo? situacionesNexo = null) : IClsSeguroCosteoService
{
    private readonly ILogger _logger = logger ?? NullLogger<clsSeguroCosteoService>.Instance;

    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "cambiar_seguro_costeo";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Cambio del seguro de la mercancía después del envío";

    public async Task<clsResultado<clsCosteoCotizacion>> CambiarAsync(
        Guid usuarioId, string folio, clsRutaIndicada ruta, bool incluir, int versionEsperada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        var cronometro = Stopwatch.StartNew();
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var resuelta = cotizacion.ResolverRuta(ruta.LineaOrden, ruta.LineaSolicitudId);
        var rutaCambiada = resuelta.EsExitoso ? resuelta.Valor : null;
        var incluiaAntes = rutaCambiada?.IncluyeSeguro;
        clsResultado<clsCosteoCotizacion> resultado;
        var registrado = false;
        var pdfInvalidado = false;
        if (rutaCambiada is null)
        {
            resultado = TieneVersion(cotizacion, versionEsperada) ?? resuelta.Error;
        }
        else
        {
            (resultado, registrado, pdfInvalidado) = await CambiarAsync(cotizacion, rutaCambiada, usuarioId, incluir, versionEsperada, cancellationToken);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new
            {
                folio = cotizacion.Folio,
                linea_orden = rutaCambiada?.Orden ?? ruta.LineaOrden,
                linea_solicitud_id = rutaCambiada?.Id ?? ruta.LineaSolicitudId,
                incluye_seguro = incluir,
            },
            resultado.EsExitoso
                ? (object)new { incluia_seguro = incluiaAntes, incluye_seguro = rutaCambiada?.IncluyeSeguro, registrado, pdf_invalidado = pdfInvalidado }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private static clsErrorDominio? TieneVersion(clsCotizacionSolicitud cotizacion, int versionEsperada) =>
        cotizacion.TieneVersion(versionEsperada) ? null : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, bool Registrado, bool PdfInvalidado)> CambiarAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, Guid usuarioId, bool incluir, int versionEsperada, CancellationToken cancellationToken)
    {
        if (TieneVersion(cotizacion, versionEsperada) is { } conflicto)
        {
            return (conflicto, false, false);
        }

        if (cotizacion.ValidarCambioSeguro(ruta) is { } invalido)
        {
            return (Detallar(invalido, cotizacion), false, false);
        }

        // D1 de la validación: la ruta ya está como se pide (marcada, aunque sea sin valor de la mercancía, o sin seguro): no se llama al
        // dominio, no se lee la tasa, no se invalida el PDF ni sube la versión.
        if (incluir == ruta.IncluyeSeguro && (incluir || (ruta.TasaSeguroAplicada is null && ruta.CostoSeguro is null)))
        {
            return (await VistaAsync(cotizacion, cancellationToken), false, false);
        }

        // La tasa solo se necesita al incluirlo en una ruta que no lo tiene.
        var tasa = 0m;
        if (incluir)
        {
            if (await tarifasSeguro.ObtenerTasaVigenteAsync(cancellationToken) is not { } vigente)
            {
                return (SinTasaVigente(), false, false);
            }

            tasa = vigente;
        }

        var cambio = cotizacion.CambiarSeguro(ruta.Orden, incluir, tasa, cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt);
        if (cambio.EsFallo)
        {
            return (cambio.Error, false, false);
        }

        if (!cambio.Valor)
        {
            // Idempotente: la ruta ya estaba así; no invalida el PDF ni sube la versión.
            return (await VistaAsync(cotizacion, cancellationToken), false, false);
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, false);
        }

        return (await VistaAsync(cotizacion, CancellationToken.None), true, pdfInvalidado);
    }

    /// <summary>La vista del costeo (como <c>GET .../costeo</c>, con el estado de los conexos en Nexo) y la tasa vigente.</summary>
    private Task<clsCosteoCotizacion> VistaAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken) =>
        clsCosteoCotizacionService.VistaConEstadosAsync(
            cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, _logger, cancellationToken, categoriasConexos, situacionesNexo?.Obtener(cotizacion.Id),
            tarifasSeguro: tarifasSeguro);

    /// <summary><c>PDF_YA_ENVIADO</c> con la llamada de la versión nueva (contrato, como el margen); los demás errores tal cual.</summary>
    private static clsErrorDominio Detallar(clsErrorDominio error, clsCotizacionSolicitud cotizacion) =>
        error.Codigo == clsCodigosError.PdfYaEnviado ? clsReglaPdfCosteo.ConVersionNueva(error, cotizacion.Folio, MotivoVersion) : error;

    /// <summary>
    /// No hay una tasa activa en <c>TarifasSeguro</c> (el script 20 siembra una y la de operación siempre deja una): es un problema de
    /// configuración de la base, no del comercial. 500 <c>ERROR_INTERNO</c> (el detalle solo va al log).
    /// </summary>
    private static clsErrorDominio SinTasaVigente() => new(clsCodigosError.ErrorInterno,
        "No hay una tasa del seguro de la mercancía activa en dbo_cotifly.TarifasSeguro (script 20): no se puede calcular el seguro.");
}
