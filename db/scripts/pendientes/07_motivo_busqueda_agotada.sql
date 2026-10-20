/* ============================================================================
   Cotifly — 07: motivo BUSQUEDA_AGOTADA en el CHECK de "sin selección" de la ruta
   ============================================================================
   Está en db/scripts/pendientes/: el ejecutador (ejecutar-bd.ps1 / .sh) no entra a esta carpeta. Al aprobarlo
   se mueve a db/scripts/ (HU-024).

   Por qué: el cubicador tiene un quinto motivo de SIN_SELECCION_AUTOMATICA, `BUSQUEDA_AGOTADA` (el buscador se
   quedó sin intentos; es del algoritmo, no del negocio). Lo documenta
   backend-dotnet/docs/COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md (rama 146-hu-0221-limpieza, HU-022.1). El CHECK
   ck_cotizacionLineasSolicitud_motivoSinSeleccion solo acepta cuatro motivos, así que una ruta con ese motivo
   no se podría guardar.

   Qué hace: recrea el CHECK con los cinco motivos y CONSERVA la condición que agregó el 03: si hay motivo,
   resultadoCubicacion tiene que ser SIN_SELECCION_AUTOMATICA (ISNULL, para que un resultado NULL no pase).
     motivoSinSeleccion IS NULL
     OR (motivoSinSeleccion IN ('EXTRADIMENSIONADO', 'SIN_CANDIDATOS_DIMENSIONALES', 'SUPERA_K_MAX',
                                'SIN_CARROCERIA_COMPATIBLE', 'BUSQUEDA_AGOTADA')
         AND ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA')

   Antes de tocar nada, se detiene (THROW 50071) si alguna ruta incumple la regla nueva y dice cuántas son.
   Idempotente: si el CHECK ya acepta BUSQUEDA_AGOTADA y exige el resultado, no cambia nada.
   Una sola transacción: si algo falla, el CHECK anterior queda como estaba.
   Va después del 02 (crea la columna) y sirve con o sin el 03 aplicado.

   El backend también tiene que conocer el motivo: la configuración de EF (clsCotizacionConfiguracion.cs) y las
   constantes del cubicador (clsRecomendacionCubicador.cs) tienen hoy la lista de cuatro.

   Cómo ejecutarlo:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i db/scripts/pendientes/07_motivo_busqueda_agotada.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'motivoSinSeleccion') IS NULL
   OR COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'resultadoCubicacion') IS NULL
    THROW 50001, N'Falta la columna motivoSinSeleccion o resultadoCubicacion: corra primero 02_alter_cubicador.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @definicion NVARCHAR(MAX) = (
        SELECT definition FROM sys.check_constraints
        WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'
          AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'));

    IF @definicion IS NOT NULL AND @definicion LIKE N'%BUSQUEDA_AGOTADA%' AND @definicion LIKE N'%isnull%'
    BEGIN
        PRINT N'07 ya estaba aplicado: el CHECK acepta BUSQUEDA_AGOTADA y exige el resultado. No se cambió nada.';
    END
    ELSE
    BEGIN
        DECLARE @incumplen INT = (
            SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud
            WHERE motivoSinSeleccion IS NOT NULL
              AND (motivoSinSeleccion NOT IN (N'EXTRADIMENSIONADO', N'SIN_CANDIDATOS_DIMENSIONALES', N'SUPERA_K_MAX',
                                              N'SIN_CARROCERIA_COMPATIBLE', N'BUSQUEDA_AGOTADA')
                   OR ISNULL(resultadoCubicacion, N'') <> N'SIN_SELECCION_AUTOMATICA'));
        IF @incumplen > 0
        BEGIN
            DECLARE @mensaje NVARCHAR(400) = CONCAT(N'07 no se aplicó: ', @incumplen,
                N' ruta(s) tienen motivoSinSeleccion con un valor desconocido o sin resultadoCubicacion = SIN_SELECCION_AUTOMATICA. ',
                N'Corríjalas antes (SELECT id, motivoSinSeleccion, resultadoCubicacion FROM dbo_cotifly.CotizacionLineasSolicitud WHERE motivoSinSeleccion IS NOT NULL).');
            THROW 50071, @mensaje, 1;
        END

        IF @definicion IS NOT NULL
            ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;

        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
                motivoSinSeleccion IS NULL
                OR (motivoSinSeleccion IN (''EXTRADIMENSIONADO'', ''SIN_CANDIDATOS_DIMENSIONALES'', ''SUPERA_K_MAX'',
                                           ''SIN_CARROCERIA_COMPATIBLE'', ''BUSQUEDA_AGOTADA'')
                    AND ISNULL(resultadoCubicacion, '''') = ''SIN_SELECCION_AUTOMATICA''));');

        PRINT N'07 aplicado: el CHECK acepta BUSQUEDA_AGOTADA y sigue exigiendo resultadoCubicacion = SIN_SELECCION_AUTOMATICA.';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'07 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe mencionar BUSQUEDA_AGOTADA e isnull):

     SELECT definition FROM sys.check_constraints WHERE name = 'ck_cotizacionLineasSolicitud_motivoSinSeleccion';

   Reversa (solo con aprobación): volver a correr el bloque 3 del 03 (cuatro motivos con ISNULL), después de
   comprobar que ninguna ruta tiene motivoSinSeleccion = 'BUSQUEDA_AGOTADA'.
   ============================================================================ */
