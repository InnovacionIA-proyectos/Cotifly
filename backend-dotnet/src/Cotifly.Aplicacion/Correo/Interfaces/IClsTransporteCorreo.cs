using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;

namespace Cotifly.Aplicacion.Correo.Interfaces;

/// <summary>
/// Puerto del transporte de correo (Strategy, <c>EnviadorDeCorreo</c> del Python). La lógica de confirmación, dueño,
/// deduplicación y trazabilidad es de <c>clsCorreoService</c>; el transporte solo arma y entrega el mensaje. Se elige por
/// <c>Correo:Modo</c>: <c>smtp_relay</c> (relay interno, implementado) o <c>wscorreos</c> / <c>smtp</c> (no portados hasta
/// que el equipo confirme el transporte real, D3).
/// </summary>
public interface IClsTransporteCorreo
{
    /// <summary>Modo configurado (<c>smtp_relay</c>, <c>wscorreos</c>, <c>smtp</c>).</summary>
    string Modo { get; }

    /// <summary>Envía el mensaje y devuelve su <c>Message-ID</c>.</summary>
    /// <exception cref="clsEnvioCorreoException">El transporte falló (red, rechazo del relay, timeout).</exception>
    Task<string> EnviarAsync(clsMensajeCorreo mensaje, CancellationToken cancellationToken = default);
}
