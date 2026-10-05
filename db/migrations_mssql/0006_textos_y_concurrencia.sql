/* ============================================================================
   Cotifly — 0006: concurrencia optimista, textos del PDF y regla del PDF
   ============================================================================
   HU-013.2 (#98), base de HU-013.3 (#99), HU-013.4 (#100) y HU-013.5 (#101).
   Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database
   First: el esquema SOLO cambia con scripts `db/migrations_mssql/000N_*.sql`; no
   hay migraciones EF). Diseño: backend-dotnet/specs/decisiones/ADR-018 §8
   (concurrencia) y §11 (regla del PDF).

   *** PENDIENTE DE APROBACIÓN — igual que el 0002 a 0005, no ejecutar en
       tbsdatacenter sin el visto bueno de scalderon-bs y del DBA ***
   Redacta: Duban (backend).

   Por qué: el comercial cambia la cotización después del costeo (margen,
   servicios conexos, textos del PDF, re-solicitud) mientras el polling de Nexo
   la sigue escribiendo, y el asistente propone acciones que se confirman más
   tarde. Sin una versión de la fila no hay forma de saber si lo que se confirma
   sigue vigente.

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. Cotizaciones.rowVersion (ROWVERSION, NOT NULL por definición): versión de
        la fila que SQL Server cambia en cada UPDATE. Viaja al frontend como ETag
        (base64) y vuelve en If-Match. Al agregarla, SQL Server la llena en todas
        las filas existentes (operación del tamaño de la tabla).
     2. Cotizaciones.textosCotizacion (JSON, NULL): asunto e introducción del PDF
        guardados (HU-013.5, #101). CHECK ISJSON.
     3. Cotizaciones.pdfDesactualizado (BIT NOT NULL, 0 por defecto): una acción
        posterior al PDF (margen, conexos, textos, re-solicitud) lo invalidó
        (pdfPath = NULL) y el comercial debe regenerarlo.
     4. CotizacionPdfsHistorial: un PDF invalidado por fila (la referencia de su
        archivo, que se conserva en el almacén, la acción que lo invalidó, quién y
        cuándo). La auditoría de la acción queda además en EventosHerramienta.
     5. vAjustesMargenPorComercial + numAjustesBajoSugerido: cuántos ajustes
        quedaron por debajo del margen sugerido (diferencia < 0).

   Idempotente y NO destructivo: cada ADD/CREATE va guardado con comprobaciones
   de existencia; la vista se redefine con CREATE OR ALTER (mismas columnas más
   una). No borra ni modifica datos. Se puede correr varias veces sin cambiar
   nada.

   Precondición: 0001, 0002, 0004 y 0005 aplicados (el 0003 no es requisito,
   aunque la vista que redefine es la del 0001).

   Cómo ejecutarlo (una vez por ambiente, antes de desplegar la versión de #98):
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0006_textos_y_concurrencia.sql
   Las pruebas de integración del backend .NET (Testcontainers) aplican 0001 a 0006
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

IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, 'dbo_cotifly no tiene el 0005: aplique primero 0001, 0002, 0004 y 0005_lineas_solicitud.sql.', 1;
GO

-- =============================================================================
-- 1. Cotizaciones.rowVersion — concurrencia optimista (ADR-018 §8)
--    Una tabla solo admite una columna ROWVERSION: se comprueba por tipo, no
--    solo por nombre.
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'rowVersion') IS NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones')
                   AND system_type_id = TYPE_ID(N'timestamp'))
    ALTER TABLE dbo_cotifly.Cotizaciones ADD rowVersion ROWVERSION;
GO

-- =============================================================================
-- 2. Cotizaciones.textosCotizacion — asunto e introducción del PDF (HU-013.5)
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'textosCotizacion') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD textosCotizacion NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_textosCotizacion_json'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD CONSTRAINT ck_cotizaciones_textosCotizacion_json CHECK (textosCotizacion IS NULL OR ISJSON(textosCotizacion) = 1);
GO

-- =============================================================================
-- 3. Cotizaciones.pdfDesactualizado — regla del PDF (ADR-018 §11)
--    NOT NULL con DEFAULT: las filas existentes quedan en 0 (su PDF, si lo
--    tienen, sigue vigente).
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'pdfDesactualizado') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD pdfDesactualizado BIT NOT NULL CONSTRAINT df_cotizaciones_pdfDesactualizado DEFAULT (0);
GO

-- =============================================================================
-- 4. dbo_cotifly.CotizacionPdfsHistorial — PDF invalidados (ADR-018 §11)
--    pdfPath: la referencia del archivo en el almacén (el archivo NO se borra).
--    motivo: la acción del comercial que lo invalidó.
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CotizacionPdfsHistorial (
        id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionPdfsHistorial_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER NOT NULL,
        pdfPath         NVARCHAR(400) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        motivo          NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        usuarioId       UNIQUEIDENTIFIER NULL,
        createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionPdfsHistorial_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizacionPdfsHistorial PRIMARY KEY (id),
        CONSTRAINT fk_cotizacionPdfsHistorial_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT fk_cotizacionPdfsHistorial_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
        CONSTRAINT ck_cotizacionPdfsHistorial_motivo CHECK (motivo IN ('ajuste_margen', 'servicios_conexos', 'textos', 're_solicitud'))
    );
END
GO

-- PDF anteriores de una cotización (auditoría y regeneración).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cotizacionPdfsHistorial_cotizacionId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial'))
    CREATE INDEX ix_cotizacionPdfsHistorial_cotizacionId ON dbo_cotifly.CotizacionPdfsHistorial (cotizacionId);
GO

-- =============================================================================
-- 5. vAjustesMargenPorComercial — ajustes por debajo del sugerido (HU-013.2)
--    Mismas columnas del 0001 más numAjustesBajoSugerido. diferencia =
--    margenAplicado - margenPolitica (sugerido): negativa = por debajo.
-- =============================================================================
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
LEFT JOIN dbo_cotifly.Cotizaciones c ON c.id = a.cotizacionId
LEFT JOIN dbo_cotifly.MargenesPolitica mp ON mp.id = c.lineaNegocioId
GROUP BY a.usuarioId, mp.lineaNegocio;
GO

/* Verificación manual (opcional):
     SELECT COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'rowVersion');         -- 8
     SELECT COUNT(*) FROM dbo_cotifly.Cotizaciones WHERE pdfDesactualizado = 1;  -- 0 recién aplicado
     SELECT TOP (5) * FROM dbo_cotifly.vAjustesMargenPorComercial;
*/
