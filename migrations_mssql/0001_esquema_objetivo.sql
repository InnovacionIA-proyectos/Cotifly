/* ============================================================================
   Cotifly — Esquema objetivo SQL Server 2019 (HU-002.4)
   ============================================================================
   Fuente de diseño: docs/ESQUEMA_OBJETIVO_SQLSERVER.md (HU-002.3) — no rediseña
   nada, es la traducción literal de ese documento a T-SQL ejecutable.

   Decisión de HU-002.4: se ejecuta como script T-SQL directo (no migraciones EF
   Core) porque HU-003 (.NET/EF Core) todavía no existe como opción real.

   Cómo ejecutarlo:
     sqlcmd -S <servidor> -U <usuario> -P <password> -i 0001_esquema_objetivo.sql
   o vía SSMS/Azure Data Studio contra la instancia SQL Server 2019 corporativa.
   Requiere permisos para CREATE DATABASE (o que el DBA ya la haya creado — ver
   sección 0, el CREATE DATABASE está guardado para no fallar si ya existe).

   Precondición (HU-002.4, condición previa): las migraciones Alembic 0006/0007/
   0008 deben estar ya aplicadas sobre el Postgres de origen antes de exportar
   datos históricos — este script solo crea la ESTRUCTURA, no migra datos.

   Idempotencia: cada CREATE está guardado con comprobaciones de existencia, así
   que el script se puede volver a correr sin fallar si ya se ejecutó antes
   (no vuelve a crear objetos existentes, tampoco los actualiza — si el esquema
   objetivo cambia, este script hay que versionarlo, ej. 0002_*.sql).
   ============================================================================ */

SET NOCOUNT ON;
GO

-- =============================================================================
-- 0. Base de datos
-- =============================================================================
IF DB_ID(N'Cotifly') IS NULL
BEGIN
    CREATE DATABASE Cotifly
        COLLATE Latin1_General_100_CI_AS_SC_UTF8; -- confirmado con el DBA, §7 del diseño
END
GO

ALTER DATABASE Cotifly SET COMPATIBILITY_LEVEL = 150; -- nativo de SQL Server 2019
GO

USE Cotifly;
GO

-- =============================================================================
-- 1. tblMargenesPolitica (antes margenes_politica) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo.tblMargenesPolitica', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblMargenesPolitica (
        id              INT IDENTITY(1,1)   NOT NULL,
        lineaNegocio    NVARCHAR(40)        NOT NULL,
        margen          DECIMAL(9,4)        NOT NULL,
        metodoCalculo   NVARCHAR(MAX)       NOT NULL CONSTRAINT df_margenesPolitica_metodoCalculo DEFAULT ('margen'),
        isActive        BIT                 NOT NULL CONSTRAINT df_margenesPolitica_isActive DEFAULT (1),
        updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_margenesPolitica_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_margenesPolitica PRIMARY KEY (id),
        CONSTRAINT uq_margenesPolitica_lineaNegocio UNIQUE (lineaNegocio)
    );
    -- Nota (§5.9 del diseño): `metodoCalculo` es una columna sin uso real, decisión
    -- abierta heredada de la auditoría de HU-002.1, nunca resuelta — se mantiene
    -- tal cual, no se rediseña acá.
END
GO

-- =============================================================================
-- 2. tblSapMapeoTrqType (antes mapeo_trqtype) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo.tblSapMapeoTrqType', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSapMapeoTrqType (
        trqType         NVARCHAR(8)         NOT NULL,
        salesOrg        NVARCHAR(12)        NOT NULL CONSTRAINT df_sapMapeoTrqType_salesOrg DEFAULT (''),
        lineaNegocio    NVARCHAR(40)        NOT NULL,
        activo          BIT                 NOT NULL CONSTRAINT df_sapMapeoTrqType_activo DEFAULT (1),
        CONSTRAINT pk_sapMapeoTrqType PRIMARY KEY (trqType, salesOrg)
    );
    -- Nota (§5.10 del diseño): el modelo Postgres tiene además un UNIQUE(trqType,
    -- salesOrg) redundante con la propia PK compuesta — se omite acá a propósito
    -- (limpieza menor, no afecta comportamiento).
END
GO

-- =============================================================================
-- 3. tblUsuariosWeb (antes usuarios_web) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo.tblUsuariosWeb', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblUsuariosWeb (
        id              INT                 NOT NULL, -- SIN IDENTITY: lo asigna SAP/ClientePro
        telefono        NVARCHAR(MAX)       NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_usuariosWeb_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        ultimaSesion    DATETIMEOFFSET      NULL,
        CONSTRAINT pk_usuariosWeb PRIMARY KEY (id)
    );
    -- Nota (§0/§5.8 del diseño): no incluye nombreUsuario/nombreCompleto/email
    -- (diseño de §2.6 de la propuesta, depende de HU-004/ClientePro, no ejecutado
    -- todavía) — se agregan en una migración aparte cuando HU-004 aterrice.
END
GO

-- =============================================================================
-- 4. tblCotizaciones (antes cotizaciones) — FK -> tblMargenesPolitica, tblUsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo.tblCotizaciones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblCotizaciones (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_cotizaciones_id DEFAULT (NEWID()),
        folio           NVARCHAR(50)        NOT NULL, -- acotado: no puede ser MAX con UNIQUE, ver nota §5.1
        lineaNegocioId  INT                 NULL,      -- lineaNegocio (texto) se retira, ver §3 del diseño
        clienteNombre   NVARCHAR(MAX)       NULL,
        clienteIdSap    NVARCHAR(MAX)       NULL,
        costeoId        NVARCHAR(MAX)       NULL, -- HU-005, sin poblar
        bdCliente       NVARCHAR(MAX)       NULL, -- HU-005, sin poblar
        costo           DECIMAL(18,2)       NULL,
        margenPolitica  DECIMAL(9,4)        NULL,
        margenAplicado  DECIMAL(9,4)        NULL,
        metodoCalculo   NVARCHAR(MAX)       NULL,
        precioFinal     DECIMAL(18,2)       NULL,
        moneda          NVARCHAR(MAX)       NULL CONSTRAINT df_cotizaciones_moneda DEFAULT ('COP'),
        origenMargen    NVARCHAR(MAX)       NULL,
        pdfPath         NVARCHAR(MAX)       NULL,
        estadoCosteo    NVARCHAR(MAX)       NULL, -- HU-005, sin poblar
        estadoEnvio     NVARCHAR(MAX)       NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        spaceId         NVARCHAR(MAX)       NULL,
        threadId        NVARCHAR(MAX)       NULL,
        ganada          BIT                 NULL, -- HU-005, sin poblar
        motivoRechazo   NVARCHAR(MAX)       NULL, -- HU-005, sin poblar
        marcadoAt       DATETIMEOFFSET      NULL, -- HU-005, sin poblar
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizaciones PRIMARY KEY (id),
        CONSTRAINT uq_cotizaciones_folio UNIQUE (folio),
        CONSTRAINT fk_cotizaciones_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo.tblMargenesPolitica (id),
        CONSTRAINT fk_cotizaciones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo.tblUsuariosWeb (id)
    );
    -- Nota `updatedAt` (§5.1 del diseño): en Postgres lo maneja SQLAlchemy a nivel
    -- de aplicación (onupdate=func.now()), no es un trigger — este script NO agrega
    -- un trigger de BD; quien escriba en esta tabla (backend .NET, HU-003.3) debe
    -- setear updatedAt explícitamente en cada UPDATE.
END
GO

-- =============================================================================
-- 5. tblMargenAjustes (antes ajustes_margen) — FK -> tblCotizaciones, tblUsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo.tblMargenAjustes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblMargenAjustes (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_margenAjustes_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER    NULL,
        folio           NVARCHAR(MAX)       NULL,
        margenPolitica  DECIMAL(9,4)        NULL,
        margenAplicado  DECIMAL(9,4)        NULL,
        diferencia      DECIMAL(9,4)        NULL,
        motivo          NVARCHAR(MAX)       NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_margenAjustes_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_margenAjustes PRIMARY KEY (id),
        CONSTRAINT fk_margenAjustes_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo.tblCotizaciones (id),
        CONSTRAINT fk_margenAjustes_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo.tblUsuariosWeb (id)
    );
END
GO

-- =============================================================================
-- 6. tblCorreos (antes correos_enviados) — FK -> tblCotizaciones, tblUsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo.tblCorreos', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblCorreos (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correos_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER    NOT NULL,
        asunto          NVARCHAR(MAX)       NULL,
        messageId       NVARCHAR(MAX)       NULL,
        confirmadoPor   INT                 NULL, -- HU-004, sin poblar
        estado          NVARCHAR(MAX)       NULL, -- enviado|fallido
        error           NVARCHAR(MAX)       NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_correos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_correos PRIMARY KEY (id),
        CONSTRAINT fk_correos_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo.tblCotizaciones (id),
        CONSTRAINT fk_correos_confirmadoPor FOREIGN KEY (confirmadoPor) REFERENCES dbo.tblUsuariosWeb (id)
    );
    -- `para`/`cc` (arrays en Postgres) no se migran acá — ver tblCorreoDestinatarios.
END
GO

-- =============================================================================
-- 7. tblCorreoDestinatarios (tabla nueva) — FK -> tblCorreos
-- =============================================================================
IF OBJECT_ID(N'dbo.tblCorreoDestinatarios', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblCorreoDestinatarios (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correoDestinatarios_id DEFAULT (NEWID()),
        correoId        UNIQUEIDENTIFIER    NOT NULL,
        email           NVARCHAR(320)       NOT NULL,
        tipo            NVARCHAR(10)        NOT NULL,
        CONSTRAINT pk_correoDestinatarios PRIMARY KEY (id),
        CONSTRAINT fk_correoDestinatarios_correoId FOREIGN KEY (correoId) REFERENCES dbo.tblCorreos (id),
        CONSTRAINT ck_correoDestinatarios_tipo CHECK (tipo IN ('para', 'cc'))
    );
    -- Invariante "al menos un tipo='para' por correoId" (§5.4 del diseño): no se
    -- puede expresar como CHECK de tabla — queda como validación de aplicación
    -- (HU-003.5), no del esquema.
END
GO

-- =============================================================================
-- 8. tblSesiones (antes sesiones) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo.tblSesiones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblSesiones (
        spaceId         NVARCHAR(128)       NOT NULL, -- acotado: es PK, ver nota §5.7
        threadId        NVARCHAR(128)       NOT NULL,
        estado          NVARCHAR(MAX)       NULL,
        ultimaActividad DATETIMEOFFSET      NOT NULL CONSTRAINT df_sesiones_ultimaActividad DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_sesiones PRIMARY KEY (spaceId, threadId),
        CONSTRAINT ck_sesiones_estado CHECK (estado IS NULL OR estado IN ('activa', 'archivada'))
    );
END
GO

-- =============================================================================
-- 9. tblEventosConversacion (antes eventos_conversacion) — FK -> tblSesiones (compuesta), tblUsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo.tblEventosConversacion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblEventosConversacion (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosConversacion_id DEFAULT (NEWID()),
        spaceId         NVARCHAR(128)       NULL, -- debe coincidir en tipo con tblSesiones.spaceId
        threadId        NVARCHAR(128)       NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        rol             NVARCHAR(MAX)       NOT NULL, -- user|assistant
        contenido       NVARCHAR(MAX)       NULL,
        herramientas    NVARCHAR(MAX)       NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_eventosConversacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_eventosConversacion PRIMARY KEY (id),
        CONSTRAINT fk_eventosConversacion_sesion FOREIGN KEY (spaceId, threadId) REFERENCES dbo.tblSesiones (spaceId, threadId),
        CONSTRAINT fk_eventosConversacion_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo.tblUsuariosWeb (id),
        CONSTRAINT ck_eventosConversacion_herramientas_json CHECK (herramientas IS NULL OR ISJSON(herramientas) = 1)
    );
END
GO

-- =============================================================================
-- 10. tblEventosHerramienta (antes eventos_herramienta) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo.tblEventosHerramienta', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.tblEventosHerramienta (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosHerramienta_id DEFAULT (NEWID()),
        herramienta     NVARCHAR(MAX)       NOT NULL,
        input           NVARCHAR(MAX)       NULL,
        outputResumen   NVARCHAR(MAX)       NULL,
        exito           BIT                 NULL,
        duracionMs      INT                 NULL,
        threadId        NVARCHAR(MAX)       NULL, -- sin índice, se eliminó en 0006 (Postgres), no se recrea acá
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_eventosHerramienta_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_eventosHerramienta PRIMARY KEY (id),
        CONSTRAINT ck_eventosHerramienta_input_json CHECK (input IS NULL OR ISJSON(input) = 1),
        CONSTRAINT ck_eventosHerramienta_outputResumen_json CHECK (outputResumen IS NULL OR ISJSON(outputResumen) = 1)
    );
END
GO

-- =============================================================================
-- 11. Vistas de solo lectura (§6 del diseño)
-- =============================================================================
IF OBJECT_ID(N'dbo.vAjustesMargenPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo.vAjustesMargenPorComercial;
GO

CREATE VIEW dbo.vAjustesMargenPorComercial AS
SELECT
    a.usuarioId,
    mp.lineaNegocio,
    COUNT(*)            AS numAjustes,
    AVG(a.diferencia)   AS diferenciaPromedio,
    MIN(a.diferencia)   AS diferenciaMin,
    MAX(a.diferencia)   AS diferenciaMax,
    MAX(a.createdAt)    AS ultimoAjuste
FROM dbo.tblMargenAjustes a
LEFT JOIN dbo.tblCotizaciones c ON c.id = a.cotizacionId
LEFT JOIN dbo.tblMargenesPolitica mp ON mp.id = c.lineaNegocioId
GROUP BY a.usuarioId, mp.lineaNegocio;
GO

IF OBJECT_ID(N'dbo.vActividadPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo.vActividadPorComercial;
GO

CREATE VIEW dbo.vActividadPorComercial AS
SELECT
    c.usuarioId,
    COUNT(DISTINCT c.id)                                       AS numCotizaciones,
    SUM(CASE WHEN c.estadoEnvio = 'enviada' THEN 1 ELSE 0 END) AS numCotizacionesEnviadas,
    COALESCE(SUM(sub.numAjustes), 0)                           AS numAjustes,
    MAX(c.createdAt)                                           AS ultimaCotizacion
FROM dbo.tblCotizaciones c
LEFT JOIN (
    SELECT cotizacionId, COUNT(*) AS numAjustes
    FROM dbo.tblMargenAjustes
    WHERE cotizacionId IS NOT NULL
    GROUP BY cotizacionId
) sub ON sub.cotizacionId = c.id
WHERE c.usuarioId IS NOT NULL
GROUP BY c.usuarioId;
GO

-- v_trazabilidad_cotizaciones se descartó en HU-002.2 (sin consumidor), no se migra.

/* ============================================================================
   Fin del script. Verificación rápida sugerida tras ejecutar:

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
     ORDER BY TABLE_NAME;
     -- Debe listar las 9 tablas + tblCorreoDestinatarios (10 en total).

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS ORDER BY TABLE_NAME;
     -- Debe listar vAjustesMargenPorComercial y vActividadPorComercial.
   ============================================================================ */
