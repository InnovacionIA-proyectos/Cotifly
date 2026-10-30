/* ============================================================================
   Cotifly — 11: peso con tara de una ruta de contenedor
   ============================================================================
   HU-030.1 (#184). Las cargas contenerizadas (clase de viaje `Contenedor`) deben llegar a NEXO con el
   peso total CON la tara de los contenedores, que escribe el comercial (decisión D2/D2b; aclaración del
   líder técnico del 2026-10-20: solo Contenedor). NEXO no suma la tara (COTIFLY-CONTRATO.md) y valida
   PESO_INCONSISTENTE (1 %) cuando la línea trae posiciones (NEXO#308), así que una línea con tara viaja
   sin posiciones. Hasta ahora Cotifly no conocía la tara.

   Qué hace (NO destructivo e idempotente: solo agrega lo que falta y no cambia datos):
     pesoConTaraKg DECIMAL(12,2) NULL en dbo_cotifly.CotizacionLineasSolicitud (la RUTA), con el CHECK
     ck_cotizacionLineasSolicitud_pesoConTaraKg (NULL o > 0).
       - NULL = la ruta no lleva tara (a NEXO va el peso de carga, como antes) o se guardó antes del 11.
       - Por qué en la ruta y no en el camión: el comercial escribe el valor en la captura, cuando los
         camiones todavía no existen (salen del cubicador en la misma petición). Cotifly lo reparte al
         enviar: la tara total (peso con tara - peso de carga) por partes iguales entre los camiones de la
         ruta (uno por contenedor, de la misma clase). Así el valor sobrevive a una recubicación, a la
         cubicación en segundo plano de la licitación y a la edición sin columnas por camión.
       - DECIMAL(12,2): el mismo tipo que pesoKg de la ruta y del camión (Nexo admite hasta 999999.99 kg
         con 2 decimales). El backend valida además que no sea menor que el peso de carga; la base solo
         exige > 0 (el peso de carga puede ser la suma de las mercancías, que no es una columna).
     No hace falta respaldo: es un ADD de una columna NULL, sin tocar las filas.

   Dónde corre: después del 09 (o del 01). No depende del 02, del 04 ni del 07. El número 10 lo usa otra
   historia (#183). El ejecutador (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend SÍ
   mapea esta columna: sin ella /health/ready responde 503 "esquema incompleto: falta 11", así que se
   aplica ANTES de desplegar la imagen.

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 11_peso_con_tara.sql
   ============================================================================ */

SET NOCOUNT ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionLineasSolicitud: aplique primero el 0009 y el 0010 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columna y CHECK (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'pesoConTaraKg') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD pesoConTaraKg DECIMAL(12,2) NULL;

    -- SQL dinámico: la columna recién agregada no existe cuando se compila el lote.
    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = N'ck_cotizacionLineasSolicitud_pesoConTaraKg'
                     AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'))
        EXEC (N'ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_pesoConTaraKg
                  CHECK (pesoConTaraKg IS NULL OR pesoConTaraKg > 0);');

    COMMIT TRANSACTION;
    PRINT N'11 aplicado: pesoConTaraKg listo en CotizacionLineasSolicitud.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'11 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 1 fila: decimal, 12, 2, 1, 1):

     SELECT c.name AS columna, ty.name AS tipo, c.precision, c.scale, c.is_nullable,
            (SELECT COUNT(*) FROM sys.check_constraints k
             WHERE k.name = N'ck_cotizacionLineasSolicitud_pesoConTaraKg' AND k.parent_object_id = c.object_id) AS con_check
     FROM sys.columns c
     JOIN sys.types ty ON ty.user_type_id = c.user_type_id
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND c.name = 'pesoConTaraKg';

   Reversa (solo antes de desplegar un backend que la mapee; con él, /health/ready vuelve a 503):
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_pesoConTaraKg;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP COLUMN pesoConTaraKg;
   ============================================================================ */
