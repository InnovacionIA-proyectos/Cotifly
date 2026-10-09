/* ============================================================================
   Cotifly — 0005: líneas de la solicitud de Nexo (licitaciones multi-línea)
   ============================================================================
   HU-011.2 (#47), base de HU-011.3 (#48) y HU-011.5 (#49). Patrón de scripts:
   backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First: el esquema
   SOLO cambia con scripts `db/migrations_mssql/000N_*.sql`; no hay migraciones
   EF). Diseño: backend-dotnet/specs/decisiones/ADR-017 (amplía ADR-015). Formato
   del Excel: docs/conexion_nexo/HU-011-formato-excel-licitacion.md.

   *** PENDIENTE DE APROBACIÓN — igual que el 0002, el 0003 y el 0004, no ejecutar
       en tbsdatacenter sin el visto bueno de scalderon-bs y del DBA ***
   Redacta: Duban (backend).

   Por qué: el 0004 dejó los campos de la LÍNEA de la solicitud de Nexo en
   Cotizaciones (una cotización = una solicitud con UNA línea) y anotó la deuda
   consciente para HU-011. Una licitación es UNA solicitud de Nexo con N líneas,
   cada una con su costeo y su precio.

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. CotizacionLineasSolicitud (1 a N con Cotizaciones): los campos de
        `CapturaLineaRequest` (origen ... valorMercancia), el orden en que viajó a
        Nexo, la fila del Excel (filaOrigen, índice base 0 de `hojas[h].filas` del
        análisis), el id de la línea en Nexo (lineaSolicitudNexoId), su estado de
        costeo (mismos 7 códigos que Cotizaciones.estadoCosteo), su costo de Nexo
        (interno), su precio con margen y serviciosConexos (JSON, reservado: Nexo
        guarda los conexos por cotización; pendiente de Gerencia Comercial si se
        cotizan por línea).
     2. CotizacionPosiciones.lineaSolicitudId: FK nullable a la línea. Las filas
        existentes siguen válidas (la FK nula es compatible) y el backfill las
        enlaza a su línea.
     3. CotizacionArchivosLicitacion (1 a 1 con Cotizaciones): el Excel original
        del pliego (máximo 10 MB, el mismo límite de Nexo) para regenerarlo con los
        precios sin tocar sus columnas (HU-011.3). Solo lo lee el dueño de la
        cotización (lo controla el backend, como el resto de /web/cotizaciones).
     4. Backfill: una línea (orden 1) por cada cotización que ya tiene datos del
        formulario ampliado (tipoSolicitud, origen y destino), con su costo y precio
        si el costeo está completo (autocosteo o entregada), y sus posiciones
        enlazadas a esa línea.

   Columnas OBSOLETAS (no se borran en este script, no destructivo): los campos de
   línea de Cotizaciones (origen, destino, tipoVehiculo, carroceria, tipologia,
   claseViaje, claseContenedor, exclusivo, consolidado, retornaContenedor,
   ciudadDevolucionContenedor, levanteContenedorDiferente,
   ciudadLevanteContenedor, valorAprovisionamiento, pesoKg, valorMercancia).
     - En una cotización de UNA línea (formulario de HU-005.5) el backend las sigue
       escribiendo como espejo de la línea 1: métricas, "Mis cotizaciones" y el PDF
       de una línea no cambian.
     - En una licitación quedan en NULL (no hay "la" línea).
     - Un script futuro (000N) las elimina cuando ningún consumidor las lea.
   Los campos del ENCABEZADO (tipoSolicitud, deadline, valorEstimado, referencia,
   horasCargue, horasDescargue) y los de seguimiento siguen en Cotizaciones.

   Idempotente y NO destructivo: cada CREATE/ADD va guardado con comprobaciones de
   existencia y el backfill solo inserta líneas que faltan (NOT EXISTS); se puede
   correr varias veces sin cambiar nada.

   Precondición: 0001, 0002 y 0004 aplicados (el 0003 no es requisito).

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0005_lineas_solicitud.sql
   Las pruebas de integración del backend .NET (Testcontainers) aplican 0001 a 0005
   en orden, partidos por `GO`, y comparan el modelo EF con el esquema.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

IF OBJECT_ID(N'dbo_cotifly.CotizacionPosiciones', N'U') IS NULL OR COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'serviciosConexos') IS NULL
    THROW 50001, 'dbo_cotifly no tiene el 0004: aplique primero 0001, 0002 y 0004_costeo_nexo.sql.', 1;
GO

-- =============================================================================
-- 1. dbo_cotifly.CotizacionLineasSolicitud — líneas de la solicitud de Nexo
--    Mismos tipos y largos que las columnas de línea del 0004 (y que
--    CapturaLineaRequest de Nexo). Los catálogos guardan el código de Nexo tal
--    cual, sin CHECK (Nexo es la fuente de esos valores).
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CotizacionLineasSolicitud (
        id                          UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_id DEFAULT (NEWID()),
        cotizacionId                UNIQUEIDENTIFIER NOT NULL,
        orden                       INT              NOT NULL,
        filaOrigen                  INT              NULL,
        origen                      NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        destino                     NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        tipoVehiculo                NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        carroceria                  NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        tipologia                   NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        claseViaje                  NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        claseContenedor             NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        exclusivo                   BIT              NULL,
        consolidado                 BIT              NULL,
        retornaContenedor           BIT              NULL,
        ciudadDevolucionContenedor  NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        levanteContenedorDiferente  BIT              NULL,
        ciudadLevanteContenedor     NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        valorAprovisionamiento      DECIMAL(18,2)    NULL,
        pesoKg                      DECIMAL(12,2)    NULL,
        valorMercancia              DECIMAL(18,2)    NULL,
        lineaSolicitudNexoId        NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        estadoCosteo                NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        costo                       DECIMAL(18,2)    NULL,
        precioFinal                 DECIMAL(18,2)    NULL,
        serviciosConexos            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        createdAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        updatedAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizacionLineasSolicitud PRIMARY KEY (id),
        CONSTRAINT fk_cotizacionLineasSolicitud_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT uq_cotizacionLineasSolicitud_cotizacionId_orden UNIQUE (cotizacionId, orden),
        CONSTRAINT ck_cotizacionLineasSolicitud_orden CHECK (orden >= 1),
        CONSTRAINT ck_cotizacionLineasSolicitud_filaOrigen CHECK (filaOrigen IS NULL OR filaOrigen >= 0),
        CONSTRAINT ck_cotizacionLineasSolicitud_montos CHECK ((costo IS NULL OR costo >= 0) AND (precioFinal IS NULL OR precioFinal >= 0)),
        CONSTRAINT ck_cotizacionLineasSolicitud_serviciosConexos_json CHECK (serviciosConexos IS NULL OR ISJSON(serviciosConexos) = 1),
        CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
            ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'))
    );
END
GO

-- =============================================================================
-- 2. CotizacionPosiciones.lineaSolicitudId — cada bulto pertenece a una línea
--    `orden` sigue siendo único por cotización (uq del 0004): las posiciones de la
--    línea N siguen numeradas después de las de la línea N - 1.
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.CotizacionPosiciones', N'lineaSolicitudId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionPosiciones ADD lineaSolicitudId UNIQUEIDENTIFIER NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'fk_cotizacionPosiciones_lineaSolicitudId'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionPosiciones'))
    ALTER TABLE dbo_cotifly.CotizacionPosiciones
        ADD CONSTRAINT fk_cotizacionPosiciones_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
            REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id);
GO

-- Posiciones de una línea (reenvío y PDF): índice filtrado sobre la FK nueva.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cotizacionPosiciones_lineaSolicitudId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.CotizacionPosiciones'))
    CREATE INDEX ix_cotizacionPosiciones_lineaSolicitudId ON dbo_cotifly.CotizacionPosiciones (lineaSolicitudId)
        WHERE lineaSolicitudId IS NOT NULL;
GO

-- =============================================================================
-- 3. dbo_cotifly.CotizacionArchivosLicitacion — Excel original del pliego
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionArchivosLicitacion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CotizacionArchivosLicitacion (
        cotizacionId    UNIQUEIDENTIFIER NOT NULL,
        nombreArchivo   NVARCHAR(255) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        tipoContenido   NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        tamanoBytes     INT              NOT NULL,
        sha256          NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        hoja            NVARCHAR(31)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        contenido       VARBINARY(MAX)   NOT NULL,
        createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionArchivosLicitacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizacionArchivosLicitacion PRIMARY KEY (cotizacionId),
        CONSTRAINT fk_cotizacionArchivosLicitacion_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT ck_cotizacionArchivosLicitacion_tamano CHECK (tamanoBytes BETWEEN 1 AND 10485760 AND DATALENGTH(contenido) = tamanoBytes)
    );
END
GO

-- =============================================================================
-- 4. Backfill: una línea por cotización del formulario ampliado (HU-005.5)
--    Solo inserta las que faltan: re-aplicar el script no duplica nada.
-- =============================================================================
INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (
    cotizacionId, orden, filaOrigen, origen, destino, tipoVehiculo, carroceria, tipologia, claseViaje, claseContenedor,
    exclusivo, consolidado, retornaContenedor, ciudadDevolucionContenedor, levanteContenedorDiferente, ciudadLevanteContenedor,
    valorAprovisionamiento, pesoKg, valorMercancia, lineaSolicitudNexoId, estadoCosteo, costo, precioFinal, serviciosConexos,
    createdAt, updatedAt)
SELECT
    c.id, 1, NULL, c.origen, c.destino, c.tipoVehiculo, c.carroceria, c.tipologia, c.claseViaje, c.claseContenedor,
    c.exclusivo, c.consolidado, c.retornaContenedor, c.ciudadDevolucionContenedor, c.levanteContenedorDiferente, c.ciudadLevanteContenedor,
    c.valorAprovisionamiento, c.pesoKg, c.valorMercancia, NULL,
    CASE WHEN c.estadoCosteo IN ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido')
         THEN c.estadoCosteo END,
    CASE WHEN c.estadoCosteo IN ('autocosteo', 'entregada') AND c.costo >= 0 THEN c.costo END,
    CASE WHEN c.estadoCosteo IN ('autocosteo', 'entregada') AND c.precioFinal >= 0 THEN c.precioFinal END,
    NULL,
    c.createdAt, c.updatedAt
FROM dbo_cotifly.Cotizaciones AS c
WHERE c.tipoSolicitud IS NOT NULL
  AND c.origen IS NOT NULL
  AND c.destino IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionLineasSolicitud AS l WHERE l.cotizacionId = c.id);
GO

-- Posiciones existentes → la línea 1 de su cotización (solo las que no tienen línea).
UPDATE p
SET p.lineaSolicitudId = l.id
FROM dbo_cotifly.CotizacionPosiciones AS p
INNER JOIN dbo_cotifly.CotizacionLineasSolicitud AS l ON l.cotizacionId = p.cotizacionId AND l.orden = 1
WHERE p.lineaSolicitudId IS NULL;
GO

/* Verificación manual (opcional):
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud;
     SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE tipoSolicitud IS NOT NULL AND origen IS NOT NULL;  -- igual al anterior tras el backfill
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones WHERE lineaSolicitudId IS NULL;                    -- 0 tras el backfill
*/
