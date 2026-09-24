using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Sesiones;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Agente.Repositories;

/// <summary>Implementación EF Core de <see cref="IClsSesionRepository"/> (sesiones + historial).</summary>
public sealed class clsSesionRepository(clsCotiflyDbContext contexto, IClsReloj reloj) : IClsSesionRepository
{
    public async Task<clsSesion?> ObtenerAsync(string spaceId, string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return await contexto.Sesiones.FindAsync([spaceId.Trim(), threadId.Trim()], cancellationToken);
    }

    public async Task<clsSesion> ObtenerOCrearAsync(string spaceId, string threadId, Guid? usuarioId = null, CancellationToken cancellationToken = default)
    {
        var sesion = await ObtenerAsync(spaceId, threadId, cancellationToken);
        if (sesion is not null)
        {
            return sesion;
        }

        var nueva = clsSesion.Crear(spaceId, threadId, reloj.AhoraUtc, usuarioId);
        if (nueva.EsFallo)
        {
            throw new ArgumentException(nueva.Error.Mensaje);
        }

        contexto.Sesiones.Add(nueva.Valor);
        return nueva.Valor;
    }

    public async Task<clsSesion> TocarAsync(string spaceId, string threadId, DateTimeOffset? cuando = null, CancellationToken cancellationToken = default)
    {
        var sesion = await ObtenerOCrearAsync(spaceId, threadId, cancellationToken: cancellationToken);
        sesion.Tocar(cuando ?? reloj.AhoraUtc);
        return sesion;
    }

    public async Task<bool> ArchivarAsync(string spaceId, string threadId, CancellationToken cancellationToken = default)
    {
        var sesion = await ObtenerAsync(spaceId, threadId, cancellationToken);
        sesion?.Archivar();
        return sesion is not null;
    }

    public async Task<IReadOnlyList<clsSesion>> ListarInactivasAsync(DateTimeOffset umbral, CancellationToken cancellationToken = default) =>
        await contexto.Sesiones
            .Where(s => s.Estado == EstadoSesion.Activa && s.UltimaActividad < umbral)
            .OrderBy(s => s.UltimaActividad)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<clsSesion>> ListarPorSpaceAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        var space = spaceId.Trim();
        return await contexto.Sesiones.AsNoTracking()
            .Where(s => s.SpaceId == space)
            .OrderByDescending(s => s.UltimaActividad)
            .ToListAsync(cancellationToken);
    }

    public void AgregarEvento(clsEventoConversacion evento)
    {
        ArgumentNullException.ThrowIfNull(evento);
        contexto.EventosConversacion.Add(evento);
    }

    public async Task<IReadOnlyList<clsEventoConversacion>> HistorialAsync(string spaceId, string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var space = spaceId.Trim();
        var hilo = threadId.Trim();

        return await contexto.EventosConversacion.AsNoTracking()
            .Where(e => e.SpaceId == space && e.ThreadId == hilo)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }
}
