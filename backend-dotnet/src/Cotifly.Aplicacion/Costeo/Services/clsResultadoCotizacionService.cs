using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Resultado ganada/perdida de la cotización en Nexo (HU-005.14, ADR-015 §6): <c>POST /web/cotizaciones/{folio}/resultado</c>.
/// Reemplaza al seguimiento interino (<see cref="IClsSeguimientoService"/>) para las cotizaciones que pasaron por Nexo.
/// </summary>
public interface IClsResultadoCotizacionService
{
    /// <summary>
    /// Dos escrituras: primero Nexo y, solo si responde 200, <c>ganada</c>, <c>motivoRechazo</c> y <c>marcadoAt</c> en Cotifly.
    /// Errores: <c>COTIZACION_NO_ENCONTRADA</c> (404), <c>SEGUIMIENTO_INVALIDO</c> (400), <c>COSTEO_NO_LISTO</c> (409: no está en
    /// Nexo), <c>RESULTADO_YA_DEFINIDO</c> y <c>COTIZACION_NO_ENTREGADA</c> (409, de Nexo, sin cambio local) y
    /// <c>COSTEO_NEXO_NO_DISPONIBLE</c>/<c>COSTEO_NEXO_TIEMPO_AGOTADO</c> (503, sin cambio local). Si el costeo no estaba aplicado,
    /// lo relee y aplica el precio en la misma escritura del resultado (HU-013.14, ADR-018 §17).
    /// </summary>
    Task<clsResultado<clsCotizacion>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsResultadoCotizacionService"/>.</summary>
/// <remarks>
/// <para><b><c>comentario</c></b> se reenvía a Nexo tal cual y no se persiste en Cotifly (decisión de la HU-005.14); solo
/// <c>motivo</c> queda en <c>motivoRechazo</c>. Límites de Nexo: <c>motivo</c> 100 y <c>comentario</c> 500 caracteres.</para>
/// <para><b>Entrega implícita.</b> Si Nexo entrega la cotización al marcar el resultado (lo hacía antes de su issue #171), la
/// respuesta trae <c>Entregada</c> y <c>estado_costeo</c> salta a <c>entregada</c> aunque estuviera en <c>autocosteo</c> o
/// <c>pendiente_costeo</c>: es un estado válido, no una inconsistencia.</para>
/// <para><b>Costeo no aplicado (HU-013.14, ADR-018 §17).</b> Si la cotización no tiene precio cuando Nexo acepta el resultado (Nexo
/// entregó y el polling aún no lo leyó), se relee el costeo de Nexo y se aplica con la regla del polling en la MISMA escritura del
/// resultado (con <c>versionNegocio</c>). Si la relectura falla, el resultado se registra igual, la cotización conserva su estado de
/// costeo (nunca <c>entregada</c> sin haber leído el costo) y queda agendada para que el polling aplique el precio en segundos.</para>
/// </remarks>
public sealed class clsResultadoCotizacionService(
    IClsCotizacionRepository cotizaciones,
    IClsCosteoNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IClsPrecioCosteoService precioCosteo,
    IOptions<clsOpcionesCosteoNexo> opciones) : IClsResultadoCotizacionService
{
    public const string HerramientaAuditoria = "resultado_cotizacion_nexo";
    public const int LargoMaximoMotivo = 100;
    public const int LargoMaximoComentario = 500;

    public async Task<clsResultado<clsCotizacion>> MarcarAsync(
        Guid usuarioId, string folio, string? resultado, string? motivo, string? comentario, CancellationToken cancellationToken = default)
    {
        var ganada = resultado?.Trim().ToLowerInvariant() switch
        {
            "ganada" => true,
            "perdida" => (bool?)false,
            _ => null,
        };
        if (ganada is null)
        {
            return Invalido("El campo 'resultado' es obligatorio y debe ser 'ganada' o 'perdida'.");
        }

        if (motivo is { Length: > LargoMaximoMotivo } || comentario is { Length: > LargoMaximoComentario })
        {
            return Invalido($"'motivo' admite máximo {LargoMaximoMotivo} caracteres y 'comentario' {LargoMaximoComentario} (límites de Nexo).");
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (cotizacion.ResultadoSoloLocal || cotizacion.CosteoId is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, cotizacion.ResultadoSoloLocal
                ? $"La cotización {cotizacion.Folio} no tiene resultado en Nexo (chat, descartada o costeo vencido): márcalo con el seguimiento de Cotifly."
                : $"La cotización {cotizacion.Folio} todavía no está en Nexo: reintenta el envío antes de marcar el resultado.");
        }

        // Nexo solo acepta el resultado de una cotización entregada: sin costeo completo no se le pregunta (y no se crea en
        // Nexo un borrador vacío solo para que lo rechace). En autocosteo sí se intenta: el polling pudo no ver aún la entrega.
        if (cotizacion.EstadoCosteo is not (EstadoCosteo.Entregada or EstadoCosteo.Autocosteo or EstadoCosteo.PendienteCosteo))
        {
            return NoEntregada(cotizacion);
        }

        // HU-013.14: en pendiente_costeo Cotifly pudo no haber leído aún un costeo que Nexo ya completó (o entregó). Se lee primero
        // (como el polling) y solo si Nexo ya tiene el costo se sigue; si no, no se le pide el resultado a Nexo.
        if (cotizacion.EstadoCosteo == EstadoCosteo.PendienteCosteo)
        {
            var previa = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.CosteoId!, cancellationToken);
            if (previa.EsFallo && previa.Error.Codigo is clsCodigosError.CosteoNexoNoDisponible or clsCodigosError.CosteoNexoTiempoAgotado)
            {
                return previa.Error;
            }

            if (previa.EsFallo || !clsDerivacionEstadoCosteo.TieneCosteo(clsDerivacionEstadoCosteo.Derivar(previa.Valor)))
            {
                return NoEntregada(cotizacion);
            }
        }

        var cronometro = Stopwatch.StartNew();
        var versionLeida = cotizacion.VersionNegocio;
        var marcada = await MarcarEnNexoAsync(cotizacion, usuarioId, ganada.Value, motivo, comentario, cancellationToken);
        bool? relectura = null;
        if (marcada.EsExitoso)
        {
            // Nexo ya tiene el resultado: desde aquí sin el token de la petición (la relectura y la escritura local no se cortan).
            var foto = await LeerCosteoSiFaltaAsync(cotizacion, usuarioId);
            relectura = foto is null ? null : foto.EsExitoso;
            await RegistrarResultadoAsync(cotizacion, versionLeida, ganada.Value, motivo, marcada.Valor, foto is { EsExitoso: true } ? foto.Valor : null);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, resultado = ganada.Value ? "ganada" : "perdida", motivo, cotizacion_nexo_id = cotizacion.CotizacionNexoId },
            marcada.EsExitoso
                ? (object)new
                {
                    estado_nexo = marcada.Valor.Estado,
                    resultado_nexo = marcada.Valor.Resultado,
                    relectura_costeo = relectura,
                    estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                    con_precio = cotizacion.PrecioFinal is not null,
                }
                : new { error = new { codigo = marcada.Error.Codigo, mensaje = marcada.Error.Mensaje } },
            marcada.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return marcada.EsExitoso ? cotizacion : marcada.Error;
    }

    /// <summary>
    /// Relee el costeo en Nexo solo si la cotización no tiene el precio aplicado (HU-013.14); <c>null</c> si no hace falta. Nunca
    /// lanza por Nexo: un fallo vuelve como resultado fallido y el polling completa el precio después.
    /// </summary>
    private async Task<clsResultado<clsCosteoConsultadoNexo>?> LeerCosteoSiFaltaAsync(clsCotizacion cotizacion, Guid usuarioId)
    {
        if (!RequiereLectura(cotizacion))
        {
            return null;
        }

        return await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.CosteoId!, CancellationToken.None);
    }

    /// <summary>¿Falta aplicar el costeo? Sin precio y sin PDF (con PDF el precio de la versión ya quedó fijo).</summary>
    private static bool RequiereLectura(clsCotizacion cotizacion) => cotizacion.PrecioFinal is null && cotizacion.PdfPath is null;

    /// <summary>
    /// Guarda el resultado que Nexo ya aceptó subiendo la versión de negocio, con <c>GuardarCambiosSiVersionAsync</c> sobre la versión
    /// leída (ADR-018 §12: bloquea primero la cotización, como los demás flujos). Nexo ya lo tiene, así que si otra escritura ganó se
    /// recarga y se vuelve a aplicar (hasta 3 veces); desde ese momento un ajuste responde <c>PDF_YA_ENVIADO</c>. Si el costeo no
    /// estaba aplicado (HU-013.14) se aplica <paramref name="foto"/> en la misma escritura; sin foto (la relectura falló) se agenda al
    /// polling sin tocar el estado de costeo. Si el polling ganó la carrera y ya puso el precio, la recarga lo trae y no se reaplica.
    /// </summary>
    private async Task RegistrarResultadoAsync(
        clsCotizacion cotizacion, int versionLeida, bool ganada, string? motivo, clsCotizacionNexo nexoMarcada, clsCosteoConsultadoNexo? foto)
    {
        var version = versionLeida;
        for (var intento = 0; intento < 3; intento++)
        {
            if (intento > 0)
            {
                await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
                version = cotizacion.VersionNegocio;
            }

            var ahora = reloj.AhoraUtc;
            cotizacion.MarcarSeguimiento(ganada, motivo, ahora);
            if (RequiereLectura(cotizacion))
            {
                if (foto is not null)
                {
                    // Misma regla que el polling: estado, agenda y precio (también por línea, ADR-017) con el margen guardado.
                    var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, CancellationToken.None) ?? [];
                    clsLecturaCosteo.AplicarFoto(cotizacion, lineas, foto, ahora, opciones.Value, precioCosteo);
                }
                else
                {
                    // Sin costo leído: el estado de costeo se conserva y el polling aplica el precio (nunca "entregada" huérfana).
                    cotizacion.ProgramarRelecturaCosteo(ahora);
                }
            }
            else if (string.Equals(nexoMarcada.Estado, clsCotizacionNexo.EstadoEntregada, StringComparison.OrdinalIgnoreCase)
                && cotizacion.EstadoCosteo != EstadoCosteo.Entregada)
            {
                // Entrega implícita (o entrega que el polling aún no vio) con el precio ya aplicado: válida, no una inconsistencia.
                cotizacion.RegistrarConsultaCosteo(EstadoCosteo.Entregada, nexoMarcada.Id, ahora, siguiente: null);
            }

            cotizacion.IncrementarVersionNegocio();
            if (await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
            {
                return;
            }
        }

        throw new InvalidOperationException($"No se pudo registrar el resultado de la cotización {cotizacion.Folio} después de 3 intentos (Nexo ya lo tiene).");
    }

    private async Task<clsResultado<clsCotizacionNexo>> MarcarEnNexoAsync(
        clsCotizacion cotizacion, Guid usuarioId, bool ganada, string? motivo, string? comentario, CancellationToken cancellationToken)
    {
        var cotizacionNexoId = cotizacion.CotizacionNexoId;
        if (cotizacionNexoId is null)
        {
            // El polling todavía no la vio: "crear o recuperar" la cotización de Nexo de la solicitud.
            var recuperada = await nexo.ObtenerCotizacionAsync(usuarioId, cotizacion.CosteoId!, cancellationToken);
            if (recuperada.EsFallo)
            {
                return recuperada.Error;
            }

            cotizacionNexoId = recuperada.Valor.Id;
            cotizacion.RegistrarCotizacionNexo(cotizacionNexoId);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }

        return await nexo.MarcarResultadoAsync(
            usuarioId, cotizacionNexoId, new clsResultadoNexo(ganada, Normalizar(motivo), Normalizar(comentario)), cancellationToken);
    }

    private static clsErrorDominio NoEntregada(clsCotizacion cotizacion) => new(clsCodigosError.CotizacionNoEntregada,
        $"La cotización {cotizacion.Folio} todavía no tiene el costeo entregado por Nexo: no se puede marcar el resultado.");

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static clsErrorDominio Invalido(string mensaje) => new(clsSeguimientoService.CodigoSeguimientoInvalido, mensaje);
}
