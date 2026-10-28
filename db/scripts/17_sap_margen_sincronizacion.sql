-- HU-042.1 (#242): copia local del margen recomendado de SAP TM (RentabClienteSet) y log de corridas del job
-- de sincronización diario. Dueña: Datos (@scalderon-bs). No lo lee backend-dotnet (no lo exige /health/ready);
-- lo consume HU-042.2 (#243) cuando ese trabajo se haga. Idempotente, en una transacción, sin respaldo (tablas nuevas).
--
-- Objetos que crea:
--   dbo_cotifly.SapMargenCliente     - una fila por (salesOrgId, trqType, bpSap), estado actual, con el nit
--                                      ya resuelto para cruzar directo con CotizacionesSolicitud
--   dbo_cotifly.SapMargenSyncLog     - log de corridas (append-only), una fila por ejecución del job
--   dbo_cotifly.sp_sincronizar_margen_sap - hace el upsert desde #SapMargenStage y escribe el log, todo en una transacción
--
-- #SapMargenStage NO es una tabla fija del esquema: es una tabla temporal local que crea el propio paso de
-- PowerShell en su conexión, justo antes de llamar al SP (que se ejecuta en esa misma conexión/sesión, por
-- eso puede verla). Se llena una vez por corrida y desaparece sola al cerrar la conexión — sin TRUNCATE
-- manual y sin que dos corridas puedan pisarse mientras cargan datos (cada una tiene la suya, privada).
--
-- El job que arma #SapMargenStage y llama al SP vive en db/jobs/sincronizar_margen_sap.ps1
-- (paso de PowerShell de un SQL Server Agent Job, no en backend-dotnet).

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo_cotifly.SapMargenCliente', N'U') IS NULL
BEGIN
    -- id propio (igual que Roles/MargenesPolitica, INT IDENTITY) además de la clave de negocio, que queda
    -- como restricción única en vez de PK.
    CREATE TABLE dbo_cotifly.SapMargenCliente
    (
        id                INT IDENTITY(1, 1) NOT NULL CONSTRAINT pk_sapMargenCliente PRIMARY KEY,
        salesOrgId        NVARCHAR(20)     NOT NULL,
        trqType           NVARCHAR(8)      NOT NULL,
        bpSap             NVARCHAR(20)     NOT NULL,
        -- NIT del cliente, para cruzar directo con CotizacionesSolicitud (que guarda NIT, no BP) sin que
        -- Duban tenga que resolver el cruce aparte. Nullable: puede no resolverse para algunos BP (ver
        -- nota de la columna equivalente en #SapMargenStage sobre cómo se llena).
        nit               NVARCHAR(20)     NULL,
        -- Código crudo de SAP; el nombre legible de la línea de negocio se obtiene cruzando (trqType,
        -- salesOrgId) contra dbo_cotifly.SapMapeoTrqType (trqType, salesOrg) — no se duplica aquí.
        margenRecomendado DECIMAL(9, 4)    NOT NULL,
        activo            BIT              NOT NULL CONSTRAINT df_sapMargenCliente_activo DEFAULT (1),
        creadoAt          DATETIMEOFFSET   NOT NULL CONSTRAINT df_sapMargenCliente_creadoAt DEFAULT (SYSDATETIMEOFFSET()),
        actualizadoAt     DATETIMEOFFSET   NOT NULL CONSTRAINT df_sapMargenCliente_actualizadoAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT uq_sapMargenCliente_clave UNIQUE (salesOrgId, trqType, bpSap)
    );
    CREATE INDEX idx_sapMargenCliente_nit ON dbo_cotifly.SapMargenCliente (nit) WHERE nit IS NOT NULL;
    PRINT N'17: tabla dbo_cotifly.SapMargenCliente creada.';
END
ELSE
    PRINT N'17: dbo_cotifly.SapMargenCliente ya existe, no se toca.';

IF OBJECT_ID(N'dbo_cotifly.SapMargenSyncLog', N'U') IS NULL
BEGIN
    -- Append-only: una fila por corrida del job, no por registro de SAP (mismo patrón que
    -- dbo_prueba.fo_snapshot_sync_log de EmbarQA/etl-sap).
    CREATE TABLE dbo_cotifly.SapMargenSyncLog
    (
        id                BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT pk_sapMargenSyncLog PRIMARY KEY,
        iniciadoAt        DATETIMEOFFSET  NOT NULL CONSTRAINT df_sapMargenSyncLog_iniciadoAt DEFAULT (SYSDATETIMEOFFSET()),
        finalizadoAt      DATETIMEOFFSET  NULL,
        filasInsertadas   INT             NULL,
        filasActualizadas INT             NULL,
        filasDesactivadas INT             NULL,
        status            NVARCHAR(20)    NOT NULL,
        mensajeError      NVARCHAR(2000)  NULL,
        CONSTRAINT ck_sapMargenSyncLog_status CHECK (status IN (N'ok', N'error', N'omitido'))
    );
    CREATE INDEX idx_sapMargenSyncLog_iniciado ON dbo_cotifly.SapMargenSyncLog (iniciadoAt DESC);
    PRINT N'17: tabla dbo_cotifly.SapMargenSyncLog creada.';
END
ELSE
    PRINT N'17: dbo_cotifly.SapMargenSyncLog ya existe, no se toca.';

COMMIT;
GO

CREATE OR ALTER PROCEDURE dbo_cotifly.sp_sincronizar_margen_sap
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iniciadoAt DATETIMEOFFSET = SYSDATETIMEOFFSET();
    DECLARE @lockResultado INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Evita que dos corridas se solapen (igual que EmbarQA con fo_snapshot).
        EXEC @lockResultado = sp_getapplock
             @Resource = N'sp_sincronizar_margen_sap',
             @LockMode = N'Exclusive',
             @LockOwner = N'Transaction',
             @LockTimeout = 0;

        IF @lockResultado < 0
        BEGIN
            ROLLBACK;
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
            VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'omitido', N'Ya hay otra corrida en curso (sp_getapplock no se pudo tomar).');
            RETURN;
        END

        DECLARE @filasInsertadas INT, @filasActualizadas INT, @filasDesactivadas INT;
        DECLARE @acciones TABLE (accion NVARCHAR(10) NOT NULL, activoResultante BIT NOT NULL);

        -- Una sola fila MATCHED por combinación: "cambió el valor" y "estaba desactivada y reaparece en SAP"
        -- quedan en la misma rama (las dos son, en la práctica, poner la fila al día); NOT MATCHED BY SOURCE
        -- es la única rama que apaga una fila.
        -- #SapMargenStage la crea y llena el llamador (PowerShell) en esta misma conexión antes de este EXEC;
        -- no es una tabla del esquema, por eso no aparece en el CREATE OR ALTER de más arriba.
        MERGE dbo_cotifly.SapMargenCliente AS destino
        USING #SapMargenStage AS origen
            ON destino.salesOrgId = origen.salesOrgId
           AND destino.trqType = origen.trqType
           AND destino.bpSap = origen.bpSap
        WHEN MATCHED AND (destino.margenRecomendado <> origen.margenRecomendado
                           OR destino.activo = 0
                           OR ISNULL(destino.nit, N'') <> ISNULL(origen.nit, N'')) THEN
            UPDATE SET margenRecomendado = origen.margenRecomendado,
                       nit = origen.nit,
                       activo = 1,
                       actualizadoAt = SYSDATETIMEOFFSET()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (salesOrgId, trqType, bpSap, nit, margenRecomendado)
            VALUES (origen.salesOrgId, origen.trqType, origen.bpSap, origen.nit, origen.margenRecomendado)
        WHEN NOT MATCHED BY SOURCE AND destino.activo = 1 THEN
            UPDATE SET activo = 0, actualizadoAt = SYSDATETIMEOFFSET()
        OUTPUT $action, inserted.activo INTO @acciones (accion, activoResultante);

        SELECT @filasInsertadas = SUM(CASE WHEN accion = N'INSERT' THEN 1 ELSE 0 END),
               @filasActualizadas = SUM(CASE WHEN accion = N'UPDATE' AND activoResultante = 1 THEN 1 ELSE 0 END),
               @filasDesactivadas = SUM(CASE WHEN accion = N'UPDATE' AND activoResultante = 0 THEN 1 ELSE 0 END)
        FROM @acciones;

        INSERT INTO dbo_cotifly.SapMargenSyncLog
            (iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, status)
        VALUES
            (@iniciadoAt, SYSDATETIMEOFFSET(), ISNULL(@filasInsertadas, 0), ISNULL(@filasActualizadas, 0), ISNULL(@filasDesactivadas, 0), N'ok');

        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
        VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'error', LEFT(ERROR_MESSAGE(), 2000));
        THROW;
    END CATCH
END
GO
