-- Convierte dbo_cotifly.SapMapeoTrqType.lineaNegocio (texto libre) en una FK real a
-- dbo_cotifly.MargenesPolitica(id), igual que ya hace CotizacionLineasSolicitud.lineaNegocioId.
-- Hoy la única conexión entre las dos tablas es que el texto coincida exacto — sin FK, si alguien
-- renombra una línea en MargenesPolitica, las filas de SapMapeoTrqType que la referenciaban por
-- nombre quedan huérfanas en silencio (nadie se entera hasta que falla un mapeo en producción).
--
-- Transición en 2 pasos a propósito (no se borra `lineaNegocio` todavía):
--   18 (este script): agrega `lineaNegocioId`, la rellena cruzando por el texto actual, la deja
--       NOT NULL con su FK. `lineaNegocio` (texto) se queda, de solo lectura, hasta que el backend
--       deje de usarla (issue aparte de Duban: clsSapMapeoRepository.cs / clsMargenResolverService.cs).
--   Script futuro (no numerado aún): DROP COLUMN lineaNegocio, una vez desplegado ese cambio.
--
-- Idempotente, en una transacción. No destructivo (no borra ni modifica `lineaNegocio`).

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF COL_LENGTH('dbo_cotifly.SapMapeoTrqType', 'lineaNegocioId') IS NULL
BEGIN
    ALTER TABLE dbo_cotifly.SapMapeoTrqType ADD lineaNegocioId INT NULL;
    PRINT N'18: columna lineaNegocioId agregada a dbo_cotifly.SapMapeoTrqType.';
END
ELSE
    PRINT N'18: dbo_cotifly.SapMapeoTrqType.lineaNegocioId ya existe, no se toca.';
GO
-- Separador de batch necesario: si el ALTER TABLE de arriba corrió en esta misma ejecución, el
-- motor todavía no "conoce" la columna nueva dentro del mismo batch (error 207, Invalid column
-- name) — con GO, el siguiente batch ya la ve. La transacción sigue abierta a través del GO.

-- Backfill por texto (solo las filas que todavía no tengan lineaNegocioId).
UPDATE s
SET s.lineaNegocioId = m.id
FROM dbo_cotifly.SapMapeoTrqType AS s
JOIN dbo_cotifly.MargenesPolitica AS m ON m.lineaNegocio = s.lineaNegocio
WHERE s.lineaNegocioId IS NULL;

-- No se sigue si queda algo sin cruzar: mejor frenar aquí (con lineaNegocio todavía disponible
-- para diagnosticar) que dejar una fila sin línea válida y forzarla a NOT NULL más abajo.
IF EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType WHERE lineaNegocioId IS NULL)
BEGIN
    ROLLBACK;
    THROW 50002, N'18: hay filas de SapMapeoTrqType cuyo lineaNegocio no cruza con ninguna fila de MargenesPolitica. Revisar antes de reintentar (SELECT trqType, salesOrg, lineaNegocio FROM dbo_cotifly.SapMapeoTrqType WHERE lineaNegocioId IS NULL).', 1;
END

IF COL_LENGTH('dbo_cotifly.SapMapeoTrqType', 'lineaNegocioId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE object_id = OBJECT_ID('dbo_cotifly.SapMapeoTrqType') AND name = 'lineaNegocioId' AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo_cotifly.SapMapeoTrqType ALTER COLUMN lineaNegocioId INT NOT NULL;
    PRINT N'18: lineaNegocioId pasó a NOT NULL.';
END

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_sapMapeoTrqType_lineaNegocioId')
BEGIN
    ALTER TABLE dbo_cotifly.SapMapeoTrqType
        ADD CONSTRAINT fk_sapMapeoTrqType_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id);
    PRINT N'18: FK fk_sapMapeoTrqType_lineaNegocioId creada.';
END
ELSE
    PRINT N'18: FK fk_sapMapeoTrqType_lineaNegocioId ya existe, no se toca.';

COMMIT;

-- Verificación: SELECT s.trqType, s.salesOrg, s.lineaNegocio, s.lineaNegocioId, m.lineaNegocio AS lineaPorId
-- FROM dbo_cotifly.SapMapeoTrqType s JOIN dbo_cotifly.MargenesPolitica m ON m.id = s.lineaNegocioId;
-- -- lineaNegocio y lineaPorId deben coincidir en todas las filas.
