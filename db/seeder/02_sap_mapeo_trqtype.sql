/* ============================================================================
   Seeder 02 — Mapeo del TrqType de SAP TM a la línea de negocio (dbo_cotifly.SapMapeoTrqType)
   ============================================================================
   PENDIENTE: falta el TrqType real de SAP para `DevolucionContenedor`. No se inventa un código
   de SAP: confirmarlo con negocio/SAP y agregarlo aquí cuando se tenga:
       (N'<TrqType>', N'', N'DevolucionContenedor')

   Idempotente: inserta solo los pares (trqType, salesOrg) que faltan.

   HU-042.4 (#247): con scripts/18_sap_mapeo_fk_margenes_politica.sql aplicado, la tabla exige
   lineaNegocioId (FK a MargenesPolitica): se toma del id de la línea (seeder/01 va antes). Sin el 18
   (base que todavía no lo tiene) se inserta como antes, solo con el texto. La línea que no exista en
   MargenesPolitica no se inserta (con el 18, el INSERT fallaría por la FK).
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.SapMapeoTrqType: corra primero scripts/01_crear_esquema.sql.', 1;
GO

-- SQL dinámico: si el 18 no está, la columna lineaNegocioId no existe y el lote no compilaría.
DECLARE @conId BIT = CASE WHEN COL_LENGTH(N'dbo_cotifly.SapMapeoTrqType', N'lineaNegocioId') IS NULL THEN 0 ELSE 1 END;
DECLARE @sql NVARCHAR(MAX) = N'
INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo' + CASE WHEN @conId = 1 THEN N', lineaNegocioId' ELSE N'' END + N')
SELECT v.trqType, v.salesOrg, v.lineaNegocio, 1' + CASE WHEN @conId = 1 THEN N', m.id' ELSE N'' END + N'
FROM (VALUES
    (N''OPTN'', N'''', N''Nacional''),
    (N''OPDS'', N'''', N''Distribucion''),
    (N''OPIM'', N'''', N''Importacion''),
    (N''OPEX'', N'''', N''Exportacion''),
    (N''OPTI'', N'''', N''Internacional'')
) AS v (trqType, salesOrg, lineaNegocio)
' + CASE WHEN @conId = 1 THEN N'JOIN dbo_cotifly.MargenesPolitica AS m ON m.lineaNegocio = v.lineaNegocio
' ELSE N'' END + N'WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType AS s WHERE s.trqType = v.trqType AND s.salesOrg = v.salesOrg);';
EXEC sys.sp_executesql @sql;
GO

/* Verificación: SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType;  -- 5 (6 cuando se agregue DevolucionContenedor). */
