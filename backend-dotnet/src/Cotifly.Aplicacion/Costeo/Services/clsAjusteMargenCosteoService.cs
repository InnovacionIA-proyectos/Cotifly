using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Ajuste de margen sobre el costeo de Nexo (HU-013.2, ADR-018 §3): <c>PUT /web/cotizaciones/{folio}/costeo/margen</c>. Es el botón
/// de la pantalla y el Confirmar de la acción propuesta <c>ajustar_margen</c> del asistente.
/// </summary>
public interface IClsAjusteMargenCosteoService
{
    /// <summary>
    /// Ajusta el margen de la cotización del comercial y recalcula el precio del flete y de los servicios conexos con
    /// <see cref="IClsPrecioService"/> (misma moneda y TRM fijada). Errores: <c>COTIZACION_NO_ENCONTRADA</c> (404, también si no es
    /// suya), <c>CONFLICTO_CONCURRENCIA</c> (409, <paramref name="versionEsperada"/> no es la de la fila), <c>AJUSTE_MARGEN_INVALIDO</c>
    /// y <c>JUSTIFICACION_REQUERIDA</c> (400), <c>COSTEO_NO_LISTO</c> y <c>PDF_YA_ENVIADO</c> (409). El mismo margen vigente responde
    /// igual sin registrar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AjustarAsync(
        Guid usuarioId, string folio, decimal margen, string? justificacion, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsAjusteMargenCosteoService"/>:
/// <list type="bullet">
/// <item>Las invariantes (rango, costo disponible, regla del PDF y justificación) son del dominio
/// (<see cref="clsCotizacionSolicitud.ValidarAjusteMargen"/>); <c>PDF_YA_ENVIADO</c> sale con la llamada para crear la versión nueva.</item>
/// <item>El precio lo recalcula el dominio por ruta (HU-021, ADR-019): su costo final (fletes, retorno, levante y conexos) con el margen y
/// el método de la ruta; la solicitud suma sus rutas. En otra moneda se convierte con la <c>trm_aplicada</c> de la cotización, nunca con
/// una TRM nueva. Mientras el contrato no diga la ruta (#138), el margen se aplica a todas las rutas.</item>
/// <item>El ajuste queda en <c>MargenAjustes</c> (sugerido = <c>margenPolitica</c>, justificación = <c>motivo</c>) con el comercial;
/// el PDF vigente se invalida y queda en el historial (<see cref="clsReglaPdfCosteo"/>); todo se guarda en una transacción con el
/// control de versión. La acción se audita en <c>EventosHerramienta</c> (sin montos).</item>
/// </list>
/// No reutiliza <c>IClsAjusteMargenService</c> (chat, HU-003.4): ese caso de uso guarda por su cuenta y registra un motivo por
/// defecto; aquí el ajuste, el precio y el PDF se confirman juntos y la justificación es opcional por encima del sugerido.
/// </summary>
public sealed class clsAjusteMargenCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenAjusteRepository ajustes,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsAjusteMargenCosteoService
{
    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "ajustar_margen_costeo";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Ajuste de margen después del envío";

    public async Task<clsResultado<clsCosteoCotizacion>> AjustarAsync(
        Guid usuarioId, string folio, decimal margen, string? justificacion, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var margenAnterior = cotizacion.PrimeraRuta?.MargenAplicado;
        var sugerido = cotizacion.SugeridoVigente;
        var (resultado, registrado, pdfInvalidado) = await AjustarAsync(cotizacion, usuarioId, margen, justificacion, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, margen, justificacion = clsMargenAjuste.LimpiarJustificacion(justificacion) },
            resultado.EsExitoso
                ? (object)new { margen_anterior = margenAnterior, margen_aplicado = cotizacion.PrimeraRuta?.MargenAplicado, margen_sugerido = sugerido, registrado, pdf_invalidado = pdfInvalidado }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, bool Registrado, bool PdfInvalidado)> AjustarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, decimal margen, string? justificacion, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, false);
        }

        if (cotizacion.ValidarAjusteMargen(margen, justificacion) is { } invalido)
        {
            return (Detallar(invalido, cotizacion), false, false);
        }

        var ruta = cotizacion.PrimeraRuta!;
        var sugerido = cotizacion.SugeridoVigente;
        var anterior = ruta.MargenAplicado;
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        if (cotizacion.Rutas.All(r => r.MargenAplicado == redondeado))
        {
            // Idempotente: el mismo margen vigente no registra un ajuste ni invalida el PDF.
            return (clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl), false, false);
        }

        // Vista previa ANTES de cambiar nada: si el módulo de precio rechaza el margen, la cotización queda intacta.
        var recalculo = Recalcular(cotizacion, redondeado);
        if (recalculo.EsFallo)
        {
            return (recalculo.Error, false, false);
        }

        // HU-021: el ajuste queda con la ruta 1 (lineaSolicitudId); mientras el contrato no diga la ruta (#138), el margen aplica a todas.
        var registro = clsMargenAjuste.RegistrarNegociacionCosteo(
            sugerido ?? anterior ?? redondeado, redondeado, justificacion, usuarioId, cotizacion.Id, cotizacion.Folio, ruta.MetodoCalculo, ruta.Id);
        if (registro.EsFallo)
        {
            return (registro.Error, false, false);
        }

        var aplicado = cotizacion.AplicarMargenNegociado(redondeado, cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt);
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

        return (clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl), true, pdfInvalidado);
    }

    /// <summary>
    /// Precio de cada ruta y de la solicitud con <paramref name="margen"/>, SIN cambiar la cotización (HU-021: el precio de la ruta es su
    /// costo final, con fletes, retorno, levante y conexos, con el margen y el método de la ruta, en la moneda y con la TRM fijadas). Todas
    /// las rutas deben tener costo final (si no, <c>COSTEO_NO_LISTO</c>). Lo usa también la vista previa del asistente (HU-013.6, #102)
    /// para el <c>resumen</c> de la acción propuesta <c>ajustar_margen</c>, con los precios que se guardarían.
    /// </summary>
    public static clsResultado<clsRecalculoMargen> Recalcular(clsCotizacionSolicitud cotizacion, decimal margen)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var precios = new List<(clsLineaSolicitud, decimal)>();
        foreach (var ruta in cotizacion.RutasOrdenadas)
        {
            if (ruta.CostoFinal is not { } costo)
            {
                return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                    $"La cotización {cotizacion.Folio} tiene rutas sin costeo de Nexo: no se puede recalcular el precio total.");
            }

            if (clsLineaSolicitud.PrecioCon(costo, margen, ruta.MetodoCalculo ?? clsReglaMargen.MetodoMargen, cotizacion.Moneda, cotizacion.TrmAplicada, out var precio) is { } error)
            {
                return error;
            }

            precios.Add((ruta, precio!.Value));
        }

        return precios.Count == 0
            ? new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} todavía no tiene el costo de Nexo.")
            : new clsRecalculoMargen(precios.Sum(p => p.Item2), precios);
    }

    /// <summary>Agrega al error del dominio el <c>detalle</c> que publica el contrato (margen sugerido, llamada de la versión nueva).</summary>
    private static clsErrorDominio Detallar(clsErrorDominio error, clsCotizacionSolicitud cotizacion) => error.Codigo switch
    {
        clsCodigosError.PdfYaEnviado => clsReglaPdfCosteo.ConVersionNueva(error, cotizacion.Folio, MotivoVersion),
        clsCodigosError.JustificacionRequerida => error with
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["margen_sugerido"] = cotizacion.SugeridoVigente },
        },
        _ => error,
    };
}

/// <summary>Precio total de la solicitud y el de cada ruta con el margen nuevo (HU-013.2; por ruta, HU-021).</summary>
public sealed record clsRecalculoMargen(decimal PrecioFinal, IReadOnlyList<(clsLineaSolicitud Ruta, decimal Precio)> Rutas);
