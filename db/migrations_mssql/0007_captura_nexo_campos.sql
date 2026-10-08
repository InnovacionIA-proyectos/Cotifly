/* ============================================================================
   Cotifly — 0007: campos de la captura hacia Nexo que faltaban (notas y municipios)
   ============================================================================
   HU-013.10 (#106), gaps G6 y G7 de docs/conexion_nexo/HU-013-preguntas-nexo.md §3. Patrón de
   scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First: el
   esquema SOLO cambia con scripts `db/migrations_mssql/000N_*.sql`; no hay
   migraciones EF). El 0006 es `0006_textos_y_concurrencia.sql` (HU-013.2, #98).
   Este script no depende de él, pero el DBA los aplica juntos y en orden:
   0006 y luego 0007.

   *** PENDIENTE DE APROBACIÓN — igual que el 0002 a 0005, no ejecutar en
       tbsdatacenter sin el visto bueno de scalderon-bs y del DBA ***
   Redacta: Duban (backend).

   Por qué: Cotifly reenvía a Nexo lo que guardó (reintento de
   POST /web/cotizaciones/{folio}/costeo/reintentar). Los campos nuevos de la
   captura deben quedar guardados para que el reintento envíe lo mismo que el
   primer intento.

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. Cotizaciones.notas NVARCHAR(2000) NULL: `notas` del encabezado de
        `CapturaSolicitudRequest` de Nexo (nota para la Mesa, distinta de la
        personalización del PDF). Mismo largo que Nexo.
     2. CotizacionLineasSolicitud.origenMunicipioId y destinoMunicipioId INT NULL:
        `origenMunicipioId`/`destinoMunicipioId` de `CapturaLineaRequest` (id de
        tbl_municipios de Nexo, el `id` de GET /web/municipios). NULL si la línea
        vino como texto (licitaciones, filas anteriores). Sin FK: el catálogo es de
        Nexo, en otra base.

   Idempotente y NO destructivo: cada ADD va guardado con COL_LENGTH; se puede
   correr varias veces sin cambiar nada. No hay backfill (las filas existentes no
   tienen esos datos).

   Precondición: 0001, 0002, 0004 y 0005 aplicados (el 0003 no es requisito; el
   0006 se aplica antes por orden de despliegue, no por dependencia).

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0007_captura_nexo_campos.sql
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

-- 1. Encabezado: notas para la Mesa de Nexo (G7).
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'notas') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD notas NVARCHAR(2000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO

-- 2. Línea: ids de municipio de Nexo (G6).
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenMunicipioId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD origenMunicipioId INT NULL;
GO

IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'destinoMunicipioId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD destinoMunicipioId INT NULL;
GO

IF OBJECT_ID(N'dbo_cotifly.ck_cotizacionLineasSolicitud_municipios', N'C') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK
        ADD CONSTRAINT ck_cotizacionLineasSolicitud_municipios
        CHECK ((origenMunicipioId IS NULL OR origenMunicipioId > 0) AND (destinoMunicipioId IS NULL OR destinoMunicipioId > 0));
GO

/* Verificación manual (opcional):
     SELECT COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'notas');                           -- 4000 (bytes)
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenMunicipioId');  -- 4
*/
