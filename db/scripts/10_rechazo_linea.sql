/* ============================================================================
   Cotifly — 10: rechazo de una línea en la Mesa de Costeo de Nexo
   ============================================================================
   HU-028.2 (#182), que habilita HU-028.1 (#183). Lo escribió el equipo de Cotifly; lo revisa @scalderon-bs (Sara) antes de
   aplicarlo en la base compartida.

   Contexto. Desde Nexo #320 (S6-HU-24.3, columnas de Nexo #314) el Administrador de Tarifas puede RECHAZAR una línea de la
   solicitud con un motivo obligatorio (hasta 500 caracteres) y deshacerlo mientras la cotización de Nexo no esté entregada.
   GET /api/solicitudes/{id} devuelve en cada línea `estadoLinea = "rechazada"` y `motivoRechazo` (backend-dotnet/docs/
   COTIFLY-CONTRATO.md §5.1 de Nexo). NO expone quién rechazó (RechazadaPor, un id de usuario de Nexo) ni cuándo (RechazadaAt),
   ni hay un catálogo de motivos: el motivo es texto libre. En Cotifly cada línea de Nexo es un CAMIÓN
   (dbo_cotifly.CotizacionVehiculosLineaSolicitud, HU-021): el rechazo se guarda ahí.

   Qué hace (NO destructivo e idempotente: cada cambio revisa si ya está; no cambia datos):
     1. CotizacionVehiculosLineaSolicitud.motivoRechazo NVARCHAR(500) NULL: el motivo que mandó Nexo (mismo largo que
        tbl_lineas_solicitud.MotivoRechazo de Nexo). NULL = el camión no está rechazado.
     2. CotizacionVehiculosLineaSolicitud.rechazadaAt DATETIMEOFFSET NULL: cuándo VIO Cotifly el rechazo por primera vez (la
        lectura del polling que lo trajo). Nexo no expone su fecha en el GET que lee Cotifly; si algún día la expone, se
        puede llenar con ella sin cambiar la columna. No se agrega "rechazadaPor": Nexo no lo expone y sería siempre NULL.
     3. 'rechazada' en el CHECK de estadoCosteo de las TRES tablas (ck_cotizaciones_estadoCosteo,
        ck_cotizacionLineasSolicitud_estadoCosteo y ck_cotizacionVehiculosLineaSolicitud_estadoCosteo):
          - camión: la Mesa rechazó su línea;
          - ruta: TODOS sus camiones vigentes están rechazados (sin precio, no suma en el total);
          - cabecera: el backend NO la escribe (con todas las rutas rechazadas la cabecera queda 'entregada' o
            'pendiente_costeo'), pero las tres tablas comparten la misma lista de estados con el enum del backend
            (EstadoCosteo; lo verifica la prueba ElCheckDeEstadoCosteoCubreExactamenteLosCodigosDelEnum). Se agrega en las
            tres para que las tres listas sigan iguales.
        Cada CHECK se reemplaza solo si a su definición le falta 'rechazada'; el nuevo es el mismo con 'rechazada' al final.
     4. ck_cotizacionVehiculosLineaSolicitud_rechazo: motivo y fecha solo con el camión en 'rechazada'
          ISNULL(estadoCosteo, N'') = N'rechazada' OR (motivoRechazo IS NULL AND rechazadaAt IS NULL)
        (con ISNULL: sin él, un camión con estadoCosteo NULL y motivo pasaría, porque NULL = 'x' es UNKNOWN y un CHECK UNKNOWN
        pasa; el mismo problema que corrigió el 03 en el CHECK del motivo del cubicador). Un camión 'rechazada' SIN motivo sí se
        admite (Nexo exige el motivo, pero un valor vacío no debe tumbar el polling).
     No hace falta respaldo: son ADD de columnas NULL y CHECK que las filas actuales ya cumplen (ninguna está 'rechazada').

   Dónde corre: después del 0010 (o del 01) y del 03 (el 03 crea el índice filtrado de la misma tabla; no lo toca). No depende
   del 02, del 04, del 05, del 06, del 07, del 08 ni del 09. El ejecutador (ejecutar-bd.ps1 / .sh), db-init y levantar-local.*
   lo toman solos, en orden de nombre (después del 09).

   *** OBLIGATORIO ANTES DE DESPLEGAR LA IMAGEN DE #183 ***
   El backend mapea motivoRechazo y rechazadaAt: sin este script /health/ready responde 503 "esquema incompleto: falta 10" y
   Traefik no manda tráfico a la versión nueva (igual que con el 03, el 04, el 05, el 06 y el 09). Aplicarlo con la imagen
   anterior no rompe nada: la imagen anterior no lee las columnas nuevas y nunca escribe 'rechazada'.

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 10_rechazo_linea.sql
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
IF OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema de rutas y camiones: aplique primero el 0009 y el 0010 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columnas, CHECK de estadoCosteo y CHECK del rechazo (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 1.1 Columnas del rechazo en el camión ----------------------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'motivoRechazo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud
            ADD motivoRechazo NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;   -- motivo de Nexo (#320)

    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'rechazadaAt') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud
            ADD rechazadaAt DATETIMEOFFSET NULL;                                              -- cuándo lo vio Cotifly

    -- 1.2 'rechazada' en el CHECK de estadoCosteo de las tres tablas -----------------
    --     Solo se reemplaza un CHECK al que le falta 'rechazada' (repetir el script no lo toca).
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_estadoCosteo'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND definition NOT LIKE N'%''rechazada''%')
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP CONSTRAINT ck_cotizaciones_estadoCosteo;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizaciones_estadoCosteo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionesSolicitud WITH CHECK ADD CONSTRAINT ck_cotizaciones_estadoCosteo CHECK (
            estadoCosteo IS NULL OR estadoCosteo IN
            (''pendiente_envio'', ''error_envio'', ''pendiente_costeo'', ''autocosteo'', ''entregada'', ''descartada'', ''costeo_vencido'', ''rechazada''));');

    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_estadoCosteo'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND definition NOT LIKE N'%''rechazada''%')
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_estadoCosteo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo CHECK (
            estadoCosteo IS NULL OR estadoCosteo IN
            (''pendiente_envio'', ''error_envio'', ''pendiente_costeo'', ''autocosteo'', ''entregada'', ''descartada'', ''costeo_vencido'', ''rechazada''));');

    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud') AND definition NOT LIKE N'%''rechazada''%')
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_estadoCosteo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo CHECK (
            estadoCosteo IS NULL OR estadoCosteo IN
            (''pendiente_envio'', ''error_envio'', ''pendiente_costeo'', ''autocosteo'', ''entregada'', ''descartada'', ''costeo_vencido'', ''rechazada''));');

    -- 1.3 Motivo y fecha solo con el camión rechazado ----------------------------------
    --     EXEC: las columnas se crean en esta misma transacción y el lote se compila antes de crearlas.
    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_rechazo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_rechazo CHECK (
            ISNULL(estadoCosteo, N'''') = N''rechazada'' OR (motivoRechazo IS NULL AND rechazadaAt IS NULL));');

    COMMIT TRANSACTION;
    PRINT N'10 aplicado: motivoRechazo y rechazadaAt en CotizacionVehiculosLineaSolicitud, ''rechazada'' en los tres CHECK de estadoCosteo y CHECK del rechazo listos.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'10 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- =============================================================================
-- 2. Verificación (solo lectura): se detiene si algo no quedó
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'motivoRechazo') IS NULL
   OR COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'rechazadaAt') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_rechazo', N'C') IS NULL
   OR (SELECT COUNT(*) FROM sys.check_constraints
       WHERE name IN (N'ck_cotizaciones_estadoCosteo', N'ck_cotizacionLineasSolicitud_estadoCosteo', N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo')
         AND definition LIKE N'%''rechazada''%') <> 3
    THROW 50002, N'10: la verificación falló (faltan columnas o CHECK). Revise el error anterior.', 1;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar:

     -- Columnas nuevas (2 filas: motivoRechazo nvarchar 1000 bytes = 500 caracteres, rechazadaAt datetimeoffset; ambas NULL)
     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.max_length, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud') AND c.name IN (N'motivoRechazo', N'rechazadaAt');

     -- Los cuatro CHECK (los tres de estadoCosteo con 'rechazada' y el del rechazo)
     SELECT name, definition FROM sys.check_constraints
     WHERE name IN (N'ck_cotizaciones_estadoCosteo', N'ck_cotizacionLineasSolicitud_estadoCosteo',
                    N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo', N'ck_cotizacionVehiculosLineaSolicitud_rechazo');

   Reversa (solo antes de desplegar un backend que las mapee; con él, /health/ready vuelve a 503 "falta 10"). Falla a propósito
   si ya hay filas 'rechazada' (el CHECK de 7 estados no las admite): en ese caso no hay reversa sin perder el rechazo.
     ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_rechazo;
     ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP COLUMN motivoRechazo, rechazadaAt;
     -- y, en cada una de las tres tablas, DROP del CHECK de estadoCosteo y ADD del mismo sin 'rechazada' (el de 01_crear_esquema.sql).
   ============================================================================ */
