/* ============================================================================
   Cotifly — 04 (PROPUESTA): cubicación de la licitación en segundo plano
   ============================================================================
   *** PENDIENTE DE REVISIÓN DE @scalderon-bs Y DE APROBACIÓN DEL DBA. NO APLICAR SIN ESA APROBACIÓN. ***
   *** No ejecutar en tbsdatacenter (ni en ningún ambiente real) sin esa aprobación.                    ***
   Está en db/scripts/pendientes/ a propósito: el ejecutador (ejecutar-bd.ps1 / .sh) no entra a esta
   carpeta. A diferencia del 03, el backend SÍ mapea estas columnas (HU-023.5, #160): sin este script
   /health/ready responde "esquema incompleto: falta 04" y el PR de la #160 NO se fusiona hasta que se
   apliquen el 0010 y este 04. Al aprobarlo, @scalderon-bs lo mueve a db/scripts/ (o lo consolida en el 01).

   HU-023.5 (#160), ADR-020. Una licitación son muchas rutas y cada una es una llamada al cubicador de hasta
   10 s: Cotifly ya no las cubica mientras el comercial espera. La licitación se guarda con un camión
   provisional por ruta y un proceso en segundo plano le pide los camiones al cubicador, ruta por ruta, con
   un límite de llamadas en paralelo (Cubicador:MaximoParalelo, petición P6). La solicitud viaja a Nexo
   cuando TODAS las rutas quedaron cubicadas.

   Qué hace (todo en dbo_cotifly.CotizacionLineasSolicitud, la ruta; NO destructivo e idempotente: solo
   agrega lo que falta y no cambia datos):
     0. Respaldo previo: copia la tabla en dbo_cotifly.Respaldo04_CotizacionLineasSolicitud (solo la primera
        vez, si las columnas todavía no existen, el respaldo no existe y hay rutas: una base vacía no lo necesita).
     1. estadoCubicacion NVARCHAR(20) NULL, CHECK pendiente / cubicada / fallida. NULL = la ruta se cubicó al
        guardarla (formulario o rutas anteriores): nada pendiente.
     2. cubicacionIntentos INT NOT NULL DEFAULT 0 (CHECK >= 0): intentos fallidos.
     3. cubicacionDisponibleAt DATETIMEOFFSET NULL: desde cuándo una réplica puede tomar la ruta (reclamo
        entre réplicas y espera entre reintentos).
     4. errorCubicacion NVARCHAR(500) NULL: el último error, para mostrárselo al comercial.
     5. avisosCubicacion NVARCHAR(MAX) NULL, CHECK ISJSON: advertencias, extradimensión y mercancías
        condicionadas del cubicador (con el segundo plano el comercial las lee después).
     6. Índice filtrado ix_cotizacionLineasSolicitud_cubicacionPendiente (cubicacionDisponibleAt, createdAt)
        WHERE estadoCubicacion = N'pendiente': la cola que lee el proceso, en orden de llegada.

   Dónde corre: después del 0010 (o del 01) y del 02; no depende de las columnas del 02. Todo corre en UNA
   transacción: si algo falla, no queda nada aplicado. Si ya se aplicó, no cambia nada.

   Cómo ejecutarlo (-b detiene el script en el primer error, -I activa QUOTED_IDENTIFIER, que exige el
   índice filtrado). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 04_cubicacion_segundo_plano.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondiciones
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionLineasSolicitud: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 0 a 6. Cambios (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 0. Respaldo previo (solo la primera vez y con datos).
    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'estadoCubicacion') IS NULL
       AND OBJECT_ID(N'dbo_cotifly.Respaldo04_CotizacionLineasSolicitud', N'U') IS NULL
       AND EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud)
        EXEC (N'SELECT * INTO dbo_cotifly.Respaldo04_CotizacionLineasSolicitud FROM dbo_cotifly.CotizacionLineasSolicitud;');

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
            ON dbo_cotifly.CotizacionLineasSolicitud (cubicacionDisponibleAt, createdAt)
            WHERE estadoCubicacion = N''pendiente'';');

    COMMIT TRANSACTION;
    PRINT N'04 aplicado: cola de la cubicación en segundo plano lista.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'04 NO se aplicó: se deshizo todo. Error:';
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

   Reversa (si @scalderon-bs la pide): quitar el índice, los 3 CHECK, el DEFAULT y las 5 columnas; el respaldo
   Respaldo04_CotizacionLineasSolicitud se puede borrar cuando se confirme.
   ============================================================================ */
