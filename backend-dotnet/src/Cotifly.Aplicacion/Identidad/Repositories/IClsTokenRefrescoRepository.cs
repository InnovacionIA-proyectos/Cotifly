using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Repositories;

/// <summary>
/// Puerto de los refresh tokens (<c>TokensRefresco</c>, script 0002). <see cref="Agregar"/> se confirma con la unidad de
/// trabajo; la rotación y las revocaciones son operaciones atómicas que se ejecutan de inmediato en la base (un
/// <c>UPDATE</c> condicional no puede esperar al <c>SaveChanges</c>).
/// </summary>
public interface IClsTokenRefrescoRepository
{
    void Agregar(clsTokenRefresco token);

    /// <summary>Token por el SHA-256 hexadecimal del valor opaco (sin seguimiento de cambios), o <c>null</c>.</summary>
    Task<clsTokenRefresco?> ObtenerPorHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotación atómica: inserta <paramref name="nuevo"/> y marca <paramref name="idActual"/> como revocado y reemplazado en
    /// una transacción, con un <c>UPDATE ... WHERE id = @id AND revocadoAt IS NULL AND reemplazadoPorId IS NULL AND
    /// expiraAt &gt; @ahora</c>. Si ninguna fila cumple (ya rotado, revocado o vencido) deshace el insert y devuelve
    /// <c>false</c>: de dos rotaciones simultáneas del mismo token solo una gana. Confirma también los cambios pendientes
    /// del contexto.
    /// </summary>
    Task<bool> RotarAsync(Guid idActual, clsTokenRefresco nuevo, DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>Revoca los tokens vigentes de la familia (cadena de rotación). Devuelve cuántos revocó.</summary>
    Task<int> RevocarFamiliaAsync(Guid familiaId, DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>Revoca todos los tokens vigentes del usuario (reuso de un refresh ya rotado). Devuelve cuántos revocó.</summary>
    Task<int> RevocarActivosDelUsuarioAsync(Guid usuarioId, DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>Borra los tokens vencidos antes de <paramref name="limite"/>. Devuelve cuántos borró.</summary>
    Task<int> PurgarVencidosAsync(DateTimeOffset limite, CancellationToken cancellationToken = default);
}
