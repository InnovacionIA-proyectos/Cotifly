using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Repositories;

/// <summary><c>jti</c> vigente revocado y su vencimiento.</summary>
public sealed record clsJtiRevocado(string Jti, DateTimeOffset ExpiraAt);

/// <summary>Puerto de los access tokens revocados (<c>TokensRevocados</c>, script 0002).</summary>
public interface IClsTokenRevocadoRepository
{
    /// <summary>Agrega el <c>jti</c> (se confirma con la unidad de trabajo; un <c>jti</c> repetido viola <c>pk_tokensRevocados</c>).</summary>
    void Agregar(clsTokenRevocado token);

    /// <summary><c>jti</c> que todavía no vencieron en <paramref name="ahora"/>.</summary>
    Task<IReadOnlyList<clsJtiRevocado>> ListarVigentesAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>Borra los <c>jti</c> vencidos en <paramref name="ahora"/>. Devuelve cuántos borró.</summary>
    Task<int> PurgarVencidosAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default);
}
