using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Correo;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Correo.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsCorreoRepository"/>.</summary>
public sealed class clsCorreoRepository(clsCotiflyDbContext contexto) : IClsCorreoRepository
{
    /// <summary>Estados que cuentan como envío (deduplicación y destinatarios previos): salió o sigue en curso en MailSender.</summary>
    private static readonly EstadoCorreo?[] _cuentan = [EstadoCorreo.Enviado, EstadoCorreo.Encolado, EstadoCorreo.Enviando];

    public void Agregar(clsCorreo correo)
    {
        ArgumentNullException.ThrowIfNull(correo);
        contexto.Correos.Add(correo);
    }

    public async Task<IReadOnlyList<clsCorreo>> ListarEnviadosDesdeAsync(Guid cotizacionId, DateTimeOffset desde, CancellationToken cancellationToken = default) =>
        await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId && _cuentan.Contains(c.Estado) && c.CreatedAt >= desde)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<clsCorreo?> ObtenerUltimoEnviadoAClienteAsync(Guid usuarioId, string nitCliente, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nitCliente);
        var nit = nitCliente.Trim();
        var cotizacionesDelCliente = contexto.CotizacionesSolicitud
            .Where(c => c.UsuarioId == usuarioId && c.NitCliente == nit)
            .Select(c => c.Id);

        return await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => _cuentan.Contains(c.Estado) && cotizacionesDelCliente.Contains(c.CotizacionId))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<clsCorreo>> ListarPorCotizacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default) =>
        await contexto.Correos.AsNoTracking()
            .Include(c => c.Destinatarios)
            .Where(c => c.CotizacionId == cotizacionId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<clsCorreo?> ObtenerAsync(Guid correoId, CancellationToken cancellationToken = default) =>
        contexto.Correos.Include(c => c.Destinatarios).FirstOrDefaultAsync(c => c.Id == correoId, cancellationToken);

    public async Task<IReadOnlyList<clsConsultaCorreoPendiente>> ListarConsultasPendientesAsync(
        DateTimeOffset ahora, int lote, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lote, 1);
        return await contexto.Correos.AsNoTracking()
            .Where(c => c.SiguienteConsultaAt != null && c.SiguienteConsultaAt <= ahora)
            .OrderBy(c => c.SiguienteConsultaAt)
            .Take(lote)
            .Select(c => new clsConsultaCorreoPendiente(c.Id, c.SiguienteConsultaAt!.Value))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReclamarConsultaAsync(Guid correoId, DateTimeOffset esperada, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default)
    {
        var filas = await contexto.Correos
            .Where(c => c.Id == correoId && c.SiguienteConsultaAt == esperada)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.SiguienteConsultaAt, (DateTimeOffset?)reclamoHasta), cancellationToken);
        return filas == 1;
    }
}
