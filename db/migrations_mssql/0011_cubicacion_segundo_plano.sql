/* ============================================================================
   Cotifly — 0011: cubicación de la licitación en segundo plano
   ============================================================================
   HU-023.5 (#160), ADR-020. Una licitación son muchas rutas y cada una es una llamada al cubicador
   de hasta 10 s: Cotifly ya no las cubica mientras el comercial espera. La licitación se guarda con
   un camión provisional por ruta y un proceso en segundo plano le pide los camiones al cubicador,
   ruta por ruta, con un límite de llamadas en paralelo (Cubicador:MaximoParalelo, petición P6).
   La solicitud viaja a Nexo cuando TODAS las rutas quedaron cubicadas.
   Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First).

   *** PENDIENTE DE APROBACIÓN DEL DBA (y de revisión de @scalderon-bs).                ***
   *** No ejecutar en tbsdatacenter (ni en ningún ambiente real) sin esa aprobación.     ***

   Qué hace (todo en dbo_cotifly.CotizacionLineasSolicitud, la ruta; NO destructivo e idempotente:
   solo agrega lo que falta y no toca datos):
     1. estadoCubicacion NVARCHAR(20) NULL, CHECK pendiente / cubicada / fallida. NULL = la ruta se
        cubicó al guardarla (formulario o rutas anteriores): nada pendiente.
     2. cubicacionIntentos INT NOT NULL DEFAULT 0 (CHECK >= 0): intentos fallidos.
     3. cubicacionDisponibleAt DATETIMEOFFSET NULL: desde cuándo una réplica puede tomar la ruta
        (reclamo entre réplicas y espera entre reintentos).
     4. errorCubicacion NVARCHAR(500) NULL: el último error, para mostrárselo al comercial.
     5. avisosCubicacion NVARCHAR(MAX) NULL, CHECK ISJSON: las advertencias, la extradimensión y las
        mercancías condicionadas del cubicador (antes solo viajaban en la respuesta; con el segundo
        plano el comercial las lee después).
     6. Índice filtrado ix_cotizacionLineasSolicitud_cubicacionPendiente (cubicacionDisponibleAt)
        WHERE estadoCubicacion = N'pendiente': la cola que lee el proceso.

   Dónde corre: después del 0010 y del db/scripts/02_alter_cubicador.sql (no depende de sus columnas),
   o sobre una base instalada con db/scripts/01_crear_esquema.sql anterior a este cambio (el 01 ya
   trae estas columnas). Todo corre en UNA transacción: si algo falla, no queda nada aplicado. Si ya
   se aplicó, no cambia nada.

   Cómo ejecutarlo (-b detiene el script en el primer error, -I activa QUOTED_IDENTIFIER, que exige el
   índice filtrado):
     sqlcmd -S <servidor> -d dbTarifario_Nexo_Pruebas -E -C -I -b -f 65001 -i 0011_cubicacion_segundo_plano.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

-- =============================================================================
-- 0. Precondiciones
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionLineasSolicitud: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1 a 6. Cambios (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'estadoCubicacion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD estadoCubicacion NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'cubicacionIntentos') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD cubicacionIntentos INT NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_cubicacionIntentos DEFAULT (0);

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'cubicacionDisponibleAt') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD cubicacionDisponibleAt DATETIMEOFFSET NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'errorCubicacion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD errorCubicacion NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'avisosCubicacion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD avisosCubicacion NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    -- Los CHECK y el índice van con SQL dinámico: las columnas pueden haberse creado en este mismo lote.
    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_estadoCubicacion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_estadoCubicacion CHECK (
                estadoCubicacion IS NULL OR estadoCubicacion IN (''pendiente'', ''cubicada'', ''fallida''));');

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_cubicacionIntentos', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_cubicacionIntentos CHECK (cubicacionIntentos >= 0);');

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_avisosCubicacion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_avisosCubicacion CHECK (avisosCubicacion IS NULL OR ISJSON(avisosCubicacion) = 1);');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = N'ix_cotizacionLineasSolicitud_cubicacionPendiente'
                     AND object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'))
        EXEC (N'CREATE INDEX ix_cotizacionLineasSolicitud_cubicacionPendiente
            ON dbo_cotifly.CotizacionLineasSolicitud (cubicacionDisponibleAt)
            WHERE estadoCubicacion = N''pendiente'';');

    COMMIT TRANSACTION;
    PRINT N'0011 aplicado: cola de la cubicación en segundo plano lista.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'0011 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 5 filas):

     SELECT c.name AS columna, ty.name AS tipo, c.is_nullable
     FROM sys.columns c
     JOIN sys.types ty ON ty.user_type_id = c.user_type_id
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud')
       AND c.name IN ('estadoCubicacion', 'cubicacionIntentos', 'cubicacionDisponibleAt', 'errorCubicacion', 'avisosCubicacion');
   ============================================================================ */
