/* ============================================================================
   Cotifly — 16: precio de venta en los conexos (PROPUESTA, pendiente de revisión de @scalderon-bs)
   ============================================================================
   HU-043.1 (#245). La consume HU-043.2. Origen: plan "Precio de venta de los servicios conexos"
   (2026-10-28, docs/backlog/2026-10-28_ISSUES-PRECIO-VENTA-CONEXOS.md en NEXO); relacionada con
   InnovacionIA-proyectos/NEXO#407.

   Qué hace (NO destructivo e idempotente: solo agrega y rellena; no borra nada), en
   CotizacionServiciosConexos:
       precioVenta  DECIMAL(18,2)  NOT NULL   precio de venta del conexo (lo que paga el cliente)
   `precio` (ya existía) sigue siendo el costo unitario, sin cambios de significado.

   Backfill: toda fila existente queda con precioVenta = precio (ninguna cotización existente
   cambia de valor; es el punto de partida hasta que algo distinto lo ajuste). Como `precio` ya
   tiene el CHECK `>= 0`, el backfill nunca puede violar el CHECK nuevo de precioVenta.

   El CHECK ck_cotizacionServiciosConexos_montos (precio >= 0 AND cantidad > 0) se reemplaza por
   uno que agrega "AND precioVenta >= 0" — mismo nombre, para no tener que tocar el backend si en
   algún momento referencia el nombre del constraint en un mensaje de error.

   Dónde corre: después del 15 (o del 01); no depende de ningún otro ajuste. El ejecutador
   (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend mapea la columna cuando
   HU-043.2 esté lista: hasta entonces no la lee, así que este script se puede aplicar antes del
   backend sin romper nada (igual queda documentado abajo si eso cambia).

   Respaldo: no hace falta — la columna nueva solo se llena con un valor que ya existía (`precio`)
   en la misma fila; no se pierde información.

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 16_precio_venta_conexos.sql
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
IF OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionServiciosConexos: aplique primero 01_crear_esquema.sql.', 1;
GO

-- =============================================================================
-- 1. Columna (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'precioVenta') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos ADD precioVenta DECIMAL(18, 2) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'16 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- Backfill y NOT NULL en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce
-- la columna al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos')
                 AND name = N'precioVenta' AND is_nullable = 1)
    BEGIN
        UPDATE dbo_cotifly.CotizacionServiciosConexos SET precioVenta = precio WHERE precioVenta IS NULL;

        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos ALTER COLUMN precioVenta DECIMAL(18, 2) NOT NULL;

        IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionServiciosConexos_montos'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos'))
            ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP CONSTRAINT ck_cotizacionServiciosConexos_montos;

        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos WITH CHECK ADD CONSTRAINT ck_cotizacionServiciosConexos_montos
            CHECK (precio >= 0 AND cantidad > 0 AND precioVenta >= 0);
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'16 NO se aplicó completo (backfill, NOT NULL y CHECK de precioVenta): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'16 aplicado: precioVenta en CotizacionServiciosConexos.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (precioVenta NOT NULL, sin filas NULL, CHECK con las 3 condiciones):

     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos') AND c.name = 'precioVenta';
     SELECT COUNT(*) AS filas_sin_precioVenta FROM dbo_cotifly.CotizacionServiciosConexos WHERE precioVenta IS NULL;  -- 0
     SELECT definition FROM sys.check_constraints WHERE name = 'ck_cotizacionServiciosConexos_montos';

   Reversa (solo antes de desplegar un backend que la mapee; con él, /health/ready respondería 503 si se exige):
     ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP CONSTRAINT ck_cotizacionServiciosConexos_montos;
     ALTER TABLE dbo_cotifly.CotizacionServiciosConexos WITH CHECK ADD CONSTRAINT ck_cotizacionServiciosConexos_montos
         CHECK (precio >= 0 AND cantidad > 0);
     ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP COLUMN precioVenta;
   ============================================================================ */
