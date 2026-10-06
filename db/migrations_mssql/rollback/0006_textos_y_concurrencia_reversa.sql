/* ============================================================================
   Cotifly — REVERSA del 0006 (concurrencia, textos del PDF y regla del PDF)
   ============================================================================
   Deja dbo_cotifly como estaba con 0001 a 0005: quita CotizacionPdfsHistorial,
   las columnas versionNegocio, textosCotizacion, pdfDesactualizado,
   margenSugerido y origenMargenSugerido de Cotizaciones (con sus CHECK y
   DEFAULT) y devuelve vAjustesMargenPorComercial a la definición del 0001.

   *** DESTRUCTIVO: se pierden los datos de esas columnas y el historial de PDF
       invalidados (los archivos del almacén NO se tocan). Solo con el visto bueno
       de scalderon-bs y del DBA, y después de volver a la versión de la
       aplicación anterior a #98 (la nueva no arranca sin el 0006). ***

   Idempotente: cada DROP va guardado; se puede correr varias veces. Si el 0007
   (#106) ya está aplicado, revisar primero su propia reversa (ver su cabecera).

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0006_textos_y_concurrencia_reversa.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

CREATE OR ALTER VIEW dbo_cotifly.vAjustesMargenPorComercial AS
SELECT
    a.usuarioId,
    mp.lineaNegocio,
    COUNT(*)            AS numAjustes,
    AVG(a.diferencia)   AS diferenciaPromedio,
    MIN(a.diferencia)   AS diferenciaMin,
    MAX(a.diferencia)   AS diferenciaMax,
    MAX(a.createdAt)    AS ultimoAjuste
FROM dbo_cotifly.MargenAjustes a
LEFT JOIN dbo_cotifly.Cotizaciones c ON c.id = a.cotizacionId
LEFT JOIN dbo_cotifly.MargenesPolitica mp ON mp.id = c.lineaNegocioId
GROUP BY a.usuarioId, mp.lineaNegocio;
GO

IF OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.CotizacionPdfsHistorial;
GO

IF OBJECT_ID(N'dbo_cotifly.ck_cotizaciones_versionNegocio', N'C') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP CONSTRAINT ck_cotizaciones_versionNegocio;
GO
IF OBJECT_ID(N'dbo_cotifly.df_cotizaciones_versionNegocio', N'D') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP CONSTRAINT df_cotizaciones_versionNegocio;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'versionNegocio') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP COLUMN versionNegocio;
GO

IF OBJECT_ID(N'dbo_cotifly.ck_cotizaciones_textosCotizacion_json', N'C') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP CONSTRAINT ck_cotizaciones_textosCotizacion_json;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'textosCotizacion') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP COLUMN textosCotizacion;
GO

IF OBJECT_ID(N'dbo_cotifly.df_cotizaciones_pdfDesactualizado', N'D') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP CONSTRAINT df_cotizaciones_pdfDesactualizado;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'pdfDesactualizado') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP COLUMN pdfDesactualizado;
GO

IF OBJECT_ID(N'dbo_cotifly.ck_cotizaciones_origenMargenSugerido', N'C') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP CONSTRAINT ck_cotizaciones_origenMargenSugerido;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'origenMargenSugerido') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP COLUMN origenMargenSugerido;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'margenSugerido') IS NOT NULL
    ALTER TABLE dbo_cotifly.Cotizaciones DROP COLUMN margenSugerido;
GO
