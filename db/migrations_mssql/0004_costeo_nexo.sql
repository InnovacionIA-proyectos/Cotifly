/* ============================================================================
   Cotifly — 0004: costeo en Nexo (formulario ampliado, posiciones y estado)
   ============================================================================
   HU-005.8 (#24), base de HU-005.5 (#21), HU-005.6 (#22), HU-005.7 (#23) y
   HU-005.14 (#42). Patrón de scripts: backend-dotnet/specs/decisiones/ADR-008
   (EF Core Database First: el esquema SOLO cambia con scripts
   `db/migrations_mssql/000N_*.sql`; no hay migraciones EF). Diseño del flujo:
   backend-dotnet/specs/decisiones/ADR-015. Mapeo de campos aprobado con negocio:
   docs/MAPEO_CAMPOS_NEXO_COTIFLY.md.

   *** PENDIENTE DE APROBACIÓN — igual que el 0002 y el 0003, no ejecutar en
       tbsdatacenter sin el visto bueno de scalderon-bs y del DBA (coordinación
       con HU-002, como pide la HU-005.8) ***
   Redacta: Duban (backend).

   Ya existían desde HU-002.2 (0001, vacías hasta este frente): costeoId (id de la
   solicitud en Nexo), nitCliente, estadoCosteo, ganada, motivoRechazo, marcadoAt y
   costeoListoAt. Este script NO las toca.

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. Cotizaciones — campos del formulario ampliado que viajan a Nexo en
        `POST /api/captura-solicitudes` (una cotización de Cotifly = una solicitud
        de Nexo con UNA línea):
          encabezado: tipoSolicitud (spot|licitacion), deadline, valorEstimado,
                      referencia, horasCargue, horasDescargue
          línea:      origen, destino, tipoVehiculo, carroceria, tipologia,
                      claseViaje, claseContenedor, exclusivo, consolidado,
                      retornaContenedor, ciudadDevolucionContenedor,
                      levanteContenedorDiferente, ciudadLevanteContenedor,
                      valorAprovisionamiento, pesoKg, valorMercancia
        El costeo en sí (flete) NO se guarda aparte: queda en `costo` (columna del
        0001) cuando el polling lo detecta, y se sigue consultando en Nexo por
        costeoId.
     2. Cotizaciones — seguimiento de la integración:
          cotizacionNexoId          id de la cotización (borrador/entregada) de Nexo;
                                    lo exige POST /api/cotizaciones/{id}/resultado
          motivoEstadoCosteo        motivo legible del estado (error al enviar,
                                    rechazo de Nexo, costeo vencido...)
          costeoEnviadoAt           cuándo Nexo aceptó la solicitud
          costeoUltimaConsultaAt    última consulta del polling
          costeoSiguienteConsultaAt agenda del polling; NULL = no se consulta más
          costeoConsultasFallidas   fallos seguidos del polling (espaciado)
          serviciosConexos          JSON con los servicios conexos del costeo de
                                    Nexo, su precio con margen y si el comercial
                                    los incluye (HU-005.7); CHECK ISJSON
     3. CotizacionPosiciones — posiciones de cubicaje (1 a N con la cotización),
        mismas unidades que Nexo (centímetros y kilogramos).
     4. Índice filtrado para la agenda del polling.

   Nota para el DBA (deuda consciente, ADR-015): los campos de LÍNEA van en
   Cotizaciones porque hoy una cotización = una solicitud de Nexo con una línea.
   HU-011 (multi-línea / licitaciones) requiere un 0005 que mueva los campos de
   línea (origen ... valorMercancia) a `CotizacionLineasSolicitud`.

   Además: CHECK ck_cotizaciones_estadoCosteo con los 7 códigos de estado del
   costeo (la columna del 0001 es NVARCHAR(MAX), vacía hasta este frente; el
   CHECK se agrega WITH CHECK solo si ninguna fila tiene otro valor).

   Idempotente y NO destructivo: cada ADD/CREATE va guardado con comprobaciones de
   existencia; se puede correr varias veces. Todas las columnas nuevas son
   nullable (salvo costeoConsultasFallidas, con DEFAULT 0): las cotizaciones
   existentes, el PDF y "Mis cotizaciones" siguen funcionando sin estos datos.

   Precondición: 0001 y 0002 aplicados. El 0003 no es requisito.

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -I -i 0004_costeo_nexo.sql
   Las pruebas de integración del backend .NET (Testcontainers) aplican 0001, 0002,
   0003 y 0004 en orden, partidos por `GO`, y comparan el modelo EF con el esquema.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.TokensRefresco', N'U') IS NULL
    THROW 50001, 'dbo_cotifly no existe o está incompleto: aplique primero 0001_script_bd_cotifly_sqlserver.sql y 0002_identidad_estado_agente.sql.', 1;
GO

-- =============================================================================
-- 1. Cotizaciones — formulario ampliado (encabezado de la solicitud de Nexo)
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'tipoSolicitud') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD tipoSolicitud NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_tipoSolicitud'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD CONSTRAINT ck_cotizaciones_tipoSolicitud CHECK (tipoSolicitud IS NULL OR tipoSolicitud IN ('spot', 'licitacion'));
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'deadline') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD deadline DATETIMEOFFSET NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'valorEstimado') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD valorEstimado DECIMAL(18,2) NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'referencia') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD referencia NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'horasCargue') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD horasCargue DECIMAL(6,2) NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'horasDescargue') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD horasDescargue DECIMAL(6,2) NULL;
GO

-- =============================================================================
-- 1bis. Cotizaciones — formulario ampliado (la línea de la solicitud de Nexo)
--    Los catálogos cerrados (SICE-TAC, carrocerías, clase de viaje, clase de
--    contenedor, tipología) guardan el código de Nexo tal cual (decisión de
--    negocio del 2026-09-26): sin CHECK, porque Nexo es la fuente de esos valores.
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'origen') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD origen NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'destino') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD destino NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'tipoVehiculo') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD tipoVehiculo NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'carroceria') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD carroceria NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'tipologia') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD tipologia NVARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'claseViaje') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD claseViaje NVARCHAR(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'claseContenedor') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD claseContenedor NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'exclusivo') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD exclusivo BIT NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'consolidado') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD consolidado BIT NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'retornaContenedor') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD retornaContenedor BIT NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'ciudadDevolucionContenedor') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD ciudadDevolucionContenedor NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'levanteContenedorDiferente') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD levanteContenedorDiferente BIT NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'ciudadLevanteContenedor') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD ciudadLevanteContenedor NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'valorAprovisionamiento') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD valorAprovisionamiento DECIMAL(18,2) NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'pesoKg') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD pesoKg DECIMAL(12,2) NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'valorMercancia') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD valorMercancia DECIMAL(18,2) NULL;
GO

-- =============================================================================
-- 2. Cotizaciones — seguimiento de la integración con Nexo
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'cotizacionNexoId') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD cotizacionNexoId NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'motivoEstadoCosteo') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD motivoEstadoCosteo NVARCHAR(1000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'costeoEnviadoAt') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD costeoEnviadoAt DATETIMEOFFSET NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'costeoUltimaConsultaAt') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD costeoUltimaConsultaAt DATETIMEOFFSET NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'costeoSiguienteConsultaAt') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD costeoSiguienteConsultaAt DATETIMEOFFSET NULL;
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'costeoConsultasFallidas') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD costeoConsultasFallidas INT NOT NULL CONSTRAINT df_cotizaciones_costeoConsultasFallidas DEFAULT (0);
GO
IF COL_LENGTH(N'dbo_cotifly.Cotizaciones', N'serviciosConexos') IS NULL
    ALTER TABLE dbo_cotifly.Cotizaciones ADD serviciosConexos NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_serviciosConexos_json'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD CONSTRAINT ck_cotizaciones_serviciosConexos_json CHECK (serviciosConexos IS NULL OR ISJSON(serviciosConexos) = 1);
GO

-- Estados del costeo (ADR-015 §2). Si alguna fila trae un valor fuera de la lista, el
-- script se detiene con un error explícito en vez de crear el CHECK sin validar.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizaciones_estadoCosteo'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
BEGIN
    IF EXISTS (SELECT 1 FROM dbo_cotifly.Cotizaciones WHERE estadoCosteo IS NOT NULL AND estadoCosteo NOT IN
               ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'))
        THROW 50002, 'Cotizaciones.estadoCosteo tiene valores fuera de los 7 códigos del costeo: revíselos antes de aplicar el 0004.', 1;
    ALTER TABLE dbo_cotifly.Cotizaciones
        ADD CONSTRAINT ck_cotizaciones_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
            ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'));
END
GO

-- Agenda del polling (HU-005.6): solo las cotizaciones que todavía se consultan.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cotizaciones_costeoSiguienteConsultaAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
    CREATE INDEX ix_cotizaciones_costeoSiguienteConsultaAt ON dbo_cotifly.Cotizaciones (costeoSiguienteConsultaAt)
        WHERE costeoSiguienteConsultaAt IS NOT NULL;
GO

-- =============================================================================
-- 3. dbo_cotifly.CotizacionPosiciones — posiciones de cubicaje (HU-005.4)
--    `posiciones[]` de CapturaLineaRequest en Nexo: descripcion, cantidad,
--    largoCm, anchoCm, altoCm, pesoKg. `orden` conserva el orden del formulario.
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionPosiciones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CotizacionPosiciones (
        id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionPosiciones_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER NOT NULL,
        orden           INT              NOT NULL,
        descripcion     NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        cantidad        INT              NOT NULL CONSTRAINT df_cotizacionPosiciones_cantidad DEFAULT (1),
        largoCm         DECIMAL(10,2)    NOT NULL,
        anchoCm         DECIMAL(10,2)    NOT NULL,
        altoCm          DECIMAL(10,2)    NOT NULL,
        pesoKg          DECIMAL(12,2)    NULL,
        createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionPosiciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizacionPosiciones PRIMARY KEY (id),
        CONSTRAINT fk_cotizacionPosiciones_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT uq_cotizacionPosiciones_cotizacionId_orden UNIQUE (cotizacionId, orden),
        CONSTRAINT ck_cotizacionPosiciones_cantidad CHECK (cantidad >= 1),
        CONSTRAINT ck_cotizacionPosiciones_medidas CHECK (largoCm > 0 AND anchoCm > 0 AND altoCm > 0 AND (pesoKg IS NULL OR pesoKg >= 0))
    );
END
GO

/* Verificación manual (opcional):
     SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones');
     -- 28 columnas del 0001 + 29 de este script = 57
     SELECT * FROM dbo_cotifly.CotizacionPosiciones;
*/
