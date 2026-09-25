namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Consulta de access tokens revocados por <c>jti</c> (logout, ADR-009). La validación del JWT la llama en cada petición
/// autenticada, así que responde desde memoria: los revocados en este proceso se ven al instante y los de otras réplicas
/// cuando se recarga la foto de <c>TokensRevocados</c> (cada 60 s).
/// </summary>
public interface IClsRevocacionAccesos
{
    /// <summary>
    /// <c>true</c> si el <c>jti</c> está revocado. Falla cerrado: si la foto nunca se pudo cargar o tiene más de 5 minutos
    /// sin poder recargarse, todo <c>jti</c> se considera revocado.
    /// </summary>
    Task<bool> EstaRevocadoAsync(string jti, CancellationToken cancellationToken = default);

    /// <summary>Revoca el <c>jti</c> en este proceso de inmediato (el logout además lo persiste).</summary>
    void RegistrarLocal(string jti, DateTimeOffset expiraAt);
}
