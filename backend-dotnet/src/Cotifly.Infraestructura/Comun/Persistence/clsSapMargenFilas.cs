namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Fila de <c>dbo_cotifly.SapMargenCliente</c> (<c>db/scripts/17_sap_margen_sincronizacion.sql</c>), la
/// copia local del margen recomendado de SAP TM. La escribe <c>sp_sincronizar_margen_sap</c>, que llama la sincronización del backend
/// (<see cref="Margen.Services.clsSincronizacionMargenSapSql"/>); EF solo la lee (<see cref="Margen.Services.clsFuenteMargenTablaLocal"/>,
/// <c>AsNoTracking</c>).
/// </summary>
public sealed class clsSapMargenClienteFila
{
    public Guid Id { get; private set; }

    public string SalesOrgId { get; private set; } = string.Empty;

    /// <summary>Código crudo de SAP; la línea de negocio sale de <c>SapMapeoTrqType</c>.</summary>
    public string TrqType { get; private set; } = string.Empty;

    /// <summary>BP del cliente tal como lo manda SAP (sin ceros a la izquierda).</summary>
    public string BpSap { get; private set; } = string.Empty;

    public string? Nit { get; private set; }

    /// <summary>Porcentaje tal como lo manda SAP (13.5 = 13,5 %), <c>decimal(9,4)</c>.</summary>
    public decimal MargenRecomendado { get; private set; }

    /// <summary><c>false</c> cuando SAP dejó de traer la combinación (soft delete del stored procedure): no se usa.</summary>
    public bool Activo { get; private set; }

    public DateTimeOffset CreadoAt { get; private set; }

    /// <summary>Última vez que el stored procedure cambió la fila (valor, NIT o reactivación).</summary>
    public DateTimeOffset ActualizadoAt { get; private set; }
}

/// <summary>
/// Fila de <c>dbo_cotifly.SapMargenSyncLog</c> (<c>db/scripts/17</c>), una por corrida de la sincronización
/// (append-only). El backend solo lee la última corrida <c>ok</c> para saber si la copia existe y qué tan vieja es.
/// </summary>
public sealed class clsSapMargenSyncLogFila
{
    /// <summary>Estado de una corrida que sincronizó la tabla.</summary>
    public const string EstadoOk = "ok";

    public Guid Id { get; private set; }

    public DateTimeOffset IniciadoAt { get; private set; }

    public DateTimeOffset? FinalizadoAt { get; private set; }

    public int? FilasInsertadas { get; private set; }

    public int? FilasActualizadas { get; private set; }

    public int? FilasDesactivadas { get; private set; }

    /// <summary><c>ok</c> | <c>error</c> | <c>omitido</c> (CHECK <c>ck_sapMargenSyncLog_status</c>).</summary>
    public string Status { get; private set; } = string.Empty;

    public string? MensajeError { get; private set; }
}
