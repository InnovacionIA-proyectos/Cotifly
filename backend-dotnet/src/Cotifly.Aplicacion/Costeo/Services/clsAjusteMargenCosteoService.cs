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
/// (<see cref="clsCotizacion.ValidarAjusteMargen"/>); <c>PDF_YA_ENVIADO</c> sale con la llamada para crear la versión nueva.</item>
/// <item>El precio sale de <see cref="IClsPrecioService"/> con el método de la cotización: el flete sobre <c>costo</c>, cada línea de
/// una licitación sobre su costo (el total es la suma) y cada conexo sobre su costo de Nexo. En otra moneda se convierte con la
/// <c>trm_aplicada</c> de la cotización, nunca con una TRM nueva.</item>
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
    IClsPrecioService precios,
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

        var margenAnterior = cotizacion.MargenAplicado;
        var sugerido = cotizacion.SugeridoVigente;
        var (resultado, registrado, pdfInvalidado) = await AjustarAsync(cotizacion, usuarioId, margen, justificacion, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, margen, justificacion = clsMargenAjuste.LimpiarJustificacion(justificacion) },
            resultado.EsExitoso
                ? (object)new { margen_anterior = margenAnterior, margen_aplicado = cotizacion.MargenAplicado, margen_sugerido = sugerido, registrado, pdf_invalidado = pdfInvalidado }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, bool Registrado, bool PdfInvalidado)> AjustarAsync(
        clsCotizacion cotizacion, Guid usuarioId, decimal margen, string? justificacion, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false, false);
        }

        if (cotizacion.ValidarAjusteMargen(margen, justificacion) is { } invalido)
        {
            return (Detallar(invalido, cotizacion), false, false);
        }

        var sugerido = cotizacion.SugeridoVigente;
        var anterior = cotizacion.MargenAplicado;
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        if (anterior == redondeado)
        {
            // Idempotente: el mismo margen vigente no registra un ajuste ni invalida el PDF.
            return (clsCosteoCotizacionService.Vista(cotizacion, opcionesPdf.Value.BaseUrl), false, false);
        }

        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var recalculo = Recalcular(cotizacion, lineas, redondeado);
        if (recalculo.EsFallo)
        {
            return (recalculo.Error, false, false);
        }

        var registro = clsMargenAjuste.RegistrarNegociacionCosteo(
            sugerido ?? anterior ?? redondeado, redondeado, justificacion, usuarioId, cotizacion.Id, cotizacion.Folio, cotizacion.MetodoCalculo);
        if (registro.EsFallo)
        {
            return (registro.Error, false, false);
        }

        cotizacion.AplicarMargenNegociado(redondeado);
        foreach (var (linea, precioLinea) in recalculo.Valor.Lineas)
        {
            linea.RegistrarPrecio(precioLinea);
        }

        cotizacion.RegistrarPrecioAjustado(recalculo.Valor.PrecioFinal, recalculo.Valor.ServiciosConexos);
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
    /// Recalcula con <see cref="IClsPrecioService"/> y el margen nuevo. Una licitación o cotización de varias líneas suma el precio de
    /// sus líneas (todas deben tener costo, si no <c>COSTEO_NO_LISTO</c>); una de una línea usa <c>costo</c>. Los conexos conservan si
    /// están incluidos.
    /// </summary>
    public clsResultado<clsRecalculoMargen> Recalcular(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, decimal margen) =>
        Recalcular(precios, cotizacion, lineas, margen);

    /// <summary>
    /// El mismo recálculo sin instancia del caso de uso y SIN cambiar la cotización: lo usa también la vista previa del asistente
    /// (HU-013.6, #102) para el <c>resumen</c> de la acción propuesta <c>ajustar_margen</c>, con los precios que se guardarían.
    /// </summary>
    public static clsResultado<clsRecalculoMargen> Recalcular(
        IClsPrecioService precios, clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, decimal margen)
    {
        ArgumentNullException.ThrowIfNull(precios);
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        var metodo = cotizacion.MetodoCalculo ?? clsReglaMargen.MetodoMargen;
        clsResultado<decimal> Precio(decimal costo) => clsPrecioMonedaCosteo.Calcular(precios, cotizacion, costo, margen, metodo);

        var preciosLineas = new List<(clsCotizacionLineaSolicitud, decimal)>();
        foreach (var linea in lineas.Where(l => l.Costo is not null).OrderBy(l => l.Orden))
        {
            var precioLinea = Precio(linea.Costo!.Value);
            if (precioLinea.EsFallo)
            {
                return precioLinea.Error;
            }

            preciosLineas.Add((linea, precioLinea.Valor));
        }

        decimal precioFinal;
        if (lineas.Count > 1)
        {
            if (preciosLineas.Count != lineas.Count)
            {
                return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                    $"La cotización {cotizacion.Folio} tiene líneas sin costeo de Nexo: no se puede recalcular el precio total.");
            }

            precioFinal = preciosLineas.Sum(p => p.Item2);
        }
        else
        {
            if (cotizacion.Costo is not { } costo)
            {
                return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {cotizacion.Folio} todavía no tiene el costo de Nexo.");
            }

            var precio = Precio(costo);
            if (precio.EsFallo)
            {
                return precio.Error;
            }

            precioFinal = precio.Valor;
        }

        var conexos = new List<clsServicioConexoCosteo>();
        foreach (var conexo in clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos))
        {
            var precioConexo = Precio(conexo.Costo);
            if (precioConexo.EsFallo)
            {
                return precioConexo.Error;
            }

            conexos.Add(conexo with { Precio = precioConexo.Valor });
        }

        return new clsRecalculoMargen(precioFinal, clsServiciosConexosJson.Escribir(conexos), preciosLineas);
    }

    /// <summary>Agrega al error del dominio el <c>detalle</c> que publica el contrato (margen sugerido, llamada de la versión nueva).</summary>
    private static clsErrorDominio Detallar(clsErrorDominio error, clsCotizacion cotizacion) => error.Codigo switch
    {
        clsCodigosError.PdfYaEnviado => clsReglaPdfCosteo.ConVersionNueva(error, cotizacion.Folio, MotivoVersion),
        clsCodigosError.JustificacionRequerida => error with
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["margen_sugerido"] = cotizacion.SugeridoVigente },
        },
        _ => error,
    };
}

/// <summary>Precio del flete (o total de las líneas), el de cada línea con costo y los conexos con su precio nuevo (HU-013.2).</summary>
public sealed record clsRecalculoMargen(decimal PrecioFinal, string? ServiciosConexos, IReadOnlyList<(clsCotizacionLineaSolicitud Linea, decimal Precio)> Lineas);
