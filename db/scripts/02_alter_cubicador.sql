/* ============================================================================
   Cotifly — 02: columnas para guardar la respuesta del cubicador
   ============================================================================
   HU-023.3 (#158), parte A. Contrato del cubicador: hoja de ruta del 2026-10-08, sección 2.

   Qué hace (todo en dbo_cotifly):
     1. CotizacionMercancias: `idTipoMercancia` (uuid) se reemplaza por `codigoMercancia`
        (el código estable que recibe el cubicador, por ejemplo BEBIDAS). Queda NULL en las
        filas viejas; el backend lo exige en las nuevas.
     2. CotizacionVehiculosLineaSolicitud (el camión): `volumenTotalM3`,
        `porcentajeLlenadoVolumen` y `porcentajeLlenadoPeso` (0 a 100).
     3. CotizacionLineasSolicitud (la ruta): `idRecomendacion` (la corrida del cubicador),
        `resultadoCubicacion` (ASIGNADO o SIN_SELECCION_AUTOMATICA), `motivoSinSeleccion`
        (los 4 motivos del contrato) y `condicionada` (sí/no, por defecto no).

   No crea, a propósito: el nombre del vehículo (se consulta por código en el catálogo del
   cubicador), la alerta de bajo llenado (el cubicador no recomienda camiones con bajo llenado)
   ni una marca de condicionada por mercancía (basta la de la ruta). No cambian `tipoVehiculo`
   ni `carroceria` del camión (ahora guardan los códigos del cubicador) ni `pesoKg` (recibe
   `pesoTotalKg`).

   Dónde corre: en una base con el esquema nuevo (`01_crear_esquema.sql` o el `0009`). Va
   después del 01; el ejecutador (`ejecutar-bd.ps1` / `.sh`) lo corre solo. Por el momento es
   un script aparte; más adelante se consolida en `01_crear_esquema.sql`.

   Se puede correr varias veces: cada cambio revisa si ya está. Todo va en UNA transacción:
   si algo falla, no queda nada aplicado.

   Seguridad de datos: si `idTipoMercancia` todavía existe y tiene algún valor, el script se
   detiene sin tocar nada (un uuid no se puede convertir a código). Hay que revisar esas filas
   antes.

   Cómo ejecutarlo a mano:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 02_alter_cubicador.sql
   (-E = autenticación de Windows, -C = confiar en el certificado, -I = QUOTED_IDENTIFIER ON,
   -b = detenerse en el primer error, -f 65001 = leer el archivo como UTF-8).
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
IF OBJECT_ID(N'dbo_cotifly.CotizacionMercancias', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema nuevo (CotizacionMercancias, CotizacionVehiculosLineaSolicitud, CotizacionLineasSolicitud): corra primero 01_crear_esquema.sql.', 1;

-- Se pregunta con SQL dinámico porque la columna puede ya no existir.
IF COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'idTipoMercancia') IS NOT NULL
BEGIN
    DECLARE @conDatos INT;
    EXEC sys.sp_executesql
        N'SELECT @n = COUNT(*) FROM dbo_cotifly.CotizacionMercancias WHERE idTipoMercancia IS NOT NULL;',
        N'@n INT OUTPUT', @n = @conDatos OUTPUT;
    IF @conDatos > 0
        THROW 50002, N'CotizacionMercancias.idTipoMercancia tiene valores: no se puede convertir a código. Revise esas filas antes de correr el 02; no se aplicó nada.', 1;
END
GO

-- =============================================================================
-- 1 a 3. Cambios (una sola transacción)
-- =============================================================================
-- TRY/CATCH: un error dentro de un EXEC dinámico (por ejemplo de sintaxis) NO aborta la transacción aunque esté
-- XACT_ABORT; sin el CATCH, el lote seguiría hasta el COMMIT y dejaría el script aplicado a medias.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 1. CotizacionMercancias: idTipoMercancia → codigoMercancia ---------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'codigoMercancia') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionMercancias
            ADD codigoMercancia NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;  -- código del catálogo del cubicador (ej. BEBIDAS)

    IF COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'idTipoMercancia') IS NOT NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionMercancias DROP COLUMN idTipoMercancia;');

    -- 2. CotizacionVehiculosLineaSolicitud (el camión): volumen y porcentajes de llenado ----
    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'volumenTotalM3') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud ADD volumenTotalM3 DECIMAL(12,3) NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'porcentajeLlenadoVolumen') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud ADD porcentajeLlenadoVolumen DECIMAL(5,2) NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'porcentajeLlenadoPeso') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud ADD porcentajeLlenadoPeso DECIMAL(5,2) NULL;

    -- Las restricciones nombran columnas recién creadas: van con SQL dinámico para que el lote compile.
    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_llenado', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_llenado CHECK (
                (volumenTotalM3 IS NULL OR volumenTotalM3 >= 0)
                AND (porcentajeLlenadoVolumen IS NULL OR (porcentajeLlenadoVolumen >= 0 AND porcentajeLlenadoVolumen <= 100))
                AND (porcentajeLlenadoPeso IS NULL OR (porcentajeLlenadoPeso >= 0 AND porcentajeLlenadoPeso <= 100)));');

    -- 3. CotizacionLineasSolicitud (la ruta): resultado del cubicador ----------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'idRecomendacion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD idRecomendacion UNIQUEIDENTIFIER NULL;  -- corrida del cubicador, para reconsultarla

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'resultadoCubicacion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD resultadoCubicacion NVARCHAR(30) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'motivoSinSeleccion') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD motivoSinSeleccion NVARCHAR(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'condicionada') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD condicionada BIT NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_condicionada DEFAULT (0);

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_resultadoCubicacion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_resultadoCubicacion CHECK (
                resultadoCubicacion IS NULL OR resultadoCubicacion IN (''ASIGNADO'', ''SIN_SELECCION_AUTOMATICA''));');

    -- El motivo solo existe cuando el cubicador no pudo escoger.
    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_motivoSinSeleccion', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
                motivoSinSeleccion IS NULL
                OR (motivoSinSeleccion IN (''EXTRADIMENSIONADO'', ''SIN_CANDIDATOS_DIMENSIONALES'', ''SUPERA_K_MAX'', ''SIN_CARROCERIA_COMPATIBLE'')
                    AND resultadoCubicacion = ''SIN_SELECCION_AUTOMATICA''));');

    COMMIT TRANSACTION;
    PRINT N'02 aplicado: columnas del cubicador listas.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'02 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 8 filas y ninguna de idTipoMercancia):

     SELECT t.name AS tabla, c.name AS columna, ty.name AS tipo, c.is_nullable
     FROM sys.columns c
     JOIN sys.tables t ON t.object_id = c.object_id AND SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
     JOIN sys.types ty ON ty.user_type_id = c.user_type_id
     WHERE c.name IN ('codigoMercancia', 'volumenTotalM3', 'porcentajeLlenadoVolumen', 'porcentajeLlenadoPeso',
                      'idRecomendacion', 'resultadoCubicacion', 'motivoSinSeleccion', 'condicionada')
        OR c.name = 'idTipoMercancia'   -- esta ya no debe aparecer
     ORDER BY t.name, c.column_id;
   ============================================================================ */
