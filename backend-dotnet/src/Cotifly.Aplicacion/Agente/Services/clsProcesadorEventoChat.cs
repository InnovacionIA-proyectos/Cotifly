using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Procesa un evento de Google Chat fuera del ciclo del webhook (lo llama el worker, un ámbito de DI por evento).</summary>
public interface IClsProcesadorEventoChat
{
    /// <summary>
    /// Identifica al comercial, corre el turno y publica la respuesta en el hilo. Un fallo del agente se informa al comercial en
    /// el hilo y se propaga (el worker lo registra y alerta al space de soporte).
    /// </summary>
    Task ProcesarAsync(clsEventoChat evento, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsProcesadorEventoChat"/> (port de <c>ProcesadorCotizacion</c> de <c>orquestador/procesador.py</c>):
/// <list type="number">
/// <item>Eventos que no son mensajes (<c>ADDED_TO_SPACE</c>...) no cotizan ni publican.</item>
/// <item>Identidad por <c>UsuariosWeb.email</c> (ADR-011 §2): sin comercial → "Ingresa primero a Cotifly desde ClientePro." y no
/// se cotiza; así ninguna cotización queda sin <c>usuarioId</c>.</item>
/// <item>Sesión por <c>spaceId</c> + <c>threadId</c> ligada al comercial: un hilo de otro comercial no se continúa.</item>
/// <item>Respuesta: tarjeta del PDF (con URL firmada de 15 minutos), previsualización o texto (<see cref="clsTarjetasChat"/>).</item>
/// </list>
/// </summary>
public sealed class clsProcesadorEventoChat(
    IClsUsuarioWebRepository usuarios,
    IClsAgenteRunner runner,
    IClsPublicadorChat publicador,
    IClsFirmaUrlPdf firmaUrl,
    IOptions<clsOpcionesPdf> opcionesPdf,
    ILogger<clsProcesadorEventoChat> logger) : IClsProcesadorEventoChat
{
    public async Task ProcesarAsync(clsEventoChat evento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);
        if (!evento.EsMensaje || string.IsNullOrWhiteSpace(evento.SpaceId))
        {
            return;
        }

        var spaceId = evento.SpaceId;
        var threadId = string.IsNullOrWhiteSpace(evento.ThreadId) ? spaceId : evento.ThreadId;
        var usuario = string.IsNullOrWhiteSpace(evento.UsuarioEmail) ? null : await usuarios.ObtenerPorEmailAsync(evento.UsuarioEmail, cancellationToken);
        if (usuario is null)
        {
            logger.LogInformation("Google Chat: el remitente no tiene cuenta en UsuariosWeb; no se cotiza.");
            await publicador.PublicarAsync(spaceId, evento.ThreadId, clsTarjetasChat.Texto(clsTextosAgente.SinIdentidad), cancellationToken);
            return;
        }

        try
        {
            var resultado = await runner.EjecutarTurnoAsync(new clsSolicitudTurno(evento.TextoParaAgente!, spaceId, threadId, usuario.Id), cancellationToken);
            if (resultado.EsFallo)
            {
                var texto = resultado.Error.Codigo == clsCodigosAplicacion.NoEncontrado ? clsTextosAgente.HiloAjeno : clsTextosAgente.SinIdentidad;
                await publicador.PublicarAsync(spaceId, evento.ThreadId, clsTarjetasChat.Texto(texto), cancellationToken);
                return;
            }

            var mensaje = clsTarjetasChat.ParaResultado(resultado.Valor, pdf => UrlFirmada(pdf, usuario.Id));
            await publicador.PublicarAsync(spaceId, evento.ThreadId, mensaje, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try
            {
                await publicador.PublicarAsync(spaceId, evento.ThreadId, clsTarjetasChat.Texto(clsTextosAgente.ParaFallo(ex)), cancellationToken);
            }
            catch (Exception publicacion) when (publicacion is not OperationCanceledException)
            {
                logger.LogWarning(publicacion, "Google Chat: no se pudo avisar el fallo en el hilo.");
            }

            throw;
        }
    }

    /// <summary><c>{Pdf:BaseUrl}/pdfs/{archivo}?t={token}</c>: Google Chat abre el enlace sin <c>Authorization</c>.</summary>
    private string UrlFirmada(clsSalidaPdf pdf, Guid usuarioId)
    {
        // generar_pdf guarda el archivo como {folio}.pdf.
        var archivo = pdf.Folio + ".pdf";
        return $"{clsUrlPdf.Construir(opcionesPdf.Value.BaseUrl, archivo)}?t={Uri.EscapeDataString(firmaUrl.Firmar(archivo, usuarioId))}";
    }
}
