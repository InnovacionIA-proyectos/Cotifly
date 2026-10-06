using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Exceptions;
using Cotifly.Aplicacion.Correo.Interfaces;

namespace Cotifly.Infraestructura.Correo.Services;

/// <summary>
/// Transporte para <c>wscorreos</c> y <c>smtp</c>, que no se portan hasta que el equipo confirme el transporte real (D3). Con
/// el correo habilitado esos modos no arrancan (<c>clsValidadorOpcionesCorreoTransporte</c>); con el correo deshabilitado el
/// caso de uso responde <c>CORREO_DESHABILITADO</c> antes de llegar aquí. Si aun así se usa, falla como envío fallido.
/// </summary>
public sealed class clsTransporteCorreoNoImplementado(string modo) : IClsTransporteCorreo
{
    public string Modo { get; } = modo;

    public Task<string> EnviarAsync(clsMensajeCorreo mensaje, CancellationToken cancellationToken = default) =>
        throw new clsEnvioCorreoException($"El transporte de correo '{Modo}' no está implementado en el backend .NET (pendiente de confirmar, D3).");
}
