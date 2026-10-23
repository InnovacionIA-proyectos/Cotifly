/* ============================================================================
   Cotifly — 03: orden del camión e id del catálogo del conexo
   ============================================================================
   HU-023.3 (#158); el backend mapea sus columnas desde HU-014 (#188). Antes estaba en db/scripts/pendientes/ con "NO APLICAR".
   Desde #188 está en db/scripts/ y lo corren el ejecutador (ejecutar-bd.ps1 / .sh), db-init y levantar-local.*, por orden de nombre
   (01, 02, 03, 05, 07, 08 y 09; levantar-local intercala el pendientes/04 entre el 03 y el 05).

   *** OBLIGATORIO ANTES DE DESPLEGAR LA IMAGEN DE #188 ***
   El backend mapea ordenVehiculo e idServicioConexoCatalogo. Sin este script, /health/ready responde 503 "esquema incompleto:
   falta 03" y Traefik no manda tráfico a la versión nueva (igual que con el 04 y el 09). Verificación de solo lectura (las dos
   columnas deben dar un largo, no NULL):
     SELECT COL_LENGTH('dbo_cotifly.CotizacionVehiculosLineaSolicitud', 'ordenVehiculo') AS ordenVehiculo,
            COL_LENGTH('dbo_cotifly.CotizacionServiciosConexos', 'idServicioConexoCatalogo') AS idServicioConexoCatalogo;

   Numeración (decisión de #188): conserva el número 03. Es el nombre con que ya lo conocen el DBA y @scalderon-bs, por nombre se
   aplica antes del 07 y deja EXACTAMENTE el mismo CHECK del motivo que el 07 (en cualquier orden terminan iguales; ver el punto 3).
   Renumerarlo (por ejemplo, a 10) no cambiaría el resultado y confundiría a quien ya lo aplicó como 03.

   Qué hace: dos deudas que el 02 no cubre (ADR-019, "Pendientes"; ADR-020) y una corrección al CHECK del 02.
     1. CotizacionVehiculosLineaSolicitud.ordenVehiculo: el orden del camión en su ruta (la fila de la recomendación del
        cubicador; el primero lleva los conexos en Nexo). Reemplaza el desfase de 1 ms de createdAt que usaba el backend antes de
        #188. Se llena con el orden actual (createdAt, id) de cada ruta. El backend numera 1..n al crear los camiones (captura,
        recálculo, re-solicitud, -vN y licitación) y ordena por ordenVehiculo; si es NULL, por createdAt.
     2. CotizacionServiciosConexos.idServicioConexoCatalogo: el id del CATÁLOGO de Nexo del conexo. idServicioConexo mezcla el id
        del catálogo (capturados, #132) y el de la fila de la cotización de Nexo (agregados o importados, #124/#135). Desde #188 el
        backend lo guarda al capturar, al importar y al copiar a una -vN, y con él ya no resuelve por nombre. Desde SQL no se puede
        saber qué idServicioConexo es del catálogo: las filas anteriores quedan en NULL y el backend las sigue resolviendo por id o
        por nombre (clsResolutorConexosCatalogo).
     3. ck_cotizacionLineasSolicitud_motivoSinSeleccion (del 02, que no se toca): acepta un motivo con
        resultadoCubicacion NULL, porque `NULL = 'SIN_SELECCION_AUTOMATICA'` es UNKNOWN y un CHECK UNKNOWN pasa.
        Se recrea exigiendo resultadoCubicacion = 'SIN_SELECCION_AUTOMATICA' (con ISNULL) cuando hay motivo.
        HU-023 (#176): el CHECK que deja el 03 es EXACTAMENTE el de db/scripts/07_motivo_busqueda_agotada.sql (los 5
        motivos, con BUSQUEDA_AGOTADA, e ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA'). El 03 solo lo
        reemplaza si a la definición actual le falta BUSQUEDA_AGOTADA o le falta el ISNULL: después del 07 no lo toca, y
        si alguien aplicó una versión sin ISNULL la corrige. Órdenes 03 -> 07, 07 -> 03 y "sin ISNULL" -> 03 / 07
        probados en clsMotivoBusquedaAgotadaTests: todos terminan en el mismo CHECK y son idempotentes.

   El índice único de (lineaSolicitudId, ordenVehiculo) es FILTRADO (WHERE ordenVehiculo IS NOT NULL). Si el 03 se aplica antes
   de desplegar la imagen de #188, la imagen anterior escribe las filas nuevas con ordenVehiculo NULL y no chocan entre sí.

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
    --    El mismo CHECK del 07 (5 motivos + ISNULL). Solo se reemplaza si a la definición actual le falta BUSQUEDA_AGOTADA o el
    --    ISNULL: el del 07 se conserva tal cual y una versión sin ISNULL (o la del 02, de 4 motivos) se corrige.
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud')
               AND (definition NOT LIKE N'%BUSQUEDA[_]AGOTADA%' OR definition NOT LIKE N'%isnull%'))
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_motivoSinSeleccion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
                motivoSinSeleccion IS NULL
                OR (motivoSinSeleccion IN (''EXTRADIMENSIONADO'', ''SIN_CANDIDATOS_DIMENSIONALES'', ''SUPERA_K_MAX'',
                                           ''SIN_CARROCERIA_COMPATIBLE'', ''BUSQUEDA_AGOTADA'')
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
