using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Ajuste de margen por ruta sobre el costeo de Nexo (ADR-018 §3):
/// <c>PUT /web/cotizaciones/{folio}/costeo/margen</c>. Es el botón de la pantalla y el Confirmar de la acción propuesta
/// <c>ajustar_margen</c> del asistente.
/// </summary>
public interface IClsAjusteMargenCosteoService
{
    /// <summary>
    /// Ajusta el margen de UNA ruta de la cotización del comercial (<paramref name="ruta"/>: su <c>linea_orden</c> o su id; sin ninguno,
    /// la única ruta) y recalcula el precio de esa ruta y el total con <see cref="IClsPrecioService"/> (misma moneda y TRM fijada).
    /// Errores: <c>COTIZACION_NO_ENCONTRADA</c> (404, también si no es suya), <c>CONFLICTO_CONCURRENCIA</c> (409,
    /// <paramref name="versionEsperada"/> no es la de la fila), <c>RUTA_REQUERIDA</c> (400, varias rutas y no dice cuál, con
    /// <c>detalle.rutas</c>), <c>RUTA_INEXISTENTE</c>, <c>AJUSTE_MARGEN_INVALIDO</c> y <c>JUSTIFICACION_REQUERIDA</c> (400, contra el
    /// sugerido de ESA ruta), <c>COSTEO_NO_LISTO</c> y <c>PDF_YA_ENVIADO</c> (409). El mismo margen vigente de la ruta responde igual sin
    /// registrar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AjustarAsync(
        Guid usuarioId, string folio, clsRutaIndicada ruta, decimal margen, string? justificacion, int versionEsperada,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsAjusteMargenCosteoService"/>:
/// <list type="bullet">
/// <item>La ruta la resuelve el dominio (<see cref="clsCotizacionSolicitud.ResolverRuta"/>); las invariantes (rango, costo disponible,
/// regla del PDF y justificación contra el sugerido de la ruta: <c>margenSAPSugerido</c> o <c>margenPoliticaSugerido</c>) también
/// (<see cref="clsCotizacionSolicitud.ValidarAjusteMargen"/>); <c>PDF_YA_ENVIADO</c> sale con la llamada para crear la versión nueva.</item>
/// <item>Solo se recalculan la ruta ajustada (su transporte con el margen nuevo y su método + la venta de sus conexos) y
/// la cabecera (la suma de las rutas). En otra moneda se convierte con la <c>trm_aplicada</c> de la cotización, nunca con una TRM nueva.</item>
/// <item>El ajuste queda en <c>MargenAjustes</c> con la ruta REAL (<c>lineaSolicitudId</c>), el sugerido de esa ruta
/// (<c>margenPolitica</c>), la justificación (<c>motivo</c>) y el comercial; el PDF vigente se invalida y queda en el historial
/// (<see cref="clsReglaPdfCosteo"/>); todo se guarda en una transacción con el control de versión. La acción se audita en
/// <c>EventosHerramienta</c> (sin montos, con la ruta).</item>
/// </list>
/// No reutiliza <c>IClsAjusteMargenService</c> (chat): ese caso de uso guarda por su cuenta y registra un motivo por
/// defecto; aquí el ajuste, el precio y el PDF se confirman juntos y la justificación es opcional por encima del sugerido.
/// </summary>
public sealed class clsAjusteMargenCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenAjusteRepository ajustes,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    IClsEnvioConexosNexoService? envioConexos = null,
    ILogger<clsAjusteMargenCosteoService>? logger = null,
    IClsCategoriasConexos? categoriasConexos = null,
    IClsSituacionLineasNexo? situacionesNexo = null,
    Margen.Services.IClsAvisoSincronizacionMargenSap? avisoSincronizacion = null,
    IClsTarifaSeguroRepository? tarifasSeguro = null,
    IClsPoliticaSegurosRepository? politicaSeguros = null) : IClsAjusteMargenCosteoService
{
    private readonly ILogger _logger = logger ?? NullLogger<clsAjusteMargenCosteoService>.Instance;

    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "ajustar_margen_costeo";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Ajuste de margen después del envío";

    public async Task<clsResultado<clsCosteoCotizacion>> AjustarAsync(
        Guid usuarioId, string folio, clsRutaIndicada ruta, decimal margen, string? justificacion, int versionEsperada,
        CancellationToken cancellationToken = default)
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
        var rutaAjustada = resuelta.EsExitoso ? resuelta.Valor : null;
        var margenAnterior = rutaAjustada?.MargenAplicado;
        var sugerido = rutaAjustada?.SugeridoVigente;
        clsResultado<clsCosteoCotizacion> resultado;
        var registrado = false;
        var pdfInvalidado = false;
        if (rutaAjustada is null)
        {
            resultado = TieneVersion(cotizacion, versionEsperada) ?? resuelta.Error;
        }
        else
        {
            (resultado, registrado, pdfInvalidado) = await AjustarAsync(cotizacion, rutaAjustada, usuarioId, margen, justificacion, versionEsperada, cancellationToken);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new
            {
                folio = cotizacion.Folio,
                linea_orden = rutaAjustada?.Orden ?? ruta.LineaOrden,
                linea_solicitud_id = rutaAjustada?.Id ?? ruta.LineaSolicitudId,
                margen,
                justificacion = clsMargenAjuste.LimpiarJustificacion(justificacion),
            },
            resultado.EsExitoso
                ? (object)new { margen_anterior = margenAnterior, margen_aplicado = rutaAjustada?.MargenAplicado, margen_sugerido = sugerido, registrado, pdf_invalidado = pdfInvalidado }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    /// <summary>El <c>If-Match</c> se verifica antes que la ruta: con otra versión, <c>CONFLICTO_CONCURRENCIA</c>.</summary>
    private static clsErrorDominio? TieneVersion(clsCotizacionSolicitud cotizacion, int versionEsperada) =>
        cotizacion.TieneVersion(versionEsperada) ? null : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, bool Registrado, bool PdfInvalidado)> AjustarAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, Guid usuarioId, decimal margen, string? justificacion, int versionEsperada,
        CancellationToken cancellationToken)
    {
        if (TieneVersion(cotizacion, versionEsperada) is { } conflicto)
        {
            return (conflicto, false, false);
        }

        if (cotizacion.ValidarAjusteMargen(ruta.Orden, margen, justificacion) is { } invalido)
        {
            return (Detallar(invalido, cotizacion, ruta), false, false);
        }

        var sugerido = ruta.SugeridoVigente;
        var anterior = ruta.MargenAplicado;
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        if (anterior == redondeado)
        {
            // Idempotente: el mismo margen vigente de la ruta no registra un ajuste ni invalida el PDF.
            return (await clsCosteoCotizacionService.VistaConEstadosAsync(cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, _logger, cancellationToken, categoriasConexos, situacionesNexo?.Obtener(cotizacion.Id),
                await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, cancellationToken), tarifasSeguro,
                politicaSeguros), false, false);
        }

        // Vista previa ANTES de cambiar nada: si el módulo de precio rechaza el margen, la cotización queda intacta.
        var recalculo = Recalcular(cotizacion, ruta, redondeado);
        if (recalculo.EsFallo)
        {
            return (recalculo.Error, false, false);
        }

        // El ajuste queda con la ruta REAL (lineaSolicitudId) y el sugerido de esa ruta.
        var registro = clsMargenAjuste.RegistrarNegociacionCosteo(
            sugerido ?? anterior ?? redondeado, redondeado, justificacion, usuarioId, cotizacion.Id, cotizacion.Folio, ruta.MetodoCalculo, ruta.Id);
        if (registro.EsFallo)
        {
            return (registro.Error, false, false);
        }

        var aplicado = cotizacion.AplicarMargenNegociado(ruta.Orden, redondeado, cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt);
        if (aplicado.EsFallo)
        {
            return (aplicado.Error, false, false);
        }

        ajustes.Agregar(registro.Valor);
        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.AjusteMargen, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, false);
        }

        // Con el estado_nexo de cada conexo, igual que GET .../costeo.
        return (await clsCosteoCotizacionService.VistaConEstadosAsync(
            cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, _logger, CancellationToken.None, categoriasConexos, situacionesNexo?.Obtener(cotizacion.Id),
            await Margen.Services.clsAvisoSincronizacionMargenSap.ConsultarSinFallarAsync(avisoSincronizacion, cotizacion, CancellationToken.None),
            tarifasSeguro, politicaSeguros),
            true, pdfInvalidado);
    }

    /// <summary>
    /// Precio de la ruta <paramref name="ruta"/> con <paramref name="margen"/> y el total de la solicitud con ese precio y el de las demás
    /// rutas, SIN cambiar la cotización (<see cref="clsLineaSolicitud.PrecioRutaCon"/>, en la moneda y con la TRM fijadas). La ruta debe
    /// tener costo final (si no, <c>COSTEO_NO_LISTO</c>); el total queda en <c>null</c> si otra ruta todavía no tiene precio. Lo usa también la vista previa del asistente
    /// para el <c>resumen</c> de la acción propuesta <c>ajustar_margen</c>, con los precios que se guardarían.
    /// </summary>
    public static clsResultado<clsRecalculoMargen> Recalcular(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, decimal margen)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        if (cotizacion.ValidarRutaNoRechazada(ruta) is { } rechazada)
        {
            return rechazada;
        }

        if (ruta.CostoFinal is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La ruta {ruta.Orden} de la cotización {cotizacion.Folio} no tiene el costeo de Nexo: no se puede recalcular su precio.");
        }

        if (ruta.PrecioRutaCon(margen, ruta.MetodoCalculo ?? clsReglaMargen.MetodoMargen, cotizacion.Moneda, cotizacion.TrmAplicada, out var precio) is { } error)
        {
            return error;
        }

        // Las rutas rechazadas por la Mesa no cuentan en el total.
        var otras = cotizacion.RutasCotizables.Where(r => r.Id != ruta.Id).ToList();
        decimal? total = otras.All(r => r.PrecioFinalLinea is not null) ? precio!.Value + otras.Sum(r => r.PrecioFinalLinea!.Value) : null;
        return new clsRecalculoMargen(total, ruta, ruta.PrecioFinalLinea, precio!.Value);
    }

    /// <summary>Agrega al error del dominio el <c>detalle</c> que publica el contrato (margen sugerido de la ruta, llamada de la versión nueva).</summary>
    private static clsErrorDominio Detallar(clsErrorDominio error, clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta) => error.Codigo switch
    {
        clsCodigosError.PdfYaEnviado => clsReglaPdfCosteo.ConVersionNueva(error, cotizacion.Folio, MotivoVersion),
        clsCodigosError.JustificacionRequerida => error with
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["margen_sugerido"] = ruta.SugeridoVigente,
                ["linea_orden"] = ruta.Orden,
                ["linea_solicitud_id"] = ruta.Id,
            },
        },
        _ => error,
    };
}

/// <summary>
/// Vista previa del ajuste de una ruta: el total de la solicitud con el precio nuevo de la ruta
/// (<c>null</c> si otra ruta no tiene precio), la ruta y su precio antes y después.
/// </summary>
public sealed record clsRecalculoMargen(decimal? PrecioFinal, clsLineaSolicitud Ruta, decimal? PrecioRutaAnterior, decimal PrecioRuta);
