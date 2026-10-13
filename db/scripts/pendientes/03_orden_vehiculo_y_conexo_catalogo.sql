/* ============================================================================
   Cotifly — 03 (PROPUESTA): orden del camión e id del catálogo del conexo
   ============================================================================
   *** PENDIENTE DE REVISIÓN DE @scalderon-bs. NO APLICAR. ***
   Está en db/scripts/pendientes/ a propósito: el ejecutador (ejecutar-bd.ps1 / .sh) solo corre
   los .sql de db/scripts (sin subcarpetas) y no entra a esta carpeta. El backend NO mapea estas columnas todavía.

   HU-023.3 (#158). Dos deudas que el 02 no cubre (ADR-019, "Pendientes"; ADR-020) y una corrección al CHECK del 02:
     1. CotizacionVehiculosLineaSolicitud.ordenVehiculo: el orden del camión en su ruta (la fila de la
        recomendación del cubicador; el primero lleva los conexos en Nexo). Hoy va en un desfase de
        1 ms de createdAt (clsVehiculoLineaSolicitud.OrdenCreacion + interceptor). Se llena con el orden
        actual (createdAt, id) de cada ruta.
     2. CotizacionServiciosConexos.idServicioConexoCatalogo: el id del CATÁLOGO de Nexo del conexo. Hoy
        idServicioConexo mezcla el id del catálogo (capturados, #132) y el de la fila de la cotización de
        Nexo (agregados o importados, #124/#135) y se resuelve por nombre (clsResolutorConexosCatalogo, #136).
        Desde SQL no se puede saber cuál idServicioConexo es del catálogo: queda NULL y el backend lo
        completaría al resolverlo.
     3. ck_cotizacionLineasSolicitud_motivoSinSeleccion (del 02, que no se toca): acepta un motivo con
        resultadoCubicacion NULL, porque `NULL = 'SIN_SELECCION_AUTOMATICA'` es UNKNOWN y un CHECK UNKNOWN pasa.
        Se recrea exigiendo resultadoCubicacion = 'SIN_SELECCION_AUTOMATICA' (con ISNULL) cuando hay motivo.

   OJO: mientras el backend no mapee ordenVehiculo ni idServicioConexoCatalogo, las filas NUEVAS quedan en
   NULL (el backend sigue usando el desfase de createdAt y la resolución por nombre). Por eso el índice único
   de (lineaSolicitudId, ordenVehiculo) es FILTRADO (WHERE ordenVehiculo IS NOT NULL): sin el filtro, el
   segundo camión nuevo de una ruta chocaría con el primero (los dos NULL).

   Idempotente (cada cambio revisa si ya está), en una transacción, con CHECK con nombre.
   Va después del 02.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema nuevo: corra primero 01_crear_esquema.sql y 02_alter_cubicador.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 1. Orden del camión en su ruta -------------------------------------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'ordenVehiculo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud ADD ordenVehiculo INT NULL;

    EXEC (N'
        WITH ordenados AS (
            SELECT v.ordenVehiculo,
                   ROW_NUMBER() OVER (PARTITION BY v.lineaSolicitudId ORDER BY v.createdAt, v.id) AS n
            FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v
        )
        UPDATE ordenados SET ordenVehiculo = n WHERE ordenVehiculo IS NULL;');

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_ordenVehiculo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_ordenVehiculo CHECK (ordenVehiculo IS NULL OR ordenVehiculo >= 1);');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo'
                   AND object_id = OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud'))
        EXEC (N'CREATE UNIQUE INDEX uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo
            ON dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, ordenVehiculo) WHERE ordenVehiculo IS NOT NULL;');

    -- 2. Id del catálogo de Nexo del conexo ------------------------------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'idServicioConexoCatalogo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos
            ADD idServicioConexoCatalogo NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;  -- id del catálogo de Nexo

    -- 3. CHECK del motivo de "sin selección" (corrige el del 02) ---------------------------
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'
               AND definition NOT LIKE N'%isnull%')
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_motivoSinSeleccion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
                motivoSinSeleccion IS NULL
                OR (motivoSinSeleccion IN (''EXTRADIMENSIONADO'', ''SIN_CANDIDATOS_DIMENSIONALES'', ''SUPERA_K_MAX'', ''SIN_CARROCERIA_COMPATIBLE'')
                    AND ISNULL(resultadoCubicacion, '''') = ''SIN_SELECCION_AUTOMATICA''));');

    COMMIT TRANSACTION;
    PRINT N'03 aplicado: ordenVehiculo, su índice, idServicioConexoCatalogo y el CHECK del motivo listos.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'03 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar:

     -- Columnas nuevas (2 filas)
     SELECT t.name AS tabla, c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.is_nullable
     FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id AND SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
     WHERE c.name IN ('ordenVehiculo', 'idServicioConexoCatalogo');

     -- Índice filtrado y CHECK corregido
     SELECT name, has_filter, filter_definition FROM sys.indexes
     WHERE name = 'uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo';
     SELECT name, definition FROM sys.check_constraints WHERE name = 'ck_cotizacionLineasSolicitud_motivoSinSeleccion';

     -- Ningún camión existente quedó sin orden ni repetido en su ruta (0 filas)
     SELECT lineaSolicitudId, ordenVehiculo, COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud
     GROUP BY lineaSolicitudId, ordenVehiculo HAVING COUNT(*) > 1 OR ordenVehiculo IS NULL;
   ============================================================================ */
