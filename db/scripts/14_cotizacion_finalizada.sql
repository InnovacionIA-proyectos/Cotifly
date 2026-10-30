/* ============================================================================
   Cotifly — 14: cotización finalizada (PROPUESTA, pendiente de revisión de @scalderon-bs)
   ============================================================================
   HU-036 (#205, #206). El comercial "finaliza" la cotización cuando acepta el precio (POST /web/cotizaciones/{folio}/finalizar): desde
   ahí el precio queda congelado (margen, conexos, re-solicitud y edición piden una versión nueva) y recién entonces se generan el PDF,
   los textos, el correo y el resultado.

   Columnas APARTE de estadoCosteo a propósito: el polling deriva estadoCosteo de Nexo en cada vuelta y lo pisaría si la finalización
   viviera ahí (la cotización finalizada sigue avanzando, p. ej. de autocosteo a entregada).

   Qué hace (NO destructivo e idempotente: solo agrega; no cambia datos), en CotizacionesSolicitud (la cabecera):
       finalizadaAt   DATETIMEOFFSET    NULL   cuándo se finalizó (NULL = sin finalizar)
       finalizadaPor  UNIQUEIDENTIFIER  NULL   quién la finalizó: FK fk_cotizaciones_finalizadaPor a UsuariosWeb(id)
     y el CHECK ck_cotizaciones_finalizada: las dos NULL o las dos con valor.

   Tipo de finalizadaPor: UNIQUEIDENTIFIER, el de la PK de UsuariosWeb (id UNIQUEIDENTIFIER, pk_usuariosWeb, db/scripts/01) y el mismo de
   usuarioId en esta tabla. Verificado contra 01_crear_esquema.sql y la base de pruebas; si en un ambiente la PK fuera de otro tipo el
   script se detiene en la precondición sin tocar nada.

   Sin backfill (decisión del 2026-10-26): las cotizaciones existentes quedan sin finalizar. Las que ya se enviaron al cliente o tienen
   resultado siguen protegidas como hasta hoy (EntregadaAlCliente); las que están en autocosteo/entregada sin enviar las finaliza el
   comercial una por una (consulta del conteo en DEPLOY.md §4.5).

   Dónde corre: después del 13 (o del 01). No depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 / .sh) y levantar-local lo
   toman solos, en orden de nombre. El backend SÍ mapea las columnas: sin él /health/ready responde 503 "esquema incompleto: falta 14",
   así que se aplica ANTES de desplegar la imagen.

   Respaldo: no hace falta. Las columnas nuevas son NULL (no tocan filas) y el CHECK lo cumplen todas las filas existentes (NULL, NULL).

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

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosWeb') AND name = N'id' AND TYPE_NAME(user_type_id) = N'uniqueidentifier')
    THROW 50002, N'UsuariosWeb.id no es UNIQUEIDENTIFIER: finalizadaPor no puede apuntarle. Revise el esquema antes de aplicar el 14.', 1;
GO

-- =============================================================================
-- 1. Columnas (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaAt') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD finalizadaAt DATETIMEOFFSET NULL;
    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaPor') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD finalizadaPor UNIQUEIDENTIFIER NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'14 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- La FK y el CHECK van en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce las columnas al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'fk_cotizaciones_finalizadaPor'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT fk_cotizaciones_finalizadaPor
                 FOREIGN KEY (finalizadaPor) REFERENCES dbo_cotifly.UsuariosWeb (id);');

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_finalizada'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT ck_cotizaciones_finalizada
                 CHECK ((finalizadaAt IS NULL AND finalizadaPor IS NULL) OR (finalizadaAt IS NOT NULL AND finalizadaPor IS NOT NULL));');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'14 NO se aplicó completo (FK y CHECK de la finalización): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'14 aplicado: finalizadaAt y finalizadaPor en CotizacionesSolicitud.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 2 columnas, 1 FK y 1 CHECK):

     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name IN ('finalizadaAt', 'finalizadaPor');
     SELECT name FROM sys.foreign_keys WHERE name = 'fk_cotizaciones_finalizadaPor';
     SELECT name FROM sys.check_constraints WHERE name = 'ck_cotizaciones_finalizada';

   Reversa (solo antes de desplegar un backend que las mapee; con él, /health/ready vuelve a 503). Pierde qué cotizaciones estaban
   finalizadas: sus candados vuelven a depender solo del envío al cliente.
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT ck_cotizaciones_finalizada;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT fk_cotizaciones_finalizadaPor;
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP COLUMN finalizadaAt, finalizadaPor;
   ============================================================================ */
