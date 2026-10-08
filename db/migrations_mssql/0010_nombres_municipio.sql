/* ============================================================================
   Cotifly — 0010: nombres de municipio en la ruta, id de la solicitud de Nexo y nombre del conexo
   ============================================================================
   HU-021.3/021.4 (#130, #131). Decisión 2 del usuario (2026-10-08): el id del municipio es
   obligatorio en todos los flujos y ADEMÁS se guarda una copia del nombre, con el mismo criterio
   que el vehículo; las ciudades de devolución y de levante del contenedor también pasan a id + nombre.
   Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008 (EF Core Database First) y ADR-019.

   *** BORRADOR — PENDIENTE DE REVISIÓN de @scalderon-bs y del visto bueno del DBA. ***
   *** No ejecutar en tbsdatacenter (ni en ningún ambiente real) sin esa aprobación.  ***

   Qué hace (todo en dbo_cotifly, NO destructivo e idempotente: solo agrega columnas que faltan y
   llena las que están vacías):
     1. CotizacionLineasSolicitud (la ruta): origenNombre, destinoNombre, ciudadDevolucionNombre y
        ciudadLevanteNombre (NVARCHAR(150) NULL). Los ids ciudadDevolucionMunicipioId y
        ciudadLevanteMunicipioId ya los creó el 0009: solo se agregan si faltaran.
     2. CotizacionesSolicitud.solicitudNexoId (NVARCHAR(64) NULL): el id de la SOLICITUD de Nexo
        (lo devuelve POST /api/captura-solicitudes y lo consulta el polling, GET /api/solicitudes/{id}).
        El 0009 borró costeoId diciendo que "nunca se usó", pero el backend sí lo usaba: era ese id.
        cotizacionNexoId es otro (el de la COTIZACIÓN de Nexo, para el resultado y los conexos).
     3. CotizacionServiciosConexos.nombreServicioConexo (NVARCHAR(200) NULL): copia del nombre del
        conexo para el PDF (mismo criterio de la decisión 2). Las filas migradas quedan sin nombre.
     4. ck_cotizaciones_estadoEnvio: el 0009 y 01_crear_esquema.sql ya lo traen, pero la base
        compartida se instaló antes de ese cambio. Se crea solo si falta (y se detiene, sin aplicar
        nada, si alguna fila trae un valor fuera de sin_enviar/enviada/reenviada).
     5. Si la base pasó por el 0009 y todavía tiene sus respaldos (Respaldo0009_*), llena los nombres
        y el id de la solicitud de Nexo que estén vacíos desde esos respaldos (las rutas de las
        cotizaciones del chat toman el origen y el destino de su cabecera). Sin respaldos, no hace nada.

   Todo corre en UNA transacción: si algo falla, no queda nada aplicado. Si ya se aplicó, no cambia
   nada. Sirve igual para una base migrada (0001 a 0009) y para una instalada desde cero con
   db/scripts/01_crear_esquema.sql antes de este script (la base compartida de QA, 2026-10-08).

   Requisitos: 0009 aplicado (o 01_crear_esquema.sql); nivel de compatibilidad >= 130 (OPENJSON).

   Cómo ejecutarlo (-b detiene el script en el primer error):
     sqlcmd -S <servidor> -d dbTarifario_Nexo_Pruebas -E -C -I -b -i 0010_nombres_municipio.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

-- =============================================================================
-- 0. Precondiciones
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema de HU-021: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
IF (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) < 130
    THROW 50002, N'El 0010 usa OPENJSON y necesita nivel de compatibilidad 130 o superior.', 1;
IF EXISTS (SELECT 1 FROM dbo_cotifly.CotizacionesSolicitud WHERE estadoEnvio IS NOT NULL AND estadoEnvio NOT IN ('sin_enviar', 'enviada', 'reenviada'))
   AND NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_estadoEnvio'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
    THROW 50013, N'CotizacionesSolicitud.estadoEnvio tiene valores fuera de sin_enviar/enviada/reenviada: corríjalos antes de aplicar el 0010.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;
GO

-- =============================================================================
-- 1. Columnas nuevas (solo si faltan)
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0010 se abortó: revise el error anterior. No se aplicó nada.', 1;

IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenNombre') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD origenNombre NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'destinoNombre') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD destinoNombre NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadDevolucionMunicipioId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD ciudadDevolucionMunicipioId INT NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadDevolucionNombre') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD ciudadDevolucionNombre NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadLevanteMunicipioId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD ciudadLevanteMunicipioId INT NULL;
IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadLevanteNombre') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD ciudadLevanteNombre NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

IF COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'solicitudNexoId') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionesSolicitud ADD solicitudNexoId NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'nombreServicioConexo') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionServiciosConexos ADD nombreServicioConexo NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO

-- =============================================================================
-- 2. CHECK de estadoEnvio (si la base se instaló antes de que el 0009 lo trajera)
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0010 se abortó: revise el error anterior. No se aplicó nada.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_estadoEnvio'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))
    ALTER TABLE dbo_cotifly.CotizacionesSolicitud
        ADD CONSTRAINT ck_cotizaciones_estadoEnvio CHECK (estadoEnvio IS NULL OR estadoEnvio IN ('sin_enviar', 'enviada', 'reenviada'));
GO

-- =============================================================================
-- 3. Datos desde los respaldos del 0009 (solo si existen; solo lo que está vacío)
--    SQL dinámico: en una base instalada desde cero los respaldos no existen.
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0010 se abortó: revise el error anterior. No se aplicó nada.', 1;

-- 3.1 Rutas que ya eran líneas antes del 0009: el texto que tenía la línea.
IF OBJECT_ID(N'dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud', N'U') IS NOT NULL
    EXEC (N'
    UPDATE l
    SET l.origenNombre           = COALESCE(l.origenNombre, NULLIF(LTRIM(RTRIM(r.origen)), N'''')),
        l.destinoNombre          = COALESCE(l.destinoNombre, NULLIF(LTRIM(RTRIM(r.destino)), N'''')),
        l.ciudadDevolucionNombre = COALESCE(l.ciudadDevolucionNombre, NULLIF(LTRIM(RTRIM(r.ciudadDevolucionContenedor)), N'''')),
        l.ciudadLevanteNombre    = COALESCE(l.ciudadLevanteNombre, NULLIF(LTRIM(RTRIM(r.ciudadLevanteContenedor)), N''''))
    FROM dbo_cotifly.CotizacionLineasSolicitud AS l
    JOIN dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud AS r ON r.id = l.id
    WHERE l.origenNombre IS NULL OR l.destinoNombre IS NULL OR l.ciudadDevolucionNombre IS NULL OR l.ciudadLevanteNombre IS NULL;');

-- 3.2 Rutas que creó el 0009 para las cotizaciones del chat (ruta 1, sin línea antes): el texto de la cabecera; y el id de la
--     solicitud de Nexo (ex costeoId) de las que pasaron por el costeo en Nexo (las del chat lo usaban para otra cosa).
IF OBJECT_ID(N'dbo_cotifly.Respaldo0009_Cotizaciones', N'U') IS NOT NULL
BEGIN
    EXEC (N'
    UPDATE l
    SET l.origenNombre           = COALESCE(l.origenNombre, NULLIF(LTRIM(RTRIM(c.origen)), N'''')),
        l.destinoNombre          = COALESCE(l.destinoNombre, NULLIF(LTRIM(RTRIM(c.destino)), N'''')),
        l.ciudadDevolucionNombre = COALESCE(l.ciudadDevolucionNombre, NULLIF(LTRIM(RTRIM(c.ciudadDevolucionContenedor)), N'''')),
        l.ciudadLevanteNombre    = COALESCE(l.ciudadLevanteNombre, NULLIF(LTRIM(RTRIM(c.ciudadLevanteContenedor)), N''''))
    FROM dbo_cotifly.CotizacionLineasSolicitud AS l
    JOIN dbo_cotifly.Respaldo0009_Cotizaciones AS c ON c.id = l.cotizacionSolicitudId
    WHERE l.orden = 1
      AND (l.origenNombre IS NULL OR l.destinoNombre IS NULL)
      AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.Respaldo0009_CotizacionLineasSolicitud AS r WHERE r.id = l.id);');

    EXEC (N'
    UPDATE s
    SET s.solicitudNexoId = LEFT(LTRIM(RTRIM(c.costeoId)), 64)
    FROM dbo_cotifly.CotizacionesSolicitud AS s
    JOIN dbo_cotifly.Respaldo0009_Cotizaciones AS c ON c.id = s.id
    WHERE s.solicitudNexoId IS NULL AND s.estadoCosteo IS NOT NULL AND NULLIF(LTRIM(RTRIM(c.costeoId)), N'''') IS NOT NULL;');

    -- 3.3 Nombre de los conexos que el 0009 pasó del JSON de la cabecera a la ruta 1.
    EXEC (N'
    UPDATE s
    SET s.nombreServicioConexo = LEFT(j.nombre, 200)
    FROM dbo_cotifly.CotizacionServiciosConexos AS s
    JOIN dbo_cotifly.CotizacionLineasSolicitud AS l ON l.id = s.lineaSolicitudId AND l.orden = 1
    JOIN dbo_cotifly.Respaldo0009_Cotizaciones AS c ON c.id = l.cotizacionSolicitudId
    CROSS APPLY OPENJSON(c.serviciosConexos) WITH (idServicio NVARCHAR(64) ''$.id'', nombre NVARCHAR(400) ''$.nombre'') AS j
    WHERE s.nombreServicioConexo IS NULL AND j.idServicio = s.idServicioConexo AND NULLIF(LTRIM(RTRIM(j.nombre)), N'''') IS NOT NULL
      AND ISJSON(c.serviciosConexos) = 1;');
END
GO

-- =============================================================================
-- 4. Confirmar
-- =============================================================================
IF @@TRANCOUNT = 0 THROW 50099, N'La transacción del 0010 se abortó: revise el error anterior. No se aplicó nada.', 1;

COMMIT TRANSACTION;
PRINT N'0010 aplicado.';
GO

/* ============================================================================
   Verificación manual sugerida tras ejecutar:

     -- Las seis columnas existen.
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'origenNombre'),
            COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'destinoNombre'),
            COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadDevolucionNombre'),
            COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'ciudadLevanteNombre'),
            COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'solicitudNexoId'),
            COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'nombreServicioConexo');   -- ninguno NULL

     -- Rutas que siguen sin nombre de origen o destino (las del chat sin texto, o filas sin respaldo).
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE origenNombre IS NULL OR destinoNombre IS NULL;

   La readiness del backend (GET /health/ready) responde "esquema incompleto: falta 0010" mientras este
   script no esté aplicado.
   ============================================================================ */
