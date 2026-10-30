using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-033 (#199, ADR-023, decisión 6): envía a Nexo, UNA vez, el resultado ganada/perdida que quedó pendiente en Cotifly porque la
/// cotización todavía no estaba Entregada. Lo llama el polling del costeo cuando ve la entrega (o en la agenda de reintento). Nunca
/// entrega la cotización en Nexo (no llama a <c>/entregar</c>: eso es de la Mesa de Costeo). No guarda: el llamador guarda con la versión.
/// </summary>
public interface IClsSincronizacionResultadoNexoService
{
    /// <summary>
    /// Si la cotización tiene un resultado pendiente y está <c>entregada</c>, lo envía a Nexo y actualiza la sincronización. Devuelve
    /// <c>true</c> si cambió algo que ve el comercial (estado de la sincronización o su error): el llamador sube la versión y guarda.
    /// </summary>
    Task<bool> SincronizarAsync(clsCotizacionSolicitud cotizacion, DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>
    /// El resultado pendiente ya no se podrá enviar (la solicitud se descartó, venció o Nexo ya no la reconoce, o pasó la edad máxima):
    /// queda <c>no_sincronizable</c> con el motivo. Devuelve <c>true</c> si cambió.
    /// </summary>
    bool Abandonar(clsCotizacionSolicitud cotizacion, string motivo);
}

/// <summary>Implementación de <see cref="IClsSincronizacionResultadoNexoService"/>.</summary>
/// <remarks>
/// Idempotente: el estado pasa de <c>pendiente</c> a <c>sincronizado</c> en la misma escritura versionada; si esa escritura pierde una carrera,
/// el siguiente ciclo vuelve a enviar el MISMO resultado y Nexo responde 200 (mismo resultado) o <c>RESULTADO_YA_DEFINIDO</c>. Ante
/// <c>RESULTADO_YA_DEFINIDO</c> con otro valor manda Nexo (igual que en <c>/resultado</c>): Cotifly adopta su resultado y lo anota. Un
/// fallo de Nexo suma un intento y reprograma; al llegar a <c>CosteoNexo:MaximoFallosSincronizacionResultado</c> queda
/// <c>no_sincronizable</c>.
/// </remarks>
public sealed class clsSincronizacionResultadoNexoService(
    IClsCosteoNexo nexo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsSincronizacionResultadoNexoService> logger,
    Cotizaciones.Services.IClsCatalogoMotivosPerdida? motivosPerdida = null) : IClsSincronizacionResultadoNexoService
{
    private readonly Cotizaciones.Services.IClsCatalogoMotivosPerdida _motivosPerdida =
        motivosPerdida ?? Cotizaciones.Services.clsCatalogoMotivosPerdida.PorDefecto;

    /// <summary>Evento de auditoría de cada envío del resultado pendiente.</summary>
    public const string HerramientaSincronizacion = "resultado_cotizacion_sincronizacion_nexo";

    public async Task<bool> SincronizarAsync(clsCotizacionSolicitud cotizacion, DateTimeOffset ahora, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (!cotizacion.ResultadoPendienteDeSincronizar || cotizacion.EstadoCosteo != EstadoCosteo.Entregada
            || cotizacion.UsuarioId is not { } usuarioId || cotizacion.SolicitudNexoId is not { } solicitudId)
        {
            return false;
        }

        var cronometro = Stopwatch.StartNew();
        var ganada = cotizacion.Ganada!.Value;
        var cotizacionNexoId = cotizacion.CotizacionNexoId;
        if (cotizacionNexoId is null)
        {
            // DEF-C-1: se LEE la cotización de Nexo (sin "crear o recuperar").
            var recuperada = await nexo.LeerCotizacionAsync(usuarioId, solicitudId, cotizacionNexoId: null, CancellationToken.None);
            if (recuperada.EsFallo && recuperada.Error.Codigo == clsCodigosError.CotizacionNoEntregada)
            {
                // Auditoría de la integración (#199): con DEF-C-1 la lectura ya no crea la cotización en Nexo; si Nexo todavía no la tiene
                // (204 de por-solicitud) es la misma carrera con la Mesa que el COTIZACION_NO_ENTREGADA de abajo: sigue pendiente, sin
                // contar un fallo (antes sumaba intentos y podía dejar el resultado no_sincronizable).
                return await SinEntregaAsync(cotizacion, ahora, ganada, recuperada.Error, cronometro);
            }

            if (recuperada.EsFallo)
            {
                return await FalloAsync(cotizacion, ahora, recuperada.Error, cronometro);
            }

            cotizacionNexoId = recuperada.Valor.Id;
            cotizacion.RegistrarCotizacionNexo(cotizacionNexoId);
        }

        // HU-018.1 (#155): con "Otro" Cotifly guarda "Otro: comentario"; a Nexo va "Otro" y el comentario aparte (el pendiente).
        var motivo = ganada ? null : _motivosPerdida.MotivoParaNexo(Normalizar(cotizacion.MotivoRechazo));
        var marcada = await nexo.MarcarResultadoAsync(
            usuarioId, cotizacionNexoId, new clsResultadoNexo(ganada, motivo, cotizacion.ComentarioResultadoPendiente), CancellationToken.None);
        if (marcada.EsExitoso)
        {
            cotizacion.MarcarResultadoSincronizado(ahora);
            await AuditarAsync(cotizacion, ganada, exito: true, new { resultado_nexo = marcada.Valor.Resultado }, cronometro);
            logger.LogInformation("Resultado pendiente de {Folio} ({Resultado}) enviado a Nexo.", cotizacion.Folio, ganada ? "ganada" : "perdida");
            return true;
        }

        if (marcada.Error.Codigo == clsCodigosError.ResultadoYaDefinido)
        {
            var leida = await nexo.LeerCotizacionAsync(usuarioId, solicitudId, cotizacionNexoId, CancellationToken.None);
            bool? deNexo = leida.EsExitoso
                ? leida.Valor.Resultado.Trim().ToLowerInvariant() switch { "ganada" => true, "perdida" => false, _ => null }
                : null;
            if (deNexo is { } valor)
            {
                string? nota = null;
                if (valor != ganada)
                {
                    // Manda Nexo (como en /resultado): Cotifly adopta su resultado y lo deja anotado.
                    nota = $"Nexo ya tenía la cotización como {(valor ? "ganada" : "perdida")}: Cotifly quedó con ese resultado "
                           + $"(el comercial había marcado {(ganada ? "ganada" : "perdida")}).";
                    cotizacion.MarcarSeguimiento(valor, null, ahora);
                }

                cotizacion.MarcarResultadoSincronizado(ahora, nota);
                await AuditarAsync(cotizacion, ganada, exito: true, new { resultado_nexo = valor ? "ganada" : "perdida", ya_definido = true }, cronometro);
                return true;
            }
        }

        if (marcada.Error.Codigo == clsCodigosError.CotizacionNoEntregada)
        {
            return await SinEntregaAsync(cotizacion, ahora, ganada, marcada.Error, cronometro);
        }

        return await FalloAsync(cotizacion, ahora, marcada.Error, cronometro);
    }

    /// <summary>
    /// Carrera con la Mesa (la deshizo o el polling vio una entrega que Nexo aún no confirma): sigue pendiente, sin contar fallo.
    /// </summary>
    private async Task<bool> SinEntregaAsync(clsCotizacionSolicitud cotizacion, DateTimeOffset ahora, bool ganada, clsErrorDominio error, Stopwatch cronometro)
    {
        cotizacion.ProgramarRelecturaCosteo(ahora + opciones.Value.IntervaloReintentoConexos);
        await AuditarAsync(cotizacion, ganada, exito: false, new { error = new { codigo = error.Codigo, mensaje = error.Mensaje } }, cronometro);
        return false;
    }

    public bool Abandonar(clsCotizacionSolicitud cotizacion, string motivo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        if (!cotizacion.ResultadoPendienteDeSincronizar)
        {
            return false;
        }

        cotizacion.MarcarResultadoNoSincronizable(motivo);
        logger.LogWarning("El resultado pendiente de {Folio} queda solo en Cotifly: {Motivo}", cotizacion.Folio, motivo);
        return true;
    }

    private async Task<bool> FalloAsync(clsCotizacionSolicitud cotizacion, DateTimeOffset ahora, clsErrorDominio error, Stopwatch cronometro)
    {
        var config = opciones.Value;
        cotizacion.RegistrarFalloSincronizacionResultado($"{error.Codigo}: {error.Mensaje}");
        if (cotizacion.IntentosSincronizacionResultado >= config.MaximoFallosSincronizacionResultado)
        {
            cotizacion.MarcarResultadoNoSincronizable(
                $"No se pudo enviar el resultado a Nexo tras {cotizacion.IntentosSincronizacionResultado} intentos ({error.Codigo}): queda solo en Cotifly. "
                + "Regístralo en Nexo con la Mesa de Costeo.");
        }
        else
        {
            cotizacion.ProgramarRelecturaCosteo(ahora + config.IntervaloReintentoConexos);
        }

        logger.LogWarning("No se pudo enviar a Nexo el resultado pendiente de {Folio} ({Codigo}); intentos: {Intentos}.",
            cotizacion.Folio, error.Codigo, cotizacion.IntentosSincronizacionResultado);
        await AuditarAsync(cotizacion, cotizacion.Ganada ?? false, exito: false, new { error = new { codigo = error.Codigo, mensaje = error.Mensaje } }, cronometro);
        return true;
    }

    private Task AuditarAsync(clsCotizacionSolicitud cotizacion, bool ganada, bool exito, object salida, Stopwatch cronometro) =>
        auditoria.RegistrarAsync(
            HerramientaSincronizacion,
            new
            {
                folio = cotizacion.Folio,
                resultado = ganada ? "ganada" : "perdida",
                cotizacion_nexo_id = cotizacion.CotizacionNexoId,
                sincronizacion = cotizacion.EstadoSincronizacionResultado,
            },
            salida,
            exito,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
}
