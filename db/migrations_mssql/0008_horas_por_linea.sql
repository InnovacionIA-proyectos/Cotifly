/* ============================================================================
   Cotifly — 0008: horas de cargue y descargue por línea (licitaciones)
   ============================================================================
   HU-013.11 (#108), gap G5 de docs/conexion_nexo/HU-013-preguntas-nexo.md §3. Patrón de
   scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First: el
   esquema SOLO cambia con scripts `db/migrations_mssql/000N_*.sql`; no hay
   migraciones EF). Depende del 0005 (tabla CotizacionLineasSolicitud); el DBA
   los aplica en orden: 0006, 0007 y luego 0008.

   *** PENDIENTE DE APROBACIÓN — igual que el 0002 a 0007, no ejecutar en
       tbsdatacenter sin el visto bueno de scalderon-bs y del DBA ***
   Redacta: Duban (backend).

   Por qué: Nexo acepta `horasCargue`/`horasDescargue` en cada línea de
   `CapturaLineaRequest` (sobreescriben su valor por defecto). En una licitación
   cada fila del pliego puede traer sus horas. Cotifly reenvía a Nexo lo que
   guardó (reintento de POST /web/cotizaciones/{folio}/costeo/reintentar y
   re-solicitud): las horas de cada línea deben quedar guardadas para que el
   reintento envíe lo mismo que el primer intento.

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. CotizacionLineasSolicitud.horasCargue y horasDescargue DECIMAL(6,2) NULL
        (mismo tipo que Cotizaciones.horasCargue/horasDescargue del 0004). NULL =
        la línea usa las horas del encabezado de la cotización.
     2. CHECK ck_cotizacionLineasSolicitud_horas: entre 0 y 240 (límite de Nexo).

   Idempotente y NO destructivo: cada ADD va guardado con COL_LENGTH/OBJECT_ID;
   se puede correr varias veces sin cambiar nada. No hay backfill (las filas
   existentes siguen con las horas del encabezado).

   Precondición: 0001, 0002, 0004 y 0005 aplicados.

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0008_horas_por_linea.sql
   Las pruebas de integración del backend .NET (Testcontainers) lo aplican después
   de los anteriores, partido por `GO`, y comparan el modelo EF con el esquema.
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

-- 1. Línea: horas propias de cargue y descargue (G5).
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasCargue') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD horasCargue DECIMAL(6,2) NULL;
GO

IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasDescargue') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD horasDescargue DECIMAL(6,2) NULL;
GO

-- 2. Rango de Nexo (0 a 240 horas).
IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_horas', N'C') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
        ADD CONSTRAINT ck_cotizacionLineasSolicitud_horas
        CHECK ((horasCargue IS NULL OR (horasCargue >= 0 AND horasCargue <= 240))
           AND (horasDescargue IS NULL OR (horasDescargue >= 0 AND horasDescargue <= 240)));
GO

/* Verificación manual (opcional):
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasCargue');     -- 5 (bytes)
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'horasDescargue');  -- 5
*/
