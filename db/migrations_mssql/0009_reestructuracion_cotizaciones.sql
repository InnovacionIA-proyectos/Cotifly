/* ============================================================================
   Cotifly — 0009: reestructuración de cotizaciones (rutas, camiones, mercancías y conexos)
   ============================================================================
   HU-021. Diseño aprobado en docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md; cambios
   que esto le pide al backend en docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md.
   Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First:
   el esquema SOLO cambia con scripts `db/migrations_mssql/000N_*.sql`).

   *** PENDIENTE DE APROBACIÓN — no ejecutar en tbsdatacenter sin el visto bueno de
       scalderon-bs y del DBA. ***

   *** NO agregar este script a las pruebas de integración del backend (Testcontainers)
       hasta que el backend esté adaptado: el modelo EF actual no coincide con este
       esquema y las pruebas de esquema fallarían. ***

   Qué hace (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. Respaldo de las tablas que se modifican (Respaldo0009_*), con todos sus datos.
     2. Catálogo de líneas de negocio: borra la cotización de prueba de ITR; quita
        Almacenamiento, Almacenamiento3PL e ITR; agrega DevolucionContenedor.
     3. CotizacionLineasSolicitud pasa a ser la RUTA: gana línea de negocio, horas,
        margen y totales; pierde vehículo, textos de ciudades, costo único y el JSON de
        conexos; se renombran cotizacionId, filaOrigen, precioFinal y
        valorAprovisionamiento.
     4. Tabla nueva CotizacionVehiculosLineaSolicitud: un camión por fila.
     5. Tabla nueva CotizacionServiciosConexos: los conexos de cada ruta (reemplaza los
        dos JSON serviciosConexos).
     6. CotizacionPosiciones pasa a CotizacionMercancias: cuelga solo de la ruta, gana
        idTipoMercancia, descripcion pasa a nombreMercancia.
     7. MargenAjustes gana lineaSolicitudId (la ruta ajustada).
     8. Cotizaciones pasa a CotizacionesSolicitud y pierde todo lo que ahora vive en la
        ruta o en el camión.
     9. Redefine las vistas vAjustesMargenPorComercial y vActividadPorComercial.

   Datos que ya existen (§11 de la propuesta):
     - Cada cotización queda con al menos una ruta. Las que no tenían línea (las del
       agente de chat) reciben una ruta 1 armada desde la cabecera.
     - Cada ruta existente queda con un camión, con el vehículo, peso, id de Nexo y
       estado que tenía la línea. El costo viejo se reparte así: levante =
       valorAprovisionamiento, flete = costo - levante, retorno = 0.
     - Margen, línea de negocio, horas y tipología de la cabecera se copian a cada ruta.
     - Los conexos incluidos del JSON pasan a filas de la ruta 1 (precio = costo / cantidad).
     - Los ajustes de margen se apuntan a la ruta 1 de su cotización.
     - precioFinal de la cabecera y precioFinalLinea de cada ruta se conservan tal cual.
     - SE PIERDE de las tablas vivas (queda en Respaldo0009_*): el texto de origen,
       destino y ciudades del contenedor de cada línea (solo quedan los ids de municipio,
       y las ciudades del contenedor no tenían id), valorEstimado, referencia,
       costeoUltimaConsultaAt y los conexos marcados como no incluidos.

   Todo corre en UNA transacción: si algo falla, no queda nada aplicado. Si ya se
   aplicó, no hace nada.

   Requisitos: 0001 a 0007 aplicados; nivel de compatibilidad de la base >= 130 (usa
   OPENJSON).

   Cómo ejecutarlo (-b detiene el script en el primer error):
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -b -i 0009_reestructuracion_cotizaciones.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

-- =============================================================================
-- 0. Precondiciones
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL
BEGIN
    PRINT N'0009 ya está aplicado: no hay nada que hacer.';
    SET NOEXEC ON;
END
GO

IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene la tabla Cotizaciones: aplique primero 0001 a 0007.', 1;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenMunicipioId') IS NULL
   OR COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'versionNegocio') IS NULL
    THROW 50001, N'Faltan migraciones anteriores: aplique 0006 y 0007 antes del 0009.', 1;
IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
    THROW 50002, N'El 0009 usa OPENJSON y necesita nivel de compatibilidad 130 o superior.', 1;
GO

-- Procedimiento temporal: elimina una columna con sus FK, defaults, checks e índices.
IF OBJECT_ID(N'tempdb..#EliminarColumna') IS NOT NULL
    DROP PROCEDURE #EliminarColumna;
GO
CREATE PROCEDURE #EliminarColumna
    @tabla   SYSNAME,
    @columna SYSNAME
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @tablaCompleta NVARCHAR(300) = N'dbo_cotifly.' + QUOTENAME(@tabla);
    DECLARE @objeto INT = OBJECT_ID(@tablaCompleta, N'U');
    IF @objeto IS NULL OR COL_LENGTH(@tablaCompleta, @columna) IS NULL
        RETURN;

    DECLARE @idColumna INT = COLUMNPROPERTY(@objeto, @columna, 'ColumnId');
    DECLARE @marca NVARCHAR(260) = N'[' + @columna + N']';
    DECLARE @sql NVARCHAR(MAX) = N'';

    SELECT @sql += N'ALTER TABLE ' + @tablaCompleta + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
    FROM sys.foreign_keys AS fk
    WHERE fk.parent_object_id = @objeto
      AND EXISTS (SELECT 1 FROM sys.foreign_key_columns AS fkc
                  WHERE fkc.constraint_object_id = fk.object_id AND fkc.parent_column_id = @idColumna);

    SELECT @sql += N'ALTER TABLE ' + @tablaCompleta + N' DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
    FROM sys.default_constraints AS dc
    WHERE dc.parent_object_id = @objeto AND dc.parent_column_id = @idColumna;

    SELECT @sql += N'ALTER TABLE ' + @tablaCompleta + N' DROP CONSTRAINT ' + QUOTENAME(cc.name) + N';'
    FROM sys.check_constraints AS cc
    WHERE cc.parent_object_id = @objeto
      AND (cc.parent_column_id = @idColumna OR CHARINDEX(@marca, cc.definition) > 0);

    SELECT @sql += CASE WHEN i.is_unique_constraint = 1
                        THEN N'ALTER TABLE ' + @tablaCompleta + N' DROP CONSTRAINT ' + QUOTENAME(i.name) + N';'
                        ELSE N'DROP INDEX ' + QUOTENAME(i.name) + N' ON ' + @tablaCompleta + N';' END
    FROM sys.indexes AS i
    WHERE i.object_id = @objeto AND i.index_id > 0 AND i.is_primary_key = 0
      AND (EXISTS (SELECT 1 FROM sys.index_columns AS ic
                   WHERE ic.object_id = @objeto AND ic.index_id = i.index_id AND ic.column_id = @idColumna)
           OR CHARINDEX(@marca, ISNULL(i.filter_definition, N'')) > 0);

    SET @sql += N'ALTER TABLE ' + @tablaCompleta + N' DROP COLUMN ' + QUOTENAME(@columna) + N';';
    EXEC sys.sp_executesql @sql;
END
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;
GO

-- =============================================================================
-- 1. Respaldo de las tablas que se modifican
--    Se pueden borrar cuando el backend nuevo esté validado.
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

SELECT * INTO dbo_cotifly.Respaldo0009_Cotizaciones FROM dbo_cotifly.Cotizaciones;
SELECT * INTO dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud FROM dbo_cotifly.CotizacionLineasSolicitud;
SELECT * INTO dbo_cotifly.Respaldo0009_CotizacionPosiciones FROM dbo_cotifly.CotizacionPosiciones;
SELECT * INTO dbo_cotifly.Respaldo0009_MargenAjustes FROM dbo_cotifly.MargenAjustes;
SELECT * INTO dbo_cotifly.Respaldo0009_MargenesPolitica FROM dbo_cotifly.MargenesPolitica;
SELECT * INTO dbo_cotifly.Respaldo0009_SapMapeoTrqType FROM dbo_cotifly.SapMapeoTrqType;

-- Filas de otras tablas que se borran con la cotización de prueba de ITR (§2).
SELECT co.* INTO dbo_cotifly.Respaldo0009_Correos
FROM dbo_cotifly.Correos AS co
JOIN dbo_cotifly.Cotizaciones AS c ON c.id = co.cotizacionId
JOIN dbo_cotifly.MargenesPolitica AS mp ON mp.id = c.lineaNegocioId
WHERE mp.lineaNegocio = N'ITR';

SELECT cd.* INTO dbo_cotifly.Respaldo0009_CorreoDestinatarios
FROM dbo_cotifly.CorreoDestinatarios AS cd
JOIN dbo_cotifly.Respaldo0009_Correos AS co ON co.id = cd.correoId;

SELECT a.* INTO dbo_cotifly.Respaldo0009_CotizacionArchivosLicitacion
FROM dbo_cotifly.CotizacionArchivosLicitacion AS a
JOIN dbo_cotifly.Cotizaciones AS c ON c.id = a.cotizacionId
JOIN dbo_cotifly.MargenesPolitica AS mp ON mp.id = c.lineaNegocioId
WHERE mp.lineaNegocio = N'ITR';

SELECT p.* INTO dbo_cotifly.Respaldo0009_CotizacionPdfsHistorial
FROM dbo_cotifly.CotizacionPdfsHistorial AS p
JOIN dbo_cotifly.Cotizaciones AS c ON c.id = p.cotizacionId
JOIN dbo_cotifly.MargenesPolitica AS mp ON mp.id = c.lineaNegocioId
WHERE mp.lineaNegocio = N'ITR';
GO

-- =============================================================================
-- 2. Catálogo de líneas de negocio
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

DECLARE @cotizacionesItr TABLE (id UNIQUEIDENTIFIER PRIMARY KEY);
INSERT INTO @cotizacionesItr (id)
SELECT c.id
FROM dbo_cotifly.Cotizaciones AS c
JOIN dbo_cotifly.MargenesPolitica AS mp ON mp.id = c.lineaNegocioId
WHERE mp.lineaNegocio = N'ITR';

IF (SELECT COUNT(*) FROM @cotizacionesItr) > 1
    THROW 50010, N'Hay más de una cotización con la línea ITR: solo estaba aprobado borrar la cotización de prueba. Revíselas antes de aplicar el 0009.', 1;

DELETE cd FROM dbo_cotifly.CorreoDestinatarios AS cd
JOIN dbo_cotifly.Correos AS co ON co.id = cd.correoId
WHERE co.cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.Correos WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.MargenAjustes WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.CotizacionPosiciones WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.CotizacionArchivosLicitacion WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.CotizacionPdfsHistorial WHERE cotizacionId IN (SELECT id FROM @cotizacionesItr);
DELETE FROM dbo_cotifly.Cotizaciones WHERE id IN (SELECT id FROM @cotizacionesItr);

IF EXISTS (SELECT 1
           FROM dbo_cotifly.Cotizaciones AS c
           JOIN dbo_cotifly.MargenesPolitica AS mp ON mp.id = c.lineaNegocioId
           WHERE mp.lineaNegocio IN (N'Almacenamiento', N'Almacenamiento3PL', N'ITR'))
    THROW 50011, N'Hay cotizaciones con Almacenamiento o Almacenamiento3PL: no estaba previsto. Revíselas antes de aplicar el 0009.', 1;

DELETE FROM dbo_cotifly.SapMapeoTrqType WHERE lineaNegocio IN (N'Almacenamiento', N'Almacenamiento3PL', N'ITR');
DELETE FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio IN (N'Almacenamiento', N'Almacenamiento3PL', N'ITR');

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'DevolucionContenedor')
    INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive, mapeoLineasNexo)
    VALUES (N'DevolucionContenedor', 0.15, N'margen', 1, N'DevolucionContenedor');
-- SapMapeoTrqType de DevolucionContenedor: pendiente del código real de SAP (no se inventa).
GO

-- =============================================================================
-- 3. CotizacionLineasSolicitud (la ruta) — columnas nuevas
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD
    lineaNegocioId              INT            NULL,
    ciudadDevolucionMunicipioId INT            NULL,
    ciudadLevanteMunicipioId    INT            NULL,
    costoFletesTotal            DECIMAL(18,2)  NULL,
    costoRetornoContenedorTotal DECIMAL(18,2)  NULL,
    costoLevanteContenedorTotal DECIMAL(18,2)  NULL,
    costoServiciosConexos       DECIMAL(18,2)  NULL,
    costoFinal                  DECIMAL(18,2)  NULL,
    margenPoliticaSugerido      DECIMAL(9,4)   NULL,
    margenSAPSugerido           DECIMAL(9,4)   NULL,
    margenAplicado              DECIMAL(9,4)   NULL,
    origenMargen                NVARCHAR(20)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    metodoCalculo               NVARCHAR(20)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

-- Las horas por línea ya las agrega el 0008_horas_por_linea.sql (HU-013.11). Se agregan solo si faltan,
-- para que este script corra igual con o sin ese 0008 aplicado.
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasCargue') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD horasCargue DECIMAL(6,2) NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasDescargue') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD horasDescargue DECIMAL(6,2) NULL;
GO

-- =============================================================================
-- 4. CotizacionVehiculosLineaSolicitud — un camión por fila
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

CREATE TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud (
    id                      UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_id DEFAULT (NEWID()),
    lineaSolicitudId        UNIQUEIDENTIFIER NOT NULL,
    idRecomendacionVehiculo UNIQUEIDENTIFIER NULL,  -- fila de la Tabla 7 del cubicador; NULL si el camión no salió de una recomendación
    tipoVehiculo            NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- código (3S3, 2S2...); NULL = sin selección automática
    carroceria              NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    pesoKg                  DECIMAL(12,2)    NULL,
    costoFlete              DECIMAL(18,2)    NULL,
    costoRetornoContenedor  DECIMAL(18,2)    NULL,
    costoLevanteContenedor  DECIMAL(18,2)    NULL,  -- copia de CotizacionLineasSolicitud.valorLevanteContenedor
    lineaSolicitudNexoId    NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    estadoCosteo            NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt               DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt               DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionVehiculosLineaSolicitud PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionVehiculosLineaSolicitud_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_montos CHECK (
        (pesoKg IS NULL OR pesoKg >= 0)
        AND (costoFlete IS NULL OR costoFlete >= 0)
        AND (costoRetornoContenedor IS NULL OR costoRetornoContenedor >= 0)
        AND (costoLevanteContenedor IS NULL OR costoLevanteContenedor >= 0)),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'))
);

CREATE INDEX ix_cotizacionVehiculosLineaSolicitud_lineaSolicitudId
    ON dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId);
GO

-- =============================================================================
-- 5. CotizacionServiciosConexos — conexos de cada ruta
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

CREATE TABLE dbo_cotifly.CotizacionServiciosConexos (
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionServiciosConexos_id DEFAULT (NEWID()),
    lineaSolicitudId  UNIQUEIDENTIFIER NOT NULL,
    idServicioConexo  NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,  -- id del catálogo de Nexo
    precio            DECIMAL(18,2)    NOT NULL,  -- unitario, al momento de aplicarlo (costo, antes del margen)
    cantidad          DECIMAL(10,2)    NOT NULL,
    createdAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionServiciosConexos PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionServiciosConexos_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionServiciosConexos_lineaSolicitudId_idServicioConexo UNIQUE (lineaSolicitudId, idServicioConexo),
    CONSTRAINT ck_cotizacionServiciosConexos_montos CHECK (precio >= 0 AND cantidad > 0)
);
GO

-- =============================================================================
-- 6. Columnas nuevas en CotizacionPosiciones y MargenAjustes
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

-- Id del tipo de mercancía en el catálogo del cubicador (tiposMercancia). NULL en las
-- filas migradas; el backend lo exige para las nuevas.
ALTER TABLE dbo_cotifly.CotizacionPosiciones ADD idTipoMercancia UNIQUEIDENTIFIER NULL;

ALTER TABLE dbo_cotifly.MargenAjustes ADD lineaSolicitudId UNIQUEIDENTIFIER NULL;
GO

IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

ALTER TABLE dbo_cotifly.MargenAjustes
    ADD CONSTRAINT fk_margenAjustes_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id);

CREATE INDEX ix_margenAjustes_lineaSolicitudId ON dbo_cotifly.MargenAjustes (lineaSolicitudId)
    WHERE lineaSolicitudId IS NOT NULL;
GO

-- =============================================================================
-- 7. Migración de datos
-- =============================================================================

-- 7.1 Una ruta para cada cotización que no tiene ninguna (las del agente de chat).
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (
    cotizacionId, orden, filaOrigen, origen, destino, tipoVehiculo, carroceria, tipologia, claseViaje, claseContenedor,
    exclusivo, consolidado, retornaContenedor, ciudadDevolucionContenedor, levanteContenedorDiferente, ciudadLevanteContenedor,
    valorAprovisionamiento, pesoKg, valorMercancia, lineaSolicitudNexoId, estadoCosteo, costo, precioFinal, serviciosConexos,
    createdAt, updatedAt)
SELECT
    c.id, 1, 0, ISNULL(c.origen, N''), ISNULL(c.destino, N''), c.tipoVehiculo, c.carroceria, c.tipologia, c.claseViaje,
    c.claseContenedor, c.exclusivo, c.consolidado, c.retornaContenedor, c.ciudadDevolucionContenedor,
    c.levanteContenedorDiferente, c.ciudadLevanteContenedor, c.valorAprovisionamiento, c.pesoKg, c.valorMercancia, NULL,
    c.estadoCosteo,
    CASE WHEN c.costo >= 0 THEN c.costo END,
    CASE WHEN c.precioFinal >= 0 THEN c.precioFinal END,
    NULL,
    c.createdAt, c.updatedAt
FROM dbo_cotifly.Cotizaciones AS c
WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud AS l WHERE l.cotizacionId = c.id);
GO

-- 7.2 Línea de negocio, horas, tipología y margen de la cabecera a cada ruta.
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

UPDATE l
SET l.lineaNegocioId         = c.lineaNegocioId,
    l.horasCargue            = COALESCE(l.horasCargue, c.horasCargue),        -- no pisa las horas propias que ya guardó el 0008_horas_por_linea
    l.horasDescargue         = COALESCE(l.horasDescargue, c.horasDescargue),
    l.tipologia              = COALESCE(l.tipologia, c.tipologia),
    l.margenAplicado         = c.margenAplicado,
    l.origenMargen           = CASE WHEN c.origenMargen IN (N'sap_tm', N'politica_linea', N'negociado') THEN c.origenMargen END,
    l.metodoCalculo          = LEFT(c.metodoCalculo, 20),
    l.margenPoliticaSugerido = CASE WHEN c.origenMargenSugerido = N'politica_linea' THEN c.margenSugerido ELSE c.margenPolitica END,
    l.margenSAPSugerido      = CASE WHEN c.origenMargenSugerido = N'sap_tm' THEN c.margenSugerido END
FROM dbo_cotifly.CotizacionLineasSolicitud AS l
JOIN dbo_cotifly.Cotizaciones AS c ON c.id = l.cotizacionId;

-- rutaOrigenIndice (todavía filaOrigen): 0 en las cotizaciones de una sola ruta.
UPDATE l
SET l.filaOrigen = 0
FROM dbo_cotifly.CotizacionLineasSolicitud AS l
WHERE l.filaOrigen IS NULL
  AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud AS o
                  WHERE o.cotizacionId = l.cotizacionId AND o.id <> l.id);

-- retornaContenedor / levanteContenedorDiferente dejan de aceptar vacío (§8).
UPDATE dbo_cotifly.CotizacionLineasSolicitud
SET retornaContenedor          = ISNULL(retornaContenedor, 0),
    levanteContenedorDiferente = ISNULL(levanteContenedorDiferente, 0)
WHERE retornaContenedor IS NULL OR levanteContenedorDiferente IS NULL;
GO

-- 7.3 Un camión por cada ruta, con lo que tenía la línea.
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (
    lineaSolicitudId, idRecomendacionVehiculo, tipoVehiculo, carroceria, pesoKg,
    costoFlete, costoRetornoContenedor, costoLevanteContenedor, lineaSolicitudNexoId, estadoCosteo, createdAt, updatedAt)
SELECT
    l.id, NULL, l.tipoVehiculo, l.carroceria, l.pesoKg,
    CASE WHEN l.costo IS NULL THEN NULL
         WHEN l.costo >= ISNULL(l.valorAprovisionamiento, 0) THEN l.costo - ISNULL(l.valorAprovisionamiento, 0)
         ELSE l.costo END,
    CASE WHEN l.costo IS NULL THEN NULL ELSE 0 END,
    CASE WHEN l.costo IS NOT NULL AND l.costo < ISNULL(l.valorAprovisionamiento, 0) THEN 0
         ELSE ISNULL(l.valorAprovisionamiento, 0) END,
    l.lineaSolicitudNexoId, l.estadoCosteo, l.createdAt, l.updatedAt
FROM dbo_cotifly.CotizacionLineasSolicitud AS l
WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud AS v WHERE v.lineaSolicitudId = l.id);
GO

-- 7.4 Conexos incluidos de los JSON (cabecera → ruta 1; línea → su ruta).
--     precio = costo / cantidad, para que precio × cantidad dé el costo que había.
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

WITH fuentes AS (
    SELECT l.id AS lineaSolicitudId, j.idServicio, j.cantidad, j.costo, j.incluido, c.updatedAt AS fecha, 1 AS prioridad
    FROM dbo_cotifly.Cotizaciones AS c
    JOIN dbo_cotifly.CotizacionLineasSolicitud AS l ON l.cotizacionId = c.id AND l.orden = 1
    CROSS APPLY OPENJSON(c.serviciosConexos)
        WITH (idServicio NVARCHAR(64) '$.id', cantidad DECIMAL(10,2) '$.cantidad',
              costo DECIMAL(18,2) '$.costo', incluido BIT '$.incluido') AS j
    UNION ALL
    SELECT l.id, j.idServicio, j.cantidad, j.costo, j.incluido, l.updatedAt, 2
    FROM dbo_cotifly.CotizacionLineasSolicitud AS l
    CROSS APPLY OPENJSON(l.serviciosConexos)
        WITH (idServicio NVARCHAR(64) '$.id', cantidad DECIMAL(10,2) '$.cantidad',
              costo DECIMAL(18,2) '$.costo', incluido BIT '$.incluido') AS j
),
unicos AS (
    SELECT f.*, ROW_NUMBER() OVER (PARTITION BY f.lineaSolicitudId, f.idServicio ORDER BY f.prioridad) AS n
    FROM fuentes AS f
    WHERE f.idServicio IS NOT NULL AND f.costo >= 0 AND ISNULL(f.incluido, 1) = 1
)
INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, precio, cantidad, createdAt, updatedAt)
SELECT u.lineaSolicitudId, u.idServicio,
       CASE WHEN u.cantidad > 0 THEN ROUND(u.costo / u.cantidad, 2) ELSE u.costo END,
       CASE WHEN u.cantidad > 0 THEN u.cantidad ELSE 1 END,
       u.fecha, u.fecha
FROM unicos AS u
WHERE u.n = 1
  AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionServiciosConexos AS s
                  WHERE s.lineaSolicitudId = u.lineaSolicitudId AND s.idServicioConexo = u.idServicio);
GO

-- 7.5 Ajustes de margen y posiciones sin ruta → ruta 1 de su cotización.
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

UPDATE a
SET a.lineaSolicitudId = l.id
FROM dbo_cotifly.MargenAjustes AS a
JOIN dbo_cotifly.CotizacionLineasSolicitud AS l ON l.cotizacionId = a.cotizacionId AND l.orden = 1
WHERE a.lineaSolicitudId IS NULL;

UPDATE p
SET p.lineaSolicitudId = l.id
FROM dbo_cotifly.CotizacionPosiciones AS p
JOIN dbo_cotifly.CotizacionLineasSolicitud AS l ON l.cotizacionId = p.cotizacionId AND l.orden = 1
WHERE p.lineaSolicitudId IS NULL;

IF EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionPosiciones WHERE lineaSolicitudId IS NULL)
    THROW 50012, N'Quedaron posiciones sin ruta: revise CotizacionPosiciones antes de aplicar el 0009.', 1;
GO

-- 7.6 Totales de cada ruta (§8 de la propuesta). precioFinalLinea y el precioFinal de la
--     cabecera se conservan como estaban; el backend los recalcula en el siguiente cambio.
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

UPDATE l
SET l.costoFletesTotal            = v.flete,
    l.costoRetornoContenedorTotal = v.retorno,
    l.costoLevanteContenedorTotal = v.levante,
    l.costoServiciosConexos       = ISNULL(s.total, 0),
    l.costoFinal                  = CASE WHEN v.flete IS NOT NULL AND v.retorno IS NOT NULL AND v.levante IS NOT NULL
                                         THEN v.flete + v.retorno + v.levante + ISNULL(s.total, 0) END
FROM dbo_cotifly.CotizacionLineasSolicitud AS l
OUTER APPLY (
    SELECT CASE WHEN COUNT(*) > 0 AND COUNT(x.costoFlete) = COUNT(*) THEN SUM(x.costoFlete) END AS flete,
           CASE WHEN COUNT(*) > 0 AND COUNT(x.costoRetornoContenedor) = COUNT(*) THEN SUM(x.costoRetornoContenedor) END AS retorno,
           CASE WHEN COUNT(*) > 0 AND COUNT(x.costoLevanteContenedor) = COUNT(*) THEN SUM(x.costoLevanteContenedor) END AS levante
    FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud AS x
    WHERE x.lineaSolicitudId = l.id
) AS v
OUTER APPLY (
    SELECT SUM(y.precio * y.cantidad) AS total
    FROM dbo_cotifly.CotizacionServiciosConexos AS y
    WHERE y.lineaSolicitudId = l.id
) AS s;
GO

-- =============================================================================
-- 8. CotizacionLineasSolicitud — quitar, renombrar y restricciones nuevas
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'origen';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'destino';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'tipoVehiculo';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'carroceria';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'ciudadDevolucionContenedor';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'ciudadLevanteContenedor';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'lineaSolicitudNexoId';
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'costo';             -- arrastra ck_cotizacionLineasSolicitud_montos
EXEC #EliminarColumna N'CotizacionLineasSolicitud', N'serviciosConexos';

EXEC sys.sp_rename N'dbo_cotifly.CotizacionLineasSolicitud.cotizacionId', N'cotizacionSolicitudId', N'COLUMN';
EXEC sys.sp_rename N'dbo_cotifly.CotizacionLineasSolicitud.filaOrigen', N'rutaOrigenIndice', N'COLUMN';
EXEC sys.sp_rename N'dbo_cotifly.CotizacionLineasSolicitud.precioFinal', N'precioFinalLinea', N'COLUMN';
EXEC sys.sp_rename N'dbo_cotifly.CotizacionLineasSolicitud.valorAprovisionamiento', N'valorLevanteContenedor', N'COLUMN';

ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ALTER COLUMN retornaContenedor BIT NOT NULL;
ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ALTER COLUMN levanteContenedorDiferente BIT NOT NULL;
GO

IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD
    CONSTRAINT df_cotizacionLineasSolicitud_retornaContenedor DEFAULT (0) FOR retornaContenedor,
    CONSTRAINT df_cotizacionLineasSolicitud_levanteContenedorDiferente DEFAULT (0) FOR levanteContenedorDiferente;

-- Si el 0008_horas_por_linea ya creó esta restricción (mismo nombre), se reemplaza por la misma regla (0 a 240 h, el límite de Nexo).
IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_horas', N'C') IS NOT NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_horas;

ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD
    CONSTRAINT fk_cotizacionLineasSolicitud_lineaNegocioId FOREIGN KEY (lineaNegocioId)
        REFERENCES dbo_cotifly.MargenesPolitica (id),
    CONSTRAINT ck_cotizacionLineasSolicitud_costos CHECK (
        (valorLevanteContenedor IS NULL OR valorLevanteContenedor >= 0)
        AND (costoFletesTotal IS NULL OR costoFletesTotal >= 0)
        AND (costoRetornoContenedorTotal IS NULL OR costoRetornoContenedorTotal >= 0)
        AND (costoLevanteContenedorTotal IS NULL OR costoLevanteContenedorTotal >= 0)
        AND (costoServiciosConexos IS NULL OR costoServiciosConexos >= 0)
        AND (costoFinal IS NULL OR costoFinal >= 0)
        AND (precioFinalLinea IS NULL OR precioFinalLinea >= 0)),
    CONSTRAINT ck_cotizacionLineasSolicitud_horas CHECK (
        (horasCargue IS NULL OR (horasCargue >= 0 AND horasCargue <= 240))
        AND (horasDescargue IS NULL OR (horasDescargue >= 0 AND horasDescargue <= 240))),
    CONSTRAINT ck_cotizacionLineasSolicitud_origenMargen CHECK (
        origenMargen IS NULL OR origenMargen IN ('sap_tm', 'politica_linea', 'negociado')),
    CONSTRAINT ck_cotizacionLineasSolicitud_municipiosContenedor CHECK (
        (ciudadDevolucionMunicipioId IS NULL OR ciudadDevolucionMunicipioId > 0)
        AND (ciudadLevanteMunicipioId IS NULL OR ciudadLevanteMunicipioId > 0));
GO

-- =============================================================================
-- 9. CotizacionPosiciones → CotizacionMercancias (cuelga solo de la ruta)
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

ALTER TABLE dbo_cotifly.CotizacionPosiciones DROP CONSTRAINT fk_cotizacionPosiciones_lineaSolicitudId;
DROP INDEX ix_cotizacionPosiciones_lineaSolicitudId ON dbo_cotifly.CotizacionPosiciones;
ALTER TABLE dbo_cotifly.CotizacionPosiciones ALTER COLUMN lineaSolicitudId UNIQUEIDENTIFIER NOT NULL;

-- Quita cotizacionId con su FK y el UNIQUE (cotizacionId, orden).
EXEC #EliminarColumna N'CotizacionPosiciones', N'cotizacionId';

EXEC sys.sp_rename N'dbo_cotifly.CotizacionPosiciones.descripcion', N'nombreMercancia', N'COLUMN';
GO

IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

ALTER TABLE dbo_cotifly.CotizacionPosiciones WITH CHECK ADD
    CONSTRAINT fk_cotizacionMercancias_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionMercancias_lineaSolicitudId_orden UNIQUE (lineaSolicitudId, orden);

EXEC sys.sp_rename N'dbo_cotifly.CotizacionPosiciones', N'CotizacionMercancias', N'OBJECT';
GO

-- =============================================================================
-- 10. Cotizaciones → CotizacionesSolicitud (la cabecera)
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

EXEC #EliminarColumna N'Cotizaciones', N'lineaNegocioId';        -- arrastra fk_cotizaciones_lineaNegocioId
EXEC #EliminarColumna N'Cotizaciones', N'costeoId';
EXEC #EliminarColumna N'Cotizaciones', N'costo';
EXEC #EliminarColumna N'Cotizaciones', N'margenPolitica';
EXEC #EliminarColumna N'Cotizaciones', N'margenAplicado';
EXEC #EliminarColumna N'Cotizaciones', N'origenMargen';
EXEC #EliminarColumna N'Cotizaciones', N'metodoCalculo';
EXEC #EliminarColumna N'Cotizaciones', N'valorEstimado';
EXEC #EliminarColumna N'Cotizaciones', N'referencia';
EXEC #EliminarColumna N'Cotizaciones', N'horasCargue';
EXEC #EliminarColumna N'Cotizaciones', N'horasDescargue';
EXEC #EliminarColumna N'Cotizaciones', N'origen';
EXEC #EliminarColumna N'Cotizaciones', N'destino';
EXEC #EliminarColumna N'Cotizaciones', N'tipoVehiculo';
EXEC #EliminarColumna N'Cotizaciones', N'carroceria';
EXEC #EliminarColumna N'Cotizaciones', N'tipologia';
EXEC #EliminarColumna N'Cotizaciones', N'claseViaje';
EXEC #EliminarColumna N'Cotizaciones', N'claseContenedor';
EXEC #EliminarColumna N'Cotizaciones', N'exclusivo';
EXEC #EliminarColumna N'Cotizaciones', N'consolidado';
EXEC #EliminarColumna N'Cotizaciones', N'retornaContenedor';
EXEC #EliminarColumna N'Cotizaciones', N'ciudadDevolucionContenedor';
EXEC #EliminarColumna N'Cotizaciones', N'levanteContenedorDiferente';
EXEC #EliminarColumna N'Cotizaciones', N'ciudadLevanteContenedor';
EXEC #EliminarColumna N'Cotizaciones', N'valorAprovisionamiento';
EXEC #EliminarColumna N'Cotizaciones', N'pesoKg';
EXEC #EliminarColumna N'Cotizaciones', N'valorMercancia';
EXEC #EliminarColumna N'Cotizaciones', N'serviciosConexos';      -- arrastra ck_cotizaciones_serviciosConexos_json
EXEC #EliminarColumna N'Cotizaciones', N'costeoUltimaConsultaAt';
EXEC #EliminarColumna N'Cotizaciones', N'margenSugerido';
EXEC #EliminarColumna N'Cotizaciones', N'origenMargenSugerido';  -- arrastra ck_cotizaciones_origenMargenSugerido

-- Las FK de Correos, MargenAjustes, CotizacionArchivosLicitacion, CotizacionPdfsHistorial y
-- CotizacionLineasSolicitud siguen apuntando a la tabla con su nombre nuevo.
EXEC sys.sp_rename N'dbo_cotifly.Cotizaciones', N'CotizacionesSolicitud', N'OBJECT';
GO

-- =============================================================================
-- 11. Vistas
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

EXEC (N'
CREATE OR ALTER VIEW dbo_cotifly.vAjustesMargenPorComercial AS
SELECT
    a.usuarioId,
    mp.lineaNegocio,
    COUNT(*)                                            AS numAjustes,
    AVG(a.diferencia)                                   AS diferenciaPromedio,
    MIN(a.diferencia)                                   AS diferenciaMin,
    MAX(a.diferencia)                                   AS diferenciaMax,
    MAX(a.createdAt)                                    AS ultimoAjuste,
    SUM(CASE WHEN a.diferencia < 0 THEN 1 ELSE 0 END)   AS numAjustesBajoSugerido
FROM dbo_cotifly.MargenAjustes a
LEFT JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = a.lineaSolicitudId
LEFT JOIN dbo_cotifly.MargenesPolitica mp ON mp.id = l.lineaNegocioId
GROUP BY a.usuarioId, mp.lineaNegocio;');

EXEC (N'
CREATE OR ALTER VIEW dbo_cotifly.vActividadPorComercial AS
SELECT
    c.usuarioId,
    COUNT(DISTINCT c.id)                                                        AS numCotizaciones,
    SUM(CASE WHEN c.estadoEnvio IN (''enviada'', ''reenviada'') THEN 1 ELSE 0 END)  AS numCotizacionesEnviadas,
    COALESCE(SUM(sub.numAjustes), 0)                                            AS numAjustes,
    MAX(c.createdAt)                                                            AS ultimaCotizacion
FROM dbo_cotifly.CotizacionesSolicitud c
LEFT JOIN (
    SELECT cotizacionId, COUNT(*) AS numAjustes
    FROM dbo_cotifly.MargenAjustes
    WHERE cotizacionId IS NOT NULL
    GROUP BY cotizacionId
) sub ON sub.cotizacionId = c.id
WHERE c.usuarioId IS NOT NULL
GROUP BY c.usuarioId;');
GO

-- =============================================================================
-- 12. Confirmar
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0009 se abortó: revise el error anterior. No se aplicó nada.', 1;

COMMIT TRANSACTION;
PRINT N'0009 aplicado.';
GO

SET NOEXEC OFF;
GO

/* ============================================================================
   Verificación manual sugerida tras ejecutar:

     -- Cada cotización tiene al menos una ruta, y cada ruta al menos un camión.
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud c
     WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = c.id);   -- 0
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l
     WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id); -- 0

     -- Los costos migrados cuadran con el costo viejo de cada línea (diferencias = 0).
     SELECT r.id, r.costo, l.costoFletesTotal + l.costoRetornoContenedorTotal + l.costoLevanteContenedorTotal AS costoNuevo
     FROM dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud r
     JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = r.id
     WHERE r.costo IS NOT NULL
       AND r.costo <> l.costoFletesTotal + l.costoRetornoContenedorTotal + l.costoLevanteContenedorTotal;

     -- Catálogo de líneas de negocio: 6 filas, con DevolucionContenedor y sin ITR/Almacenamiento.
     SELECT lineaNegocio, mapeoLineasNexo, isActive FROM dbo_cotifly.MargenesPolitica ORDER BY lineaNegocio;

     -- Las vistas responden.
     SELECT TOP (5) * FROM dbo_cotifly.vAjustesMargenPorComercial;
     SELECT TOP (5) * FROM dbo_cotifly.vActividadPorComercial;

   Cuando el backend nuevo esté validado, los respaldos se borran con:
     DROP TABLE dbo_cotifly.Respaldo0009_Cotizaciones, dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud,
                dbo_cotifly.Respaldo0009_CotizacionPosiciones, dbo_cotifly.Respaldo0009_MargenAjustes,
                dbo_cotifly.Respaldo0009_MargenesPolitica, dbo_cotifly.Respaldo0009_SapMapeoTrqType,
                dbo_cotifly.Respaldo0009_Correos, dbo_cotifly.Respaldo0009_CorreoDestinatarios,
                dbo_cotifly.Respaldo0009_CotizacionArchivosLicitacion, dbo_cotifly.Respaldo0009_CotizacionPdfsHistorial;
   ============================================================================ */
