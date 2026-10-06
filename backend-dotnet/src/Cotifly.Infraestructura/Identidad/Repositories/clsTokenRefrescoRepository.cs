using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cotifly.Infraestructura.Identidad.Repositories;

/// <summary>
/// Implementación EF Core de <see cref="IClsTokenRefrescoRepository"/>. La rotación y las revocaciones usan
/// <c>ExecuteUpdate</c> (un <c>UPDATE</c> condicional en la base): SQL Server bloquea la fila, así que de dos rotaciones
/// simultáneas la segunda reevalúa el <c>WHERE</c> cuando la primera confirma y no afecta ninguna fila.
/// </summary>
public sealed class clsTokenRefrescoRepository(clsCotiflyDbContext contexto) : IClsTokenRefrescoRepository
{
    public void Agregar(clsTokenRefresco token)
    {
        ArgumentNullException.ThrowIfNull(token);
        contexto.TokensRefresco.Add(token);
    }

    public Task<clsTokenRefresco?> ObtenerPorHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        var hash = tokenHash.ToLowerInvariant();
        return contexto.TokensRefresco.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
    }

    public async Task<bool> RotarAsync(Guid idActual, clsTokenRefresco nuevo, DateTimeOffset ahora, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nuevo);

        await using var transaccion = await contexto.Database.BeginTransactionAsync(cancellationToken);
        contexto.TokensRefresco.Add(nuevo);
        await contexto.SaveChangesAsync(cancellationToken);

        var filas = await contexto.TokensRefresco
            .Where(t => t.Id == idActual && t.RevocadoAt == null && t.ReemplazadoPorId == null && t.ExpiraAt > ahora)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoAt, ahora).SetProperty(t => t.ReemplazadoPorId, nuevo.Id), cancellationToken);

        if (filas == 0)
        {
            await transaccion.RollbackAsync(cancellationToken);
            contexto.ChangeTracker.Clear();
            return false;
        }

        await transaccion.CommitAsync(cancellationToken);
        return true;
    }

    public Task<int> RevocarFamiliaAsync(Guid familiaId, DateTimeOffset ahora, CancellationToken cancellationToken = default) =>
        contexto.TokensRefresco
            .Where(t => t.FamiliaId == familiaId && t.RevocadoAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoAt, ahora), cancellationToken);

    public Task<int> RevocarActivosDelUsuarioAsync(Guid usuarioId, DateTimeOffset ahora, CancellationToken cancellationToken = default) =>
        contexto.TokensRefresco
            .Where(t => t.UsuarioId == usuarioId && t.RevocadoAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevocadoAt, ahora), cancellationToken);

    public async Task<int> PurgarVencidosAsync(DateTimeOffset limite, CancellationToken cancellationToken = default)
    {
        // reemplazadoPorId apunta a la misma tabla: primero se sueltan las referencias hacia las filas que se borran.
        var vencidos = contexto.TokensRefresco.Where(t => t.ExpiraAt < limite).Select(t => t.Id);
        await contexto.TokensRefresco
            .Where(t => t.ReemplazadoPorId != null && vencidos.Contains(t.ReemplazadoPorId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReemplazadoPorId, (Guid?)null), cancellationToken);
        return await contexto.TokensRefresco.Where(t => t.ExpiraAt < limite).ExecuteDeleteAsync(cancellationToken);
    }
}
