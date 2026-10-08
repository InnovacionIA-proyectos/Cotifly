/* ============================================================================
   Seeder 01 — Líneas de negocio y su margen de política (dbo_cotifly.MargenesPolitica)
   ============================================================================
   Sin esto el backend falla con "línea de negocio desconocida" en la primera cotización:
   es configuración que la aplicación necesita desde el primer día, no datos históricos.

   `mapeoLineasNexo` = nombre equivalente en el enum LineaNegocio de Nexo
   (NEXO/src/Nexo.Domain/Enums/Enums.cs: PuertoAInterior, InteriorAPuerto, Nacional,
   DevolucionContenedor, Andino, Licitaciones). `Distribucion` no tiene equivalente y queda
   NULL a propósito. `Licitaciones` no se mapea: es un tipo de solicitud, no una línea de negocio.

   Idempotente: inserta solo las líneas que faltan; no pisa los márgenes que ya se hayan
   ajustado. Se corre con la base ya seleccionada (-d). No crea nada.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo_cotifly.MargenesPolitica', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.MargenesPolitica: corra primero scripts/01_crear_esquema.sql.', 1;
GO

INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive, mapeoLineasNexo)
SELECT v.lineaNegocio, v.margen, v.metodoCalculo, 1, v.mapeoLineasNexo
FROM (VALUES
    (N'Nacional',             CAST(0.15 AS DECIMAL(9,4)), N'margen', N'Nacional'),
    (N'Distribucion',         CAST(0.15 AS DECIMAL(9,4)), N'margen', CAST(NULL AS NVARCHAR(MAX))),
    (N'Importacion',          CAST(0.15 AS DECIMAL(9,4)), N'margen', N'PuertoAInterior'),
    (N'Exportacion',          CAST(0.15 AS DECIMAL(9,4)), N'margen', N'InteriorAPuerto'),
    (N'Internacional',        CAST(0.15 AS DECIMAL(9,4)), N'margen', N'Andino'),
    (N'DevolucionContenedor', CAST(0.15 AS DECIMAL(9,4)), N'margen', N'DevolucionContenedor')
) AS v (lineaNegocio, margen, metodoCalculo, mapeoLineasNexo)
WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.MargenesPolitica AS m WHERE m.lineaNegocio = v.lineaNegocio);
GO

/* Verificación: SELECT lineaNegocio, margen, mapeoLineasNexo FROM dbo_cotifly.MargenesPolitica ORDER BY lineaNegocio;
   -- 6 filas, margen 0.15 en todas; mapeoLineasNexo NULL solo en Distribucion. */
