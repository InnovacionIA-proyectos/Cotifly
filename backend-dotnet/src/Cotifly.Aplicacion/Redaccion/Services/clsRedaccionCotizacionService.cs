using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Redaccion.Services;

/// <summary>
/// Redacción del asunto y la introducción del PDF con la IA y el texto estándar de respaldo (HU-013.5, ADR-018 §2a). Nunca falla por
/// la IA: siempre devuelve textos válidos.
/// </summary>
public interface IClsRedaccionCotizacionService
{
    /// <summary>
    /// Pide los textos a <see cref="IClsRedactorCotizacion"/> con el tiempo máximo de <see cref="clsOpcionesRedaccion"/>. Si tarda, falla
    /// o la salida no pasa <see cref="clsReglasTextosCotizacion"/>, devuelve los de <see cref="clsTextosEstandar"/> con
    /// <c>fuente = estandar</c>. Audita la redacción de <paramref name="folio"/>. Solo la cancelación del llamador se propaga.
    /// </summary>
    Task<clsResultadoRedaccion> RedactarAsync(string folio, clsContextoRedaccion contexto, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsRedaccionCotizacionService"/>. La espera se corta con el tiempo máximo aunque el proveedor no
/// atienda la cancelación (<see cref="Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)"/>). La auditoría
/// (<c>EventosHerramienta</c>, <see cref="HerramientaAuditoria"/>) guarda el folio, el proveedor, el modelo, la duración, la fuente, si
/// hubo respaldo y su motivo; nunca los textos ni el contexto (nombres del cliente y del comercial). Va sin <c>ThreadId</c>, como la
/// re-solicitud (#100): el asistente no la ve.
/// </summary>
public sealed class clsRedaccionCotizacionService(
    IClsRedactorCotizacion redactor,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesRedaccion> opciones,
    TimeProvider tiempo,
    ILogger<clsRedaccionCotizacionService> logger) : IClsRedaccionCotizacionService
{
    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "redactar_textos_costeo";

    // Motivos del respaldo que no son de validación (los de validación son los de clsReglasTextosCotizacion).
    public const string MotivoTiempoAgotado = "tiempo_agotado";
    public const string MotivoIaNoDisponible = "ia_no_disponible";

    public async Task<clsResultadoRedaccion> RedactarAsync(string folio, clsContextoRedaccion contexto, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        ArgumentNullException.ThrowIfNull(contexto);
        var identidad = redactor.Identidad;
        var limite = TimeSpan.FromSeconds(opciones.Value.TiempoRedaccionSegundos);
        var cronometro = Stopwatch.StartNew();
        string? modelo = null;
        string? motivo;
        clsValidacionTextosCotizacion? validacion = null;

        using (var corte = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            corte.CancelAfter(limite);
            try
            {
                var respuesta = await redactor.RedactarAsync(contexto, corte.Token).WaitAsync(limite, tiempo, cancellationToken);
                modelo = respuesta.Modelo;
                validacion = clsReglasTextosCotizacion.InterpretarSalidaIa(respuesta.Texto);
                motivo = validacion.Motivo;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                motivo = MotivoTiempoAgotado;
            }
            catch (Exception ex)
            {
                // El proveedor falló (caído, 429, 5xx, red): la causa queda en el log, sin el contexto ni la clave.
                logger.LogWarning("El redactor de la IA falló ({Tipo}); se usan los textos estándar.", ex.GetType().Name);
                motivo = MotivoIaNoDisponible;
            }
        }

        var duracion = (int)Math.Min(int.MaxValue, cronometro.ElapsedMilliseconds);
        var respaldo = motivo is not null;
        if (respaldo)
        {
            logger.LogInformation("La redacción del folio {Folio} usa los textos estándar: {Motivo}.", folio, motivo);
        }

        var resultado = respaldo
            ? new clsResultadoRedaccion(
                clsTextosEstandar.AsuntoEstandar(contexto.LineaNegocio), clsTextosEstandar.IntroduccionEstandar, clsTextosCotizacion.FuenteEstandar,
                identidad.Proveedor, modelo ?? identidad.Modelo, duracion, Respaldo: true, motivo)
            : new clsResultadoRedaccion(
                validacion!.Asunto!, validacion.Introduccion!, clsTextosCotizacion.FuenteIa,
                identidad.Proveedor, modelo ?? identidad.Modelo, duracion, Respaldo: false, null);

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio },
            new
            {
                proveedor = resultado.Proveedor,
                modelo = resultado.Modelo,
                duracion_ms = duracion,
                fuente = resultado.Fuente,
                respaldo,
                motivo_respaldo = motivo,
            },
            exito: true,
            duracion,
            threadId: null);
        return resultado;
    }
}
