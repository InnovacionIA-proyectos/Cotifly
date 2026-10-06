using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Decorador de auditoría de <see cref="IClsCorreoService"/>: cada envío (exitoso, rechazado o fallido) queda en
/// <c>EventosHerramienta</c> como <c>enviar_correo</c>. El PDF adjunto no se audita (no forma parte de la entrada).
/// </summary>
public sealed class clsCorreoServiceAuditado(IClsCorreoService interno, IClsAuditoriaService auditoria) : IClsCorreoService
{
    public Task<clsResultado<clsSalidaEnvioCorreo>> EnviarAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return auditoria.AuditarAsync(
            clsAuditoriaService.HerramientaEnviarCorreo,
            entrada,
            entrada.ThreadId,
            () => interno.EnviarAsync(entrada, cancellationToken));
    }
}
