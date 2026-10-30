/* ============================================================================
   Cotifly — 13: correo por MailSender (estado asíncrono) y resultado diferido a Nexo
   ============================================================================
   HU-033 (#199, ADR-023). Decisiones del líder técnico del 2026-10-22:
     - El correo de la cotización sale por el microservicio interno MailSender (POST /api/v1/emails, 202 con un id) y no por SMTP
       directo. MailSender lo encola y lo entrega en segundo plano: Cotifly guarda el id, consulta el estado (GET /api/v1/emails/{id})
       con un worker y lo muestra en el historial de envíos.
     - Si el comercial marca ganada/perdida cuando la cotización todavía no está Entregada en Nexo, el resultado se guarda solo en
       Cotifly ("pendiente de sincronizar") y el polling del costeo lo envía a Nexo una sola vez cuando la Mesa la entregue.

   Qué hace (NO destructivo e idempotente: solo agrega; no cambia datos):
     1. Correos (un envío de la cotización):
          idMailSender         UNIQUEIDENTIFIER NULL   id del correo en MailSender (índice único filtrado: un id, una fila)
          redirigidoA          NVARCHAR(320)    NULL   buzón de pruebas al que se redirigió fuera de producción (los destinatarios
                                                       guardados en CorreoDestinatarios siguen siendo los originales)
          intentosEntrega      INT              NULL   intentos de entrega al relay que lleva MailSender (attemptCount)
          enviadoAt            DATETIMEOFFSET   NULL   cuándo el relay lo aceptó (sentAt de MailSender)
          estadoActualizadoAt  DATETIMEOFFSET   NULL   última consulta del estado en MailSender
          siguienteConsultaAt  DATETIMEOFFSET   NULL   agenda del worker de correos (NULL = estado final); índice filtrado
          encoladoAt           DATETIMEOFFSET   NULL   última aceptación de MailSender (envío o reintento): desde aquí se mide la
                                                       edad máxima de la consulta (NULL = se usa createdAt)
        y el CHECK ck_correos_estado: estado NULL o 'encolado' | 'enviando' | 'enviado' | 'fallido' (antes sin CHECK: solo
        'enviado' y 'fallido', que el CHECK admite).
     2. CotizacionesSolicitud (la cabecera):
          sincronizacionResultadoNexo      NVARCHAR(20)   NULL  'pendiente' | 'sincronizado' | 'no_sincronizable' (CHECK)
          comentarioResultadoPendiente     NVARCHAR(500)  NULL  comentario para Nexo mientras el resultado está pendiente (límite de Nexo)
          resultadoSincronizadoAt          DATETIMEOFFSET NULL  cuándo Nexo aceptó el resultado
          errorSincronizacionResultado     NVARCHAR(1000) NULL  último error, o por qué ya no se puede enviar
          intentosSincronizacionResultado  INT NOT NULL DEFAULT 0 (CHECK >= 0)
        NULL en sincronizacionResultadoNexo = sin resultado, o resultado anterior al 13 (el backend lo muestra como 'sincronizado' si la
        cotización está en Nexo, o 'solo_cotifly' si no).

   Dónde corre: después del 12 (o del 01). No depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 / .sh) y levantar-local lo
   toman solos, en orden de nombre. El backend SÍ mapea las columnas: sin él /health/ready responde 503 "esquema incompleto: falta 13",
   así que se aplica ANTES de desplegar la imagen.

   Respaldo: no hace falta. Las columnas nuevas son NULL (o NOT NULL con DEFAULT 0, que llena las filas existentes con 0) y los CHECK
   admiten todos los valores que hoy escribe el backend.

   Permisos: ninguno nuevo (cotifly_app ya tiene SELECT/INSERT/UPDATE/DELETE sobre el esquema).

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 13_correo_mailsender.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
-- Índices filtrados: exigen QUOTED_IDENTIFIER y ANSI_NULLS en ON.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Correos', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene Correos o CotizacionesSolicitud: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columnas y CHECK (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 1.1 Correos
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'idMailSender') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD idMailSender UNIQUEIDENTIFIER NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'redirigidoA') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD redirigidoA NVARCHAR(320) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'intentosEntrega') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD intentosEntrega INT NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'enviadoAt') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD enviadoAt DATETIMEOFFSET NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'estadoActualizadoAt') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD estadoActualizadoAt DATETIMEOFFSET NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'siguienteConsultaAt') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD siguienteConsultaAt DATETIMEOFFSET NULL;
    IF COL_LENGTH(N'dbo_cotifly.Correos', N'encoladoAt') IS NULL
        ALTER TABLE dbo_cotifly.Correos ADD encoladoAt DATETIMEOFFSET NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_correos_estado' AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Correos'))
        ALTER TABLE dbo_cotifly.Correos WITH CHECK ADD CONSTRAINT ck_correos_estado
            CHECK (estado IS NULL OR estado IN ('encolado', 'enviando', 'enviado', 'fallido'));

    -- 1.2 CotizacionesSolicitud
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'sincronizacionResultadoNexo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud
            ADD sincronizacionResultadoNexo NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'comentarioResultadoPendiente') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud
            ADD comentarioResultadoPendiente NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'resultadoSincronizadoAt') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD resultadoSincronizadoAt DATETIMEOFFSET NULL;
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'errorSincronizacionResultado') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud
            ADD errorSincronizacionResultado NVARCHAR(1000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'intentosSincronizacionResultado') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud
            ADD intentosSincronizacionResultado INT NOT NULL
                CONSTRAINT df_cotizaciones_intentosSincronizacionResultado DEFAULT (0);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'13 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- Los CHECK de las columnas nuevas van en otro lote: dentro del mismo lote del ADD, SQL Server todavía no las conoce al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_sincronizacionResultadoNexo'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT ck_cotizaciones_sincronizacionResultadoNexo
                 CHECK (sincronizacionResultadoNexo IS NULL OR sincronizacionResultadoNexo IN (''pendiente'', ''sincronizado'', ''no_sincronizable''));');

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_intentosSincronizacionResultado'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT ck_cotizaciones_intentosSincronizacionResultado
                 CHECK (intentosSincronizacionResultado >= 0);');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'13 NO se aplicó completo (CHECK de CotizacionesSolicitud): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

-- =============================================================================
-- 2. Índices filtrados de Correos (fuera de la transacción: CREATE INDEX con filtro sobre columnas recién agregadas)
-- =============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_correos_siguienteConsultaAt' AND object_id = OBJECT_ID(N'dbo_cotifly.Correos'))
    EXEC (N'CREATE INDEX ix_correos_siguienteConsultaAt ON dbo_cotifly.Correos (siguienteConsultaAt) WHERE siguienteConsultaAt IS NOT NULL;');
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ux_correos_idMailSender' AND object_id = OBJECT_ID(N'dbo_cotifly.Correos'))
    EXEC (N'CREATE UNIQUE INDEX ux_correos_idMailSender ON dbo_cotifly.Correos (idMailSender) WHERE idMailSender IS NOT NULL;');
GO

PRINT N'13 aplicado: seguimiento de MailSender en Correos y resultado diferido a Nexo en CotizacionesSolicitud.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 11 columnas, 3 CHECK y 2 índices):

     SELECT OBJECT_NAME(c.object_id) AS tabla, c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.is_nullable
     FROM sys.columns c
     WHERE (c.object_id = OBJECT_ID(N'dbo_cotifly.Correos')
            AND c.name IN ('idMailSender', 'redirigidoA', 'intentosEntrega', 'enviadoAt', 'estadoActualizadoAt', 'siguienteConsultaAt'))
        OR (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud')
            AND c.name IN ('sincronizacionResultadoNexo', 'comentarioResultadoPendiente', 'resultadoSincronizadoAt',
                           'errorSincronizacionResultado', 'intentosSincronizacionResultado'));
     SELECT name FROM sys.check_constraints
     WHERE name IN ('ck_correos_estado', 'ck_cotizaciones_sincronizacionResultadoNexo', 'ck_cotizaciones_intentosSincronizacionResultado');
     SELECT name FROM sys.indexes WHERE name IN ('ix_correos_siguienteConsultaAt', 'ux_correos_idMailSender');

   Reversa (solo antes de desplegar un backend que las mapee; con él, /health/ready vuelve a 503). Falla si ya hay correos 'encolado'
   o 'enviando' y el CHECK se recrea; se pueden pasar a 'fallido' antes.
     DROP INDEX ux_correos_idMailSender ON dbo_cotifly.Correos;
     DROP INDEX ix_correos_siguienteConsultaAt ON dbo_cotifly.Correos;
     ALTER TABLE dbo_cotifly.Correos DROP CONSTRAINT ck_correos_estado;
     ALTER TABLE dbo_cotifly.Correos DROP COLUMN idMailSender, redirigidoA, intentosEntrega, enviadoAt, estadoActualizadoAt, siguienteConsultaAt;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT ck_cotizaciones_sincronizacionResultadoNexo;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT ck_cotizaciones_intentosSincronizacionResultado;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT df_cotizaciones_intentosSincronizacionResultado;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP COLUMN sincronizacionResultadoNexo, comentarioResultadoPendiente,
         resultadoSincronizadoAt, errorSincronizacionResultado, intentosSincronizacionResultado;
   ============================================================================ */
