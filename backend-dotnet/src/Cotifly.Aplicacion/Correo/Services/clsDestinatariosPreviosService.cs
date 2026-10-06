using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Repositories;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>Implementación de <see cref="IClsDestinatariosPreviosService"/>.</summary>
public sealed class clsDestinatariosPreviosService(IClsCorreoRepository correos) : IClsDestinatariosPreviosService
{
    public async Task<clsDestinatarios> ObtenerAsync(Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default)
    {
        if (usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(nitCliente))
        {
            return clsDestinatarios.Vacios;
        }

        var ultimo = await correos.ObtenerUltimoEnviadoAClienteAsync(usuarioId, nitCliente.Trim(), cancellationToken);
        return ultimo is null ? clsDestinatarios.Vacios : new clsDestinatarios(ultimo.Para, ultimo.Cc);
    }
}
