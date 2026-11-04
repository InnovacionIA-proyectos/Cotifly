namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// HU-042.2 (#243, ADR-027): una corrida de la sincronización del margen de SAP TM tal como quedó en <c>dbo_cotifly.SapMargenSyncLog</c>
/// (<c>CorridaSincronizacionMargenSap</c> del contrato). Las filas solo vienen en las corridas que llegaron al stored procedure.
/// </summary>
/// <param name="Estado"><c>ok</c>, <c>error</c> u <c>omitido</c> (CHECK <c>ck_sapMargenSyncLog_status</c>).</param>
/// <param name="IniciadoAt">Inicio de la corrida.</param>
/// <param name="FinalizadoAt">Fin de la corrida (<c>null</c> si no se registró).</param>
/// <param name="FilasInsertadas">Combinaciones nuevas (solo con <c>ok</c>).</param>
/// <param name="FilasActualizadas">Combinaciones con valor o NIT nuevo, o reactivadas (solo con <c>ok</c>).</param>
/// <param name="FilasDesactivadas">Combinaciones que SAP dejó de traer (<c>activo = 0</c>, solo con <c>ok</c>).</param>
/// <param name="MensajeError">Motivo del <c>error</c> o del <c>omitido</c>, sin datos de conexión ni credenciales.</param>
public sealed record clsCorridaSincronizacionMargenSap(
    string Estado,
    DateTimeOffset IniciadoAt,
    DateTimeOffset? FinalizadoAt,
    int? FilasInsertadas,
    int? FilasActualizadas,
    int? FilasDesactivadas,
    string? MensajeError)
{
    /// <summary>La corrida sincronizó la tabla.</summary>
    public const string EstadoOk = "ok";

    /// <summary>La corrida falló y la tabla quedó como estaba.</summary>
    public const string EstadoError = "error";

    /// <summary>Otra corrida tenía el candado del stored procedure (<c>sp_getapplock</c>): esta no hizo nada.</summary>
    public const string EstadoOmitido = "omitido";
}

/// <summary>
/// HU-042.2 (#243): <c>GET /web/admin/sap-margen/estado</c> (<c>EstadoSincronizacionMargenSap</c> del contrato).
/// </summary>
/// <param name="UltimaCorrida">La corrida más reciente, cualquiera sea su estado (<c>null</c> si nunca hubo una).</param>
/// <param name="UltimaCorridaOk">La corrida <c>ok</c> más reciente: de ella depende la próxima (<c>null</c> si nunca la hubo).</param>
/// <param name="FilasActivas">Combinaciones activas en <c>SapMargenCliente</c> (las que usa la cotización).</param>
/// <param name="ProximaCorridaEstimada">Cuándo la correrá el worker de esta réplica, según su próximo chequeo y el intervalo.</param>
/// <param name="IntervaloMinutos"><c>SapTm:SincronizacionMinutos</c>: edad máxima de la última corrida <c>ok</c>.</param>
/// <param name="ChequeoMinutos"><c>SapTm:ChequeoMinutos</c>: cada cuánto el worker revisa si toca.</param>
/// <param name="EnCurso">Esta réplica está sincronizando ahora.</param>
/// <param name="NitConfigurado">Hay <c>ConnectionStrings:DwOperaciones</c>: sin ella, cada fila conserva el NIT que ya tenía (o <c>null</c>).</param>
/// <param name="Habilitada"><c>SapTm:SincronizacionHabilitada</c>: apagada, ni el worker ni la corrida forzada sincronizan.</param>
public sealed record clsEstadoSincronizacionMargenSap(
    clsCorridaSincronizacionMargenSap? UltimaCorrida,
    clsCorridaSincronizacionMargenSap? UltimaCorridaOk,
    int FilasActivas,
    DateTimeOffset ProximaCorridaEstimada,
    int IntervaloMinutos,
    int ChequeoMinutos,
    bool EnCurso,
    bool NitConfigurado,
    bool Habilitada);

/// <summary>
/// HU-042.2 (#243): resultado de un intento de sincronización. El worker usa todos los estados; <c>POST /web/admin/sap-margen/sincronizar</c>
/// responde 200 solo con <c>ok</c>, <c>error</c> u <c>omitido</c> (<c>ResultadoSincronizacionMargenSap</c> del contrato): "no toca", "en
/// curso" y "no configurada" son errores del caso de uso.
/// </summary>
/// <param name="Estado">Uno de los <c>Estado*</c> de esta clase.</param>
/// <param name="FilasRecibidas">Filas que entregó SAP TM y pasaron la validación (0 si falló antes).</param>
/// <param name="DuracionMs">Duración del intento.</param>
/// <param name="Corrida">La fila de <c>SapMargenSyncLog</c> de este intento (<c>null</c> si no se llegó a registrar o si no hubo corrida).</param>
public sealed record clsResultadoSincronizacionMargenSap(string Estado, int FilasRecibidas, int DuracionMs, clsCorridaSincronizacionMargenSap? Corrida)
{
    /// <summary>La corrida terminó y el stored procedure sincronizó la tabla.</summary>
    public const string EstadoOk = clsCorridaSincronizacionMargenSap.EstadoOk;

    /// <summary>Falló antes o dentro del stored procedure; la tabla quedó como estaba.</summary>
    public const string EstadoError = clsCorridaSincronizacionMargenSap.EstadoError;

    /// <summary>El stored procedure no tomó el candado: otra réplica estaba sincronizando.</summary>
    public const string EstadoOmitido = clsCorridaSincronizacionMargenSap.EstadoOmitido;

    /// <summary>La última corrida <c>ok</c> todavía no tiene la edad de <c>SapTm:SincronizacionMinutos</c>: no se llamó a SAP.</summary>
    public const string EstadoNoToca = "no_toca";

    /// <summary>Esta réplica ya está sincronizando (worker o petición): no se hizo nada.</summary>
    public const string EstadoEnCurso = "en_curso";

    /// <summary>Deshabilitada, o falta <c>SapTm:Url</c>, <c>SapTm:User</c> o <c>SapTm:Pass</c>, o están mal escritas.</summary>
    public const string EstadoNoConfigurada = "no_configurada";

    /// <summary>Intento sin corrida ("no toca", "en curso", "no configurada").</summary>
    public static clsResultadoSincronizacionMargenSap SinCorrida(string estado) => new(estado, 0, 0, null);
}
