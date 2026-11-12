using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;

namespace Cotifly.Aplicacion.Correo.Interfaces;

/// <summary>
/// Puerto del transporte de correo. La lógica de confirmación, dueño, deduplicación, protección y trazabilidad es de
/// <c>clsCorreoService</c>; el transporte solo entrega el mensaje. El único transporte es MailSender
/// (<c>Correo:Modo=mailsender</c>, ADR-023), que encola el correo y responde 202 con su id.
/// </summary>
public interface IClsTransporteCorreo
{
    /// <summary>Modo configurado (<c>mailsender</c>).</summary>
    string Modo { get; }

    /// <summary>Entrega el mensaje al transporte y devuelve cómo quedó (con MailSender, encolado con su id).</summary>
    /// <exception cref="clsEnvioCorreoException">
    /// El transporte no aceptó el mensaje: lo rechazó (400/413/422), no respondió a tiempo o no está disponible.
    /// </exception>
    Task<clsEnvioAceptado> EnviarAsync(clsMensajeCorreo mensaje, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consulta y reintento de un correo ya encolado en el transporte (MailSender: <c>GET /api/v1/emails/{id}</c> y
/// <c>POST /api/v1/emails/{id}/retry</c>).
/// </summary>
public interface IClsSeguimientoCorreoExterno
{
    /// <summary>Estado del correo, o <c>null</c> si el transporte no lo conoce (404).</summary>
    /// <exception cref="clsEnvioCorreoException">El transporte no respondió o respondió con un error.</exception>
    Task<clsEstadoEntregaCorreo?> ConsultarAsync(Guid idExterno, string? idCorrelacion, CancellationToken cancellationToken = default);

    /// <summary>Pide un reintento de entrega; <c>false</c> si el transporte no conoce el correo (404).</summary>
    /// <exception cref="clsEnvioCorreoException">El transporte no respondió o respondió con un error.</exception>
    Task<bool> ReintentarAsync(Guid idExterno, string? idCorrelacion, CancellationToken cancellationToken = default);
}
