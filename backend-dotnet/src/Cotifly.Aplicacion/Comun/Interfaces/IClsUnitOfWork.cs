using Cotifly.Aplicacion.Comun.Exceptions;

namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Unidad de trabajo sobre <c>dbo_cotifly</c> (ADR-002: Repository + Unit of Work). Los repositorios
/// solo agregan o modifican en memoria; nada llega a la base hasta <see cref="GuardarCambiosAsync"/>,
/// que confirma todo en una sola transacción.
/// </summary>
public interface IClsUnitOfWork
{
    /// <summary>Confirma los cambios pendientes y devuelve cuántas filas se escribieron.</summary>
    /// <exception cref="clsViolacionUnicidadException">
    /// Una restricción única rechazó el cambio (ej. <c>uq_cotizaciones_folio</c> → <c>FOLIO_DUPLICADO</c>).
    /// Tras la excepción los cambios pendientes se descartan.
    /// </exception>
    Task<int> GuardarCambiosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Concurrencia optimista sobre una cotización (ADR-018 §8 y §12, script 0006): en una sola transacción bloquea la fila
    /// (<c>UPDLOCK</c>, siempre la cotización antes que sus líneas), compara su <c>versionNegocio</c> con
    /// <paramref name="versionEsperada"/> (el <c>If-Match</c> o la versión que se leyó al cargarla) y solo si coincide confirma los
    /// cambios pendientes. Devuelve <c>false</c> sin escribir nada (y descarta los cambios pendientes) si la versión cambió entretanto,
    /// si la cotización no existe o si SQL Server eligió esta transacción como víctima de un interbloqueo (1205): el llamador responde
    /// <c>CONFLICTO_CONCURRENCIA</c> o reintenta después. Subir la versión es responsabilidad del caso de uso
    /// (<c>clsCotizacion.IncrementarVersionNegocio</c>).
    /// </summary>
    /// <exception cref="clsViolacionUnicidadException">Igual que <see cref="GuardarCambiosAsync"/>.</exception>
    Task<bool> GuardarCambiosSiVersionAsync(Guid cotizacionId, int versionEsperada, CancellationToken cancellationToken = default);
}
