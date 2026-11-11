/* ============================================================================
   Cotifly — 22: origen de la lectura del Excel de una licitación (analizar-excel o leer-estructura de NEXO)
   ============================================================================
   HU-044.7 (#290). Parte de HU-044 (importación inteligente del Excel de licitación con NEXO). Lo consume el backend en el
   mismo issue (crear y exportar la licitación). Dueña: @scalderon-bs (Datos).
   Origen: decisión de producto del 2026-11-09 (una licitación leída con la IA de NEXO se exporta en un Excel nuevo, sin
   escribir sobre el original del comercial).

   Qué hace (NO destructivo e idempotente: solo agrega; no borra ni cambia valores):

   1. En CotizacionArchivosLicitacion (el Excel original de la licitación):
          origenLectura   VARCHAR(20)   NULL   cómo se leyó el Excel:
                                                 'analizar_excel'  = analizar-excel (cada fila es una fila de la hoja);
                                                 'leer_estructura' = leer-estructura de NEXO (cada fila es el índice dentro
                                                                     de una tabla que NEXO reorganizó: ya no es una fila
                                                                     del archivo).
      NULL es 'analizar_excel': las licitaciones existentes quedan en NULL y se siguen exportando como hoy, con los precios
      sobre el Excel original. Con 'leer_estructura' la exportación arma la hoja "Licitación" y no toca el original.
      CHECK ck_cotizacionArchivosLicitacion_origenLectura: NULL, 'analizar_excel' o 'leer_estructura'.

   Las filas existentes no cambian (columna nueva NULL, sin DEFAULT): ninguna licitación cambia cómo se exporta.

   Dónde corre: después del 21 (o del 01); no depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 / .sh) lo toma
   solo, en orden de nombre. El backend de HU-044.7 mapea la columna: aplíquelo ANTES de desplegar ese backend (sin él,
   /health/ready dice "falta 22" y crear o exportar una licitación falla).

   Respaldo: no hace falta — una columna nueva NULL y un CHECK; no se modifica ningún valor existente.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON, como el resto de los scripts).
   Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 22_origen_lectura_licitacion.sql
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
IF OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionArchivosLicitacion: aplique primero 01_crear_esquema.sql.', 1;
GO

-- =============================================================================
-- 1. Columna origenLectura (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionArchivosLicitacion', N'origenLectura') IS NULL
    BEGIN
        ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion ADD origenLectura VARCHAR(20) NULL;
        PRINT N'22: columna dbo_cotifly.CotizacionArchivosLicitacion.origenLectura creada.';
    END
    ELSE
        PRINT N'22: dbo_cotifly.CotizacionArchivosLicitacion.origenLectura ya existe, no se toca.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'22 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- CHECK en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce la columna al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionArchivosLicitacion_origenLectura'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion'))
        ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion WITH CHECK ADD CONSTRAINT ck_cotizacionArchivosLicitacion_origenLectura
            CHECK (origenLectura IS NULL OR origenLectura IN ('analizar_excel', 'leer_estructura'));

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'22 NO se aplicó completo (CHECK de origenLectura): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'22 aplicado: origenLectura en CotizacionArchivosLicitacion.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (una columna VARCHAR(20) NULL, un CHECK, ninguna licitación con origen):

     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.max_length, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion') AND c.name = 'origenLectura';
     SELECT name, definition FROM sys.check_constraints WHERE name = 'ck_cotizacionArchivosLicitacion_origenLectura';
     SELECT COUNT(*) AS licitaciones_con_origen FROM dbo_cotifly.CotizacionArchivosLicitacion WHERE origenLectura IS NOT NULL;  -- 0

   Reversa (solo antes de desplegar un backend que la mapee):
     ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion DROP CONSTRAINT ck_cotizacionArchivosLicitacion_origenLectura;
     ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion DROP COLUMN origenLectura;
   ============================================================================ */
