namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// #191: fila de <c>dbo_cotifly.CapturasIdempotentes</c> (<c>db/scripts/06_idempotencia_captura.sql</c>). Solo describe la tabla en el
/// modelo EF (la sonda <c>/health/ready</c> y las pruebas de esquema la leen de ahí): la leen y escriben
/// <see cref="clsAlmacenIdempotenciaSql"/> y <see cref="clsRegistroCapturasIdempotentesSql"/> con SQL propio (INSERT que choca con el
/// UNIQUE, UPDATE condicionado por la toma), nunca el <c>ChangeTracker</c>.
/// </summary>
public sealed class clsCapturaIdempotenteFila
{
    public Guid Id { get; private set; }

    public Guid UsuarioId { get; private set; }

    public string Operacion { get; private set; } = string.Empty;

    public string Clave { get; private set; } = string.Empty;

    public string Huella { get; private set; } = string.Empty;

    public string Estado { get; private set; } = string.Empty;

    public Guid? TomadaPor { get; private set; }

    public DateTimeOffset? EnCursoHasta { get; private set; }

    public string? Resultado { get; private set; }

    public Guid? CotizacionId { get; private set; }

    public string? Folio { get; private set; }

    public string? SolicitudNexoId { get; private set; }

    public decimal? ScorePrioridad { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset ExpiraAt { get; private set; }
}
