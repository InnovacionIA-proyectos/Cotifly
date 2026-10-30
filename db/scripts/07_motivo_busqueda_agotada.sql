/* ============================================================================
   Cotifly — 07: quinto motivo de "sin selección" (BUSQUEDA_AGOTADA)
   ============================================================================
   HU-023 (#176). El contrato vigente del cubicador (COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md §5.3)
   agrega un quinto valor de `motivoSinSeleccion`: BUSQUEDA_AGOTADA (el buscador del cubicador se
   quedó sin intentos; es del algoritmo, no del negocio). El CHECK del 02 solo admite cuatro.

   Qué hace (todo en dbo_cotifly.CotizacionLineasSolicitud):
     Reemplaza ck_cotizacionLineasSolicitud_motivoSinSeleccion por uno con los 5 motivos y con
     `ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA'`: conserva la regla del 02 (el
     motivo solo existe cuando el cubicador no pudo escoger) y cierra el hueco del NULL (en el 02,
     un motivo con resultadoCubicacion NULL pasaba porque el CHECK daba UNKNOWN). Es el mismo
     CHECK que deja db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql: si el 03 corre después, ve BUSQUEDA_AGOTADA y el
     ISNULL y no toca este CHECK.

   Dónde corre: después del 02 (el ejecutador corre los .sql de db/scripts en orden de nombre). Sin el
   07 el backend sigue funcionando: detecta que el CHECK no admite BUSQUEDA_AGOTADA, guarda el
   motivo en NULL y deja el motivo real como advertencia de la ruta (MOTIVO_SIN_SELECCION).
   Con el 07 guarda el motivo real. /health/ready solo lo avisa (no deja de estar listo).

   Se puede correr varias veces: si el CHECK ya contiene BUSQUEDA_AGOTADA y el ISNULL no hace nada (si le falta
   el ISNULL, por ejemplo una versión aplicada a mano con `AND resultadoCubicacion = ...`, lo reemplaza). DROP y
   ADD ... WITH CHECK van en UNA transacción: si algo falla, queda el CHECK anterior.

   Respaldo: el script imprime la definición anterior del CHECK antes de reemplazarla. Para
   volver atrás (solo si no hay filas con BUSQUEDA_AGOTADA):
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion
       CHECK (<la definición impresa>);

   Cómo ejecutarlo a mano:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 07_motivo_busqueda_agotada.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'motivoSinSeleccion') IS NULL
   OR COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'resultadoCubicacion') IS NULL
    THROW 50001, N'dbo_cotifly no tiene las columnas del cubicador: corra primero 02_alter_cubicador.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    DECLARE @anterior NVARCHAR(MAX) = (
        SELECT definition FROM sys.check_constraints
        WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'
          AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'));

    -- Ya aplicado solo si tiene BUSQUEDA_AGOTADA Y el ISNULL: una versión sin ISNULL (motivo con resultadoCubicacion NULL pasaba) se corrige.
    IF @anterior IS NOT NULL AND @anterior LIKE N'%BUSQUEDA[_]AGOTADA%' AND @anterior LIKE N'%isnull%'
    BEGIN
        PRINT N'07 ya estaba aplicado: el CHECK del motivo ya admite BUSQUEDA_AGOTADA y exige ISNULL. No se cambió nada.';
        RETURN;
    END;

    -- Filas que el CHECK nuevo rechazaría (motivo sin SIN_SELECCION_AUTOMATICA): se detiene sin tocar nada.
    DECLARE @invalidas INT = (
        SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud
        WHERE motivoSinSeleccion IS NOT NULL
          AND (ISNULL(resultadoCubicacion, N'') <> N'SIN_SELECCION_AUTOMATICA'
               OR motivoSinSeleccion NOT IN (N'EXTRADIMENSIONADO', N'SIN_CANDIDATOS_DIMENSIONALES', N'SUPERA_K_MAX',
                                             N'SIN_CARROCERIA_COMPATIBLE', N'BUSQUEDA_AGOTADA')));
    IF @invalidas > 0
    BEGIN
        DECLARE @mensaje NVARCHAR(400) = CONCAT(N'07 NO se aplicó: ', @invalidas,
            N' ruta(s) tienen motivoSinSeleccion sin resultadoCubicacion = SIN_SELECCION_AUTOMATICA. Revíselas con la consulta de verificación.');
        THROW 50002, @mensaje, 1;
    END;

    IF @anterior IS NOT NULL
        PRINT CONCAT(N'Respaldo del CHECK anterior: ', @anterior);

    BEGIN TRANSACTION;

    IF @anterior IS NOT NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion;

    EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
        ADD CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
            motivoSinSeleccion IS NULL
            OR (motivoSinSeleccion IN (''EXTRADIMENSIONADO'', ''SIN_CANDIDATOS_DIMENSIONALES'', ''SUPERA_K_MAX'',
                                       ''SIN_CARROCERIA_COMPATIBLE'', ''BUSQUEDA_AGOTADA'')
                AND ISNULL(resultadoCubicacion, '''') = ''SIN_SELECCION_AUTOMATICA''));');

    COMMIT TRANSACTION;
    PRINT N'07 aplicado: el CHECK del motivo admite los 5 motivos del contrato (con BUSQUEDA_AGOTADA).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'07 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar:

     -- El CHECK nuevo (1 fila, la definición debe contener BUSQUEDA_AGOTADA e isnull, e is_not_trusted = 0)
     SELECT name, definition, is_not_trusted, is_disabled
     FROM sys.check_constraints
     WHERE name = 'ck_cotizacionLineasSolicitud_motivoSinSeleccion';

     -- Ninguna ruta con motivo y sin SIN_SELECCION_AUTOMATICA (0 filas)
     SELECT id, resultadoCubicacion, motivoSinSeleccion
     FROM dbo_cotifly.CotizacionLineasSolicitud
     WHERE motivoSinSeleccion IS NOT NULL AND ISNULL(resultadoCubicacion, '') <> 'SIN_SELECCION_AUTOMATICA';
   ============================================================================ */
