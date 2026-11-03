/* ============================================================================
   Seeder 02 — Mapeo del TrqType de SAP TM a la línea de negocio (dbo_cotifly.SapMapeoTrqType)
   ============================================================================
   PENDIENTE: falta el TrqType real de SAP para `DevolucionContenedor`. No se inventa un código
   de SAP: confirmarlo con negocio/SAP y agregarlo aquí cuando se tenga:
       (N'<TrqType>', N'', N'DevolucionContenedor')

   Idempotente: inserta solo los pares (trqType, salesOrg) que faltan.

   Con el script 18 (HU-042.4, #247) la tabla tiene además `lineaNegocioId INT NOT NULL` (FK a
   MargenesPolitica): el id se busca por el texto de la línea, igual que el backfill del 18. Sin el
   18 (bases de prueba del backend que no lo aplican) se inserta solo el texto, como antes. Va
   después del seeder 01, que siembra MargenesPolitica; se detiene sin insertar nada si una línea
   no existe ahí.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.SapMapeoTrqType: corra primero scripts/01_crear_esquema.sql.', 1;
GO

-- Misma collation explícita que las columnas de SapMapeoTrqType y MargenesPolitica (01 y 0001). No sirve
-- DATABASE_DEFAULT: una base creada sin collation (las de prueba del backend) queda con la del servidor y choca.
CREATE TABLE #mapeo (
    trqType      NVARCHAR(8)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    salesOrg     NVARCHAR(12) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    lineaNegocio NVARCHAR(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL
);
INSERT INTO #mapeo (trqType, salesOrg, lineaNegocio) VALUES
    (N'OPTN', N'', N'Nacional'),
    (N'OPDS', N'', N'Distribucion'),
    (N'OPIM', N'', N'Importacion'),
    (N'OPEX', N'', N'Exportacion'),
    (N'OPTI', N'', N'Internacional');

DELETE v FROM #mapeo AS v
WHERE EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType AS s WHERE s.trqType = v.trqType AND s.salesOrg = v.salesOrg);

IF COL_LENGTH(N'dbo_cotifly.SapMapeoTrqType', N'lineaNegocioId') IS NULL
    INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo)
    SELECT trqType, salesOrg, lineaNegocio, 1 FROM #mapeo;
ELSE
BEGIN
    IF EXISTS (SELECT 1 FROM #mapeo AS v WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.MargenesPolitica AS m WHERE m.lineaNegocio = v.lineaNegocio))
        THROW 50002, N'Una línea del mapeo no existe en MargenesPolitica: corra primero seeder/01_lineas_negocio.sql. No se insertó nada.', 1;

    -- Dinámico: si la columna no existe, el INSERT con lineaNegocioId no compilaría en este lote.
    EXEC (N'INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, lineaNegocioId, activo)
            SELECT v.trqType, v.salesOrg, v.lineaNegocio, m.id, 1
            FROM #mapeo AS v
            JOIN dbo_cotifly.MargenesPolitica AS m ON m.lineaNegocio = v.lineaNegocio;');
END

DROP TABLE #mapeo;
GO

/* Verificación: SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType;  -- 5 (6 cuando se agregue DevolucionContenedor).
   Con el 18: SELECT s.trqType, s.lineaNegocio, m.lineaNegocio AS lineaPorId FROM dbo_cotifly.SapMapeoTrqType s
              JOIN dbo_cotifly.MargenesPolitica m ON m.id = s.lineaNegocioId;  -- las dos columnas coinciden */
