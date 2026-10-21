/* ============================================================================
   Cotifly — 12: vehículo elegido de la ruta y mercancías sin medidas
   ============================================================================
   HU-032 (#197, ADR-022). Decisión del líder técnico del 2026-10-21 (Hallazgo 1 de QA: el cubicador
   sobreescribía en silencio el vehículo digitado). El vehículo que elige el comercial vuelve a la captura
   como RESPALDO del cubicador: se usa cuando la ruta no se puede cubicar (solo peso o mercancías sin
   medidas) o cuando el cubicador no selecciona vehículo o no responde. Además se aceptan mercancías "no
   cubicables": solo con descripción, cantidad o peso, sin ninguna medida ni tipo de mercancía.

   Qué hace (NO destructivo e idempotente: solo agrega o amplía; no cambia datos):
     1. CotizacionLineasSolicitud (la RUTA): tipoVehiculoElegido NVARCHAR(100) NULL y carroceriaElegida
        NVARCHAR(100) NULL (el mismo tipo y la misma intercalación que tipoVehiculo/carroceria del camión).
          - Por qué en la ruta y no en el camión: el camión guarda lo que VIAJA (el del cubicador, el elegido
            o lo que la Mesa de Costeo puso en Nexo, D2); el elegido es la intención del comercial y lo
            conservan la edición y la re-solicitud aunque el cubicador o la Mesa lo cambien.
          - NULL = el comercial no eligió vehículo o la ruta se guardó antes del 12.
     2. CotizacionMercancias: largoCm, anchoCm y altoCm pasan a NULL, y el CHECK
        ck_cotizacionPosiciones_medidas se reemplaza: o las tres NULL (mercancía no cubicable) o las tres
        > 0 (como hasta ahora). pesoKg no cambia (NULL o >= 0). Las filas existentes cumplen las dos reglas.

   Dónde corre: después del 11 (o del 01). No depende del 02, del 04 ni del 07. El ejecutador
   (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend SÍ mapea las columnas nuevas y
   guarda medidas NULL: sin él /health/ready responde 503 "esquema incompleto: falta 12", así que se
   aplica ANTES de desplegar la imagen.

   Respaldo: no hace falta. Las columnas nuevas son NULL (no tocan filas) y ALTER COLUMN ... NULL solo
   relaja la restricción; el CHECK nuevo admite todo lo que admitía el anterior. El CHECK anterior queda
   impreso abajo y en la reversa.

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 12_vehiculo_elegido.sql
   ============================================================================ */

SET NOCOUNT ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.CotizacionMercancias', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionLineasSolicitud o CotizacionMercancias: aplique primero el 0009 y el 0010 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. Columnas del vehículo elegido, medidas NULL y CHECK nuevo (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'tipoVehiculoElegido') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD tipoVehiculoElegido NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'carroceriaElegida') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud
            ADD carroceriaElegida NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    -- El CHECK anterior (todas > 0) se imprime como respaldo y se quita antes de cambiar la nulabilidad.
    DECLARE @definicion NVARCHAR(MAX) = (SELECT definition FROM sys.check_constraints
                                         WHERE name = N'ck_cotizacionPosiciones_medidas'
                                           AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias'));
    IF @definicion IS NOT NULL AND CHARINDEX(N'largoCm] IS NULL', @definicion) = 0
    BEGIN
        PRINT N'CHECK anterior de ck_cotizacionPosiciones_medidas: ' + @definicion;
        ALTER TABLE dbo_cotifly.CotizacionMercancias DROP CONSTRAINT ck_cotizacionPosiciones_medidas;
    END;

    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias') AND name = N'largoCm' AND is_nullable = 0)
        ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN largoCm DECIMAL(10,2) NULL;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias') AND name = N'anchoCm' AND is_nullable = 0)
        ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN anchoCm DECIMAL(10,2) NULL;
    IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias') AND name = N'altoCm' AND is_nullable = 0)
        ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN altoCm DECIMAL(10,2) NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
                   WHERE name = N'ck_cotizacionPosiciones_medidas'
                     AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias'))
        ALTER TABLE dbo_cotifly.CotizacionMercancias WITH CHECK ADD CONSTRAINT ck_cotizacionPosiciones_medidas
            CHECK (((largoCm IS NULL AND anchoCm IS NULL AND altoCm IS NULL) OR (largoCm > 0 AND anchoCm > 0 AND altoCm > 0))
                   AND (pesoKg IS NULL OR pesoKg >= 0));

    COMMIT TRANSACTION;
    PRINT N'12 aplicado: vehículo elegido en CotizacionLineasSolicitud y medidas NULL en CotizacionMercancias.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'12 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 5 filas: las dos columnas nuevas con is_nullable = 1,
   las tres medidas con is_nullable = 1) y el CHECK con "IS NULL":

     SELECT OBJECT_NAME(c.object_id) AS tabla, c.name AS columna, c.is_nullable
     FROM sys.columns c
     WHERE (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud') AND c.name IN ('tipoVehiculoElegido', 'carroceriaElegida'))
        OR (c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionMercancias') AND c.name IN ('largoCm', 'anchoCm', 'altoCm'));
     SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionPosiciones_medidas';

   Reversa (solo antes de desplegar un backend que las mapee; con él, /health/ready vuelve a 503). Falla si
   ya hay mercancías sin medidas: hay que corregirlas o borrarlas antes.
     ALTER TABLE dbo_cotifly.CotizacionMercancias DROP CONSTRAINT ck_cotizacionPosiciones_medidas;
     ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN largoCm DECIMAL(10,2) NOT NULL;
     ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN anchoCm DECIMAL(10,2) NOT NULL;
     ALTER TABLE dbo_cotifly.CotizacionMercancias ALTER COLUMN altoCm DECIMAL(10,2) NOT NULL;
     ALTER TABLE dbo_cotifly.CotizacionMercancias WITH CHECK ADD CONSTRAINT ck_cotizacionPosiciones_medidas
         CHECK (largoCm > 0 AND anchoCm > 0 AND altoCm > 0 AND (pesoKg IS NULL OR pesoKg >= 0));
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP COLUMN tipoVehiculoElegido;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP COLUMN carroceriaElegida;
   ============================================================================ */
