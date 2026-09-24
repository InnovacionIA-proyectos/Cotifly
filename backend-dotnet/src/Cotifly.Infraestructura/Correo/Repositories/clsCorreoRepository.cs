using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Correo;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Correo.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsCorreoRepository"/>.</summary>
public sealed class clsCorreoRepository(clsCotiflyDbContext contexto, IClsReloj reloj) : IClsCorreoRepository
{
    public void Agregar(clsCorreo correo)
    {
        ArgumentNullException.ThrowIfNull(correo);
        contexto.Correos.Add(correo);
    }

    public async Task<bool> ExisteDuplicadoRecienteAsync(
        Guid cotizacionId,
        IEnumerable<string> para,
        TimeSpan? ventana = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(para);
        var objetivo = para.ToList();
        var desde = reloj.AhoraUtc - (ventana ?? IClsCorreoRepository.VentanaDuplicado);

        var recientes = await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId && c.Estado == EstadoCorreo.Enviado && c.CreatedAt >= desde)
            .ToListAsync(cancellationToken);

        return recientes.Any(c => c.TieneMismosPara(objetivo));
    }

    public async Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
}
