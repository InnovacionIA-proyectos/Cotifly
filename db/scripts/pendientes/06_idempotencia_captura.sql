/* ============================================================================
   Cotifly — 06 (PROPUESTA, #173): claves de idempotencia de la captura, persistentes
   ============================================================================
   *** PENDIENTE DE REVISIÓN DE @scalderon-bs Y DEL DBA. NO APLICAR. ***
   Está en db/scripts/pendientes/ a propósito: el ejecutador (ejecutar-bd.ps1 / .sh) solo corre los .sql de
   db/scripts (sin subcarpetas). El backend NO mapea esta tabla todavía.

   Por qué (deuda, ADR-018 §12): POST /web/solicitudes-costeo acepta Idempotency-Key (#173). Hoy la clave y la
   captura "en curso" (folio confirmado antes de llamar a Nexo y el solicitudNexoId en cuanto se conoce) viven en
   la memoria del proceso (clsAlmacenIdempotenciaEnMemoria y clsRegistroCapturasIdempotentesEnMemoria). Se pierden
   al reiniciar la API y en un despliegue start-first (dos réplicas a la vez): un reintento que cae en la réplica
   nueva volvería a crear la solicitud en Nexo. Con esta tabla la clave se reserva en la MISMA transacción que
   confirma el folio (INSERT que choca con el UNIQUE si otra réplica ya la tomó) y el reintento lee el folio y el
   solicitudNexoId de aquí, sin llamar a Nexo.

   Qué hace: crea dbo_cotifly.CapturasIdempotentes con UNIQUE (usuarioId, clave). Vigencia de 24 h (expiraAt): el
   backend ignora las vencidas y una tarea de limpieza las borra. Sin FK a CotizacionesSolicitud a propósito: la
   fila se reserva antes de que exista el folio (estado = 'reservada') y se purga sola.

   Idempotente y no destructivo. Va después del 02 (no depende del 03, 04 ni 05).
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF SCHEMA_ID(N'dbo_cotifly') IS NULL
    THROW 50001, N'No existe el esquema dbo_cotifly: corra primero 01_crear_esquema.sql.', 1;
GO

IF OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CapturasIdempotentes (
        id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_capturasIdempotentes_id DEFAULT (NEWID()),
        usuarioId         UNIQUEIDENTIFIER NOT NULL,
        clave             NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- Idempotency-Key (8 a 64)
        huella            CHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,             -- SHA-256 del cuerpo, en hex
        estado            NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- reservada | confirmada | enviada | terminada
        cotizacionId      UNIQUEIDENTIFIER NULL,
        folio             NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        solicitudNexoId   NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        createdAt         DATETIMEOFFSET NOT NULL CONSTRAINT df_capturasIdempotentes_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        updatedAt         DATETIMEOFFSET NOT NULL CONSTRAINT df_capturasIdempotentes_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        expiraAt          DATETIMEOFFSET NOT NULL,
        CONSTRAINT pk_capturasIdempotentes PRIMARY KEY (id),
        CONSTRAINT uq_capturasIdempotentes_usuarioId_clave UNIQUE (usuarioId, clave),
        CONSTRAINT ck_capturasIdempotentes_estado CHECK (estado IN (N'reservada', N'confirmada', N'enviada', N'terminada')),
        CONSTRAINT ck_capturasIdempotentes_clave CHECK (LEN(clave) BETWEEN 8 AND 64)
    );
    CREATE INDEX ix_capturasIdempotentes_expiraAt ON dbo_cotifly.CapturasIdempotentes (expiraAt);
END
GO

/* Verificación (solo lectura). */
SELECT OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') AS tabla,
       (SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'uq_capturasIdempotentes_usuarioId_clave') AS unicos;
GO

/* Reversa (solo con aprobación, y antes de que el backend la mapee):
   DROP TABLE dbo_cotifly.CapturasIdempotentes;
*/
