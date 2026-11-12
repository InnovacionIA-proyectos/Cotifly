/* ============================================================================
   Cotifly — 22: origen de la lectura del Excel de la licitación (origenLectura)
   ============================================================================
   HU-044.7 (#290). La licitación guarda cómo se leyó su Excel para saber cómo exportarla:
   con 'analizar_excel' (lo de siempre) los precios se escriben sobre el Excel original;
   con 'leer_estructura' (lectura con la IA de NEXO) se exporta en una hoja nueva, sin tocar
   el original.

   Qué hace (NO destructivo e idempotente: solo agrega una columna; no borra ni cambia valores):

   1. En CotizacionArchivosLicitacion (el Excel original del pliego):
          origenLectura  NVARCHAR(20)  NOT NULL  DEFAULT 'analizar_excel'
      CHECK ck_cotizacionArchivosLicitacion_origenLectura: origenLectura IN ('analizar_excel', 'leer_estructura').

   Las filas existentes quedan con 'analizar_excel' (el DEFAULT de la columna NOT NULL): ninguna
   licitación ya guardada cambia de comportamiento al exportarse.

   Dónde corre: después del 20 (o del 01); no depende de ningún otro ajuste. El ejecutador
   (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend mapea esta columna desde
   HU-044.7, así que se aplica antes de desplegar ese backend.

   Respaldo: no hace falta — columna nueva con DEFAULT; no se modifica ningún valor existente.

   Cómo ejecutarlo:
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
-- 1. Columna origenLectura
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionArchivosLicitacion', N'origenLectura') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion ADD origenLectura NVARCHAR(20)
            COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL
            CONSTRAINT df_cotizacionArchivosLicitacion_origenLectura DEFAULT (N'analizar_excel');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'22 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- El CHECK en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce la columna al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionArchivosLicitacion_origenLectura'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion'))
        ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion WITH CHECK ADD CONSTRAINT ck_cotizacionArchivosLicitacion_origenLectura
            CHECK (origenLectura IN (N'analizar_excel', N'leer_estructura'));

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
   Verificación sugerida tras ejecutar (una columna NOT NULL con DEFAULT, un CHECK, ninguna
   licitación con leer_estructura):

     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.max_length, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion') AND c.name = 'origenLectura';
     SELECT name, definition FROM sys.check_constraints WHERE name = 'ck_cotizacionArchivosLicitacion_origenLectura';
     SELECT COUNT(*) AS leidas_con_ia FROM dbo_cotifly.CotizacionArchivosLicitacion WHERE origenLectura = 'leer_estructura';  -- 0

   Reversa (solo antes de desplegar un backend que la mape):
     ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion DROP CONSTRAINT ck_cotizacionArchivosLicitacion_origenLectura;
     ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion DROP CONSTRAINT df_cotizacionArchivosLicitacion_origenLectura;
     ALTER TABLE dbo_cotifly.CotizacionArchivosLicitacion DROP COLUMN origenLectura;
   ============================================================================ */
