/* ============================================================================
   Cotifly — 0003: la métrica de cotizaciones enviadas cuenta también las reenviadas
   ============================================================================
   HU-003.6 (#12, parte B). Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008
   (EF Core Database First: el esquema SOLO cambia con scripts
   `db/migrations_mssql/000N_*.sql`; no hay migraciones EF).

   *** PENDIENTE DE APROBACIÓN — igual que el 0002, no ejecutar en tbsdatacenter
       sin el visto bueno del equipo de datos (Sara + scalderon-bs) ***

   Por qué: desde HU-003.5 (#11) un segundo envío exitoso deja la cotización en
   `estadoEnvio = 'reenviada'` (el Python siempre escribía 'enviada', bug corregido).
   La vista `vActividadPorComercial` del 0001 cuenta solo 'enviada', así que una
   cotización reenviada dejaba de contarse como enviada. El contrato
   (`ActividadComercial.num_cotizaciones_enviadas` en backend-dotnet/specs/openapi.yaml)
   exige contar ambos estados.

   Qué hace: redefine SOLO esa vista con `CREATE OR ALTER VIEW` (mismas columnas,
   mismos tipos, mismo nombre). No toca tablas, índices ni datos.

   Idempotente y NO destructivo: se puede correr varias veces. Precondición: 0001
   aplicado (la vista y las tablas existen); el 0002 no es requisito de este script.

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -i 0003_metricas_reenviadas.sql
   Las pruebas de integración del backend .NET (Testcontainers) aplican 0001, 0002
   y 0003 en orden, partidos por `GO`.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.MargenAjustes', N'U') IS NULL
    THROW 50001, 'dbo_cotifly no existe o está incompleto: aplique primero 0001_script_bd_cotifly_sqlserver.sql.', 1;
GO

CREATE OR ALTER VIEW dbo_cotifly.vActividadPorComercial AS
SELECT
    c.usuarioId,
    COUNT(DISTINCT c.id)                                                        AS numCotizaciones,
    SUM(CASE WHEN c.estadoEnvio IN ('enviada', 'reenviada') THEN 1 ELSE 0 END)  AS numCotizacionesEnviadas,
    COALESCE(SUM(sub.numAjustes), 0)                                            AS numAjustes,
    MAX(c.createdAt)                                                            AS ultimaCotizacion
FROM dbo_cotifly.Cotizaciones c
LEFT JOIN (
    SELECT cotizacionId, COUNT(*) AS numAjustes
    FROM dbo_cotifly.MargenAjustes
    WHERE cotizacionId IS NOT NULL
    GROUP BY cotizacionId
) sub ON sub.cotizacionId = c.id
WHERE c.usuarioId IS NOT NULL
GROUP BY c.usuarioId;
GO

/* ============================================================================
   Verificación rápida sugerida tras ejecutar:

     SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo_cotifly.vActividadPorComercial'));
     -- Debe contener: IN ('enviada', 'reenviada')
   ============================================================================ */
