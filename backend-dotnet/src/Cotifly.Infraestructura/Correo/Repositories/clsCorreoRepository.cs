using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Correo;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Correo.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsCorreoRepository"/>.</summary>
public sealed class clsCorreoRepository(clsCotiflyDbContext contexto) : IClsCorreoRepository
{
    public void Agregar(clsCorreo correo)
    {
        ArgumentNullException.ThrowIfNull(correo);
        contexto.Correos.Add(correo);
    }

    public async Task<IReadOnlyList<clsCorreo>> ListarEnviadosDesdeAsync(Guid cotizacionId, DateTimeOffset desde, CancellationToken cancellationToken = default) =>
        await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId && c.Estado == EstadoCorreo.Enviado && c.CreatedAt >= desde)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<clsCorreo?> ObtenerUltimoEnviadoAClienteAsync(Guid usuarioId, string nitCliente, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nitCliente);
        var nit = nitCliente.Trim();
        var cotizacionesDelCliente = contexto.Cotizaciones
            .Where(c => c.UsuarioId == usuarioId && c.NitCliente == nit)
            .Select(c => c.Id);

        return await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.Estado == EstadoCorreo.Enviado && cotizacionesDelCliente.Contains(c.CotizacionId))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
}
