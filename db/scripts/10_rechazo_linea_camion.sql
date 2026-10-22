/* ============================================================================
   Cotifly — 10: rechazo de una línea con motivo, por camión
   ============================================================================
   HU-028.1/028.2 (#182, #183). La Mesa de Costeo de Nexo puede rechazar UNA línea (= un camión en Cotifly)
   con un motivo obligatorio: no frena la cotización (se puede entregar, salen PDF y Excel), la línea
   rechazada no suma al total y no lleva precio, y el motivo solo lo ve el comercial (nunca el PDF ni el
   Excel, decisión de Sara del 2026-10-16).

   Aprobado y aplicado en la base compartida el 2026-10-22 (HU-024.1, organización de scripts). Movido de
   db/scripts/pendientes/ a db/scripts/. El backend todavía NO mapea estas columnas (HU-028.1, #183):
   mapearlas sin la columna rompería cada lectura del costeo.

   Contrato de Nexo verificado el 2026-10-22 (chat de Nexo, contra origin/QA y origin/DEPLOY de Nexo):
     - `GET /api/solicitudes/{id}` → `clsLineaCosteoDto.estadoLinea` / `.motivoRechazo` YA ESTÁN en QA y
       en DEPLOY de Nexo (S6-HU-24.1/#309, S6-HU-24.3/#320): `estadoLinea = "rechazada"` y `motivoRechazo`
       (texto, null si no aplica). Se pueden leer hoy en cualquier reconsulta del polling.
     - Confirmado por línea individual (un camión), no por ruta ni por `grupoRutaId`: el comando de Nexo
       (`clsRechazarLineaCotizacionCommand`) recibe una sola `LineaSolicitudId` por llamada. La columna
       va en `CotizacionVehiculosLineaSolicitud`, como ya está pensada.
     - `GET /api/cotizaciones/{id}` (`clsCotizacionLineaDto`) NO trae nada de rechazo: una línea rechazada
       nunca tiene flete, así que nunca entra a esa lista (es solo de líneas con flete). No afecta este script.
     - Quién y cuándo rechazó (`RechazadaPor`/`RechazadaAt`) NO se pueden leer en ninguna reconsulta: solo
       salen en la respuesta puntual del endpoint de rechazar (`clsLineaRechazoDto`), que además solo existe
       en DEPLOY, no en QA/main de Nexo. Cotifly nunca llama ese endpoint (quien rechaza es la Mesa de Nexo,
       Cotifly solo hace polling), así que hoy NO hay forma de obtener esa fecha ni ese usuario desde Cotifly.
       **Por eso este script NO agrega `rechazadaAt`**: se agregaría en un script aparte si Nexo expone
       `RechazadaPor`/`RechazadaAt` en una lectura reconsultable (pedírselo a Ismael si se necesita).

   Qué hace (NO destructivo e idempotente: solo agrega lo que falta y no cambia datos):
     1. CotizacionVehiculosLineaSolicitud.estadoCosteo: agrega 'rechazada' a la lista de valores aceptados
        (antes: pendiente_envio, error_envio, pendiente_costeo, autocosteo, entregada, descartada,
        costeo_vencido). Un camión rechazado se ignora en los totales de su ruta, igual que uno descartado
        (HU-028.1, clsLineaSolicitud.VehiculosQueCuentan), pero con su propio motivo.
     2. CotizacionVehiculosLineaSolicitud.motivoRechazo NVARCHAR(500) NULL: el texto que llega en
        `motivoRechazo` de Nexo. Solo lo ve el comercial (decisión de Sara); nunca va al PDF ni al Excel.
     3. CotizacionLineasSolicitud.estadoCosteo (la RUTA, resumen de sus camiones): agrega 'rechazada' a su
        lista de valores, con la misma regla que ya existe para 'descartada' (clsResumenEstadoCosteo): si
        TODOS los camiones de la ruta quedan rechazados, la ruta misma queda 'rechazada', sin precio, y no
        bloquea la cotización.
     4. CotizacionesSolicitud.estadoCosteo (la CABECERA) **NO se toca**: por decisión de Sara, si se
        rechazan TODAS las rutas de una cotización, Nexo la entrega igual ("la cotización se envía igual
        al comercial, con los motivos de cada línea") — el resumen de la cabecera queda 'entregada', no
        'rechazada'. Si esto cambia, hace falta otro script.

   Dónde corre: después del 02 (no depende del 03 al 09). Una sola transacción: si algo falla, no queda
   nada aplicado. Si ya se aplicó, no cambia nada.

   Cómo ejecutarlo (-b detiene el script en el primer error):
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 10_rechazo_linea_camion.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema nuevo: corra primero 01_crear_esquema.sql y 02_alter_cubicador.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- 1 y 2. Camión: estado 'rechazada' + motivo ------------------------------------------------
    IF COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'motivoRechazo') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud
            ADD motivoRechazo NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo'
               AND definition NOT LIKE N'%rechazada%')
        ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionVehiculosLineaSolicitud_estadoCosteo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
                (''pendiente_envio'', ''error_envio'', ''pendiente_costeo'', ''autocosteo'', ''entregada'', ''descartada'',
                 ''costeo_vencido'', ''rechazada''));');

    -- 3. Ruta: estado 'rechazada' en el resumen --------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_estadoCosteo'
               AND definition NOT LIKE N'%rechazada%')
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo;

    IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_estadoCosteo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
            ADD CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
                (''pendiente_envio'', ''error_envio'', ''pendiente_costeo'', ''autocosteo'', ''entregada'', ''descartada'',
                 ''costeo_vencido'', ''rechazada''));');

    COMMIT TRANSACTION;
    PRINT N'10 aplicado: estado rechazada y motivoRechazo listos en el camión, y rechazada en el resumen de la ruta.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'10 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar:

     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'motivoRechazo');  -- 1000 (500 UTF-16)

     SELECT name, definition FROM sys.check_constraints
     WHERE name IN (N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo', N'ck_cotizacionLineasSolicitud_estadoCosteo');
     -- las dos definiciones deben mencionar 'rechazada'

   Reversa (solo con aprobación, y antes de que el backend mapee estas columnas):
     recrear los dos CHECK sin 'rechazada' (ver 01_crear_esquema.sql) y
     ALTER TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud DROP COLUMN motivoRechazo;
   ============================================================================ */
