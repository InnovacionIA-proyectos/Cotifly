/* ============================================================================
   Cotifly — 14: cotización finalizada
   ============================================================================
   HU-036.1 (#205), habilita HU-036.2 (#206). Decisión de Sara del 2026-10-25: el comercial revisa y ajusta
   el margen de sus rutas y recién al "Finalizar" se habilitan el PDF, el correo, marcar ganada/perdida y
   "Redactar con IA". Después de finalizar, ningún cambio que mueva el precio se permite en sitio: toca una
   versión nueva.

   Qué hace (NO destructivo e idempotente: solo agrega; no cambia datos):
     CotizacionesSolicitud (la cabecera):
       finalizadaAt   DATETIMEOFFSET    NULL   cuándo se finalizó. NULL = no está finalizada.
       finalizadaPor  UNIQUEIDENTIFIER  NULL   quién la finalizó (FK a UsuariosWeb). NULL puede significar
                                                "no finalizada" o "finalizada por el sistema" (backfill,
                                                hoy no aplica: HU-036 no hace backfill).
     Van aparte de estadoCosteo a propósito: el polling deriva estadoCosteo de Nexo en cada vuelta y lo
     pisaría si "finalizada" se mezclara ahí. Son dos conceptos independientes (una cotización puede estar
     entregada y no finalizada, o finalizada y seguir recibiendo actualizaciones de estadoCosteo de Nexo,
     ver HU-036.4/#208: el precio sí queda congelado).

   Dónde corre: después del 12 (o del 01). No depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 /
   .sh) y levantar-local lo toman solos, en orden de nombre. El backend SÍ mapea las columnas: sin él
   /health/ready responde 503 "esquema incompleto: falta 14", así que se aplica ANTES de desplegar la imagen.

   Respaldo: no hace falta. Las columnas nuevas son NULL y no tocan filas existentes.

   Permisos: ninguno nuevo (cotifly_app ya tiene SELECT/INSERT/UPDATE/DELETE sobre el esquema).

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 14_cotizacion_finalizada.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionesSolicitud o UsuariosWeb: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columnas y FK (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaAt') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD finalizadaAt DATETIMEOFFSET NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaPor') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD finalizadaPor UNIQUEIDENTIFIER NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'fk_cotizaciones_finalizadaPor'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT fk_cotizaciones_finalizadaPor
            FOREIGN KEY (finalizadaPor) REFERENCES dbo_cotifly.UsuariosWeb (id);

    COMMIT TRANSACTION;
    PRINT N'14 aplicado: finalizadaAt y finalizadaPor listos en CotizacionesSolicitud.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'14 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 2 columnas, ambas NULL, y la FK):

     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name IN (N'finalizadaAt', N'finalizadaPor');
     SELECT name FROM sys.foreign_keys WHERE name = N'fk_cotizaciones_finalizadaPor';

   Reversa (solo antes de desplegar un backend que las mapee; con él, /health/ready vuelve a 503 "falta 14").
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT fk_cotizaciones_finalizadaPor;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP COLUMN finalizadaAt, finalizadaPor;
   ============================================================================ */
