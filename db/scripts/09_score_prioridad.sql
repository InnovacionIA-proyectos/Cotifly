/* ============================================================================
   Cotifly — 09: scorePrioridad de NEXO en la cabecera de la solicitud
   ============================================================================
   HU-018.1 (#155). NEXO responde el alta de la solicitud (POST /api/captura-solicitudes, 201
   CapturaResultDto, backend-dotnet/docs/COTIFLY-CONTRATO.md §5) con `scorePrioridad`, y hasta ahora
   Cotifly lo descartaba. El score es de la SOLICITUD de NEXO, que en Cotifly es la cabecera
   (dbo_cotifly.CotizacionesSolicitud, la misma fila que guarda solicitudNexoId): va ahí, no en la ruta.

   Qué hace (NO destructivo e idempotente: solo agrega la columna si falta y no cambia datos):
     scorePrioridad DECIMAL(9,4) NULL en dbo_cotifly.CotizacionesSolicitud.
       - NULL = NEXO no lo mandó, la solicitud no se ha enviado o se envió antes de este script.
       - DECIMAL(9,4): el contrato no fija la escala (el ejemplo de NEXO trae 62.5; sus pruebas, 0.73),
         así que se guardan 4 decimales y hasta 99999.9999. El backend redondea a 4 decimales y descarta
         (deja NULL) un valor fuera de ese rango, sin fallar el envío.
     No hace falta respaldo: es un ADD de una columna NULL, sin tocar las filas.

   Dónde corre: después del 0010 (o del 01). No depende del 02, del 04 ni del 07. El ejecutador
   (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend SÍ mapea esta columna: sin ella
   /health/ready responde 503 "esquema incompleto: falta 09" (igual que con el 04), así que se aplica
   ANTES de desplegar la imagen.

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 09_score_prioridad.sql
   ============================================================================ */

SET NOCOUNT ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionesSolicitud: aplique primero el 0009 y el 0010 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columna (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'scorePrioridad') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD scorePrioridad DECIMAL(9,4) NULL;

    COMMIT TRANSACTION;
    PRINT N'09 aplicado: scorePrioridad listo en CotizacionesSolicitud.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'09 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 1 fila: decimal, 9, 4, 1):

     SELECT c.name AS columna, ty.name AS tipo, c.precision, c.scale, c.is_nullable
     FROM sys.columns c
     JOIN sys.types ty ON ty.user_type_id = c.user_type_id
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name = 'scorePrioridad';

   Reversa (solo antes de desplegar un backend que la mapee; con él, /health/ready vuelve a 503):
     ALTER TABLE dbo_cotifly.CotizacionesSolicitud DROP COLUMN scorePrioridad;
   ============================================================================ */
