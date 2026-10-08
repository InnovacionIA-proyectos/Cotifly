/* ============================================================================
   Seeder 02 — Mapeo del TrqType de SAP TM a la línea de negocio (dbo_cotifly.SapMapeoTrqType)
   ============================================================================
   PENDIENTE: falta el TrqType real de SAP para `DevolucionContenedor`. No se inventa un código
   de SAP: confirmarlo con negocio/SAP y agregarlo aquí cuando se tenga:
       (N'<TrqType>', N'', N'DevolucionContenedor')

   Idempotente: inserta solo los pares (trqType, salesOrg) que faltan.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.SapMapeoTrqType: corra primero scripts/01_crear_esquema.sql.', 1;
GO

INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo)
SELECT v.trqType, v.salesOrg, v.lineaNegocio, 1
FROM (VALUES
    (N'OPTN', N'', N'Nacional'),
    (N'OPDS', N'', N'Distribucion'),
    (N'OPIM', N'', N'Importacion'),
    (N'OPEX', N'', N'Exportacion'),
    (N'OPTI', N'', N'Internacional')
) AS v (trqType, salesOrg, lineaNegocio)
WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType AS s WHERE s.trqType = v.trqType AND s.salesOrg = v.salesOrg);
GO

/* Verificación: SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType;  -- 5 (6 cuando se agregue DevolucionContenedor). */
