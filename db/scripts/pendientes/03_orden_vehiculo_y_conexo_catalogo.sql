/* ============================================================================
   Cotifly — 03 (PROPUESTA): orden del camión e id del catálogo del conexo
   ============================================================================
   *** PENDIENTE DE REVISIÓN DE @scalderon-bs. NO APLICAR. ***
   Está en db/scripts/pendientes/ a propósito: el ejecutador (ejecutar-bd.ps1 / .sh) solo corre
   db/scripts/*.sql y no entra a esta carpeta. El backend NO mapea estas columnas todavía.

   HU-023.3 (#158). Dos deudas que el 02 no cubre (ADR-019, "Pendientes"; ADR-020):
     1. CotizacionVehiculosLineaSolicitud.ordenVehiculo: el orden del camión en su ruta (la fila de la
        recomendación del cubicador; el primero lleva los conexos en Nexo). Hoy va en un desfase de
        1 ms de createdAt (clsVehiculoLineaSolicitud.OrdenCreacion + interceptor). Se llena con el orden
        actual (createdAt, id) de cada ruta.
     2. CotizacionServiciosConexos.idServicioConexoCatalogo: el id del CATÁLOGO de Nexo del conexo. Hoy
        idServicioConexo mezcla el id del catálogo (capturados, #132) y el de la fila de la cotización de
        Nexo (agregados o importados, #124/#135) y se resuelve por nombre (clsResolutorConexosCatalogo, #136).
        Desde SQL no se puede saber cuál idServicioConexo es del catálogo: queda NULL y el backend lo
        completaría al resolverlo.

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

    -- 2. Id del catálogo de Nexo del conexo ------------------------------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'idServicioConexoCatalogo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos
            ADD idServicioConexoCatalogo NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;  -- id del catálogo de Nexo

    COMMIT TRANSACTION;
    PRINT N'03 aplicado: ordenVehiculo e idServicioConexoCatalogo listos.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'03 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO
