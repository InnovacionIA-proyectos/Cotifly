/* ============================================================================
   Cotifly — Esquema objetivo SQL Server 2019 (HU-002.4)
   ============================================================================
   Fuente de diseño: docs/ESQUEMA_OBJETIVO_SQLSERVER.md (HU-002.3) — no rediseña
   nada, es la traducción literal de ese documento a T-SQL ejecutable.

   Destino real (aclaración del equipo, 2026-09-22): NO es una base de datos
   nueva dedicada a Cotifly. Es un schema nuevo (`dbo_cotifly`) dentro de una
   base que YA EXISTE: `dbTarifario_Nexo_Pruebas`. Por eso este script:
     - No crea ninguna base de datos ni toca su COMPATIBILITY_LEVEL (no es
       nuestra para tocarle configuración a nivel de base).
     - Todos los objetos van calificados con el schema `dbo_cotifly.`, no `dbo.`.
     - La collation `Latin1_General_100_CI_AS_SC_UTF8` se declara EXPLÍCITAMENTE
       en cada columna de texto (nvarchar), no a nivel de base — la base ya
       existe y puede tener su propia collation fijada, no se asume ni se toca.

   Confirmar antes de ejecutar en el entorno real: `dbTarifario_Nexo_Pruebas`
   suena a nombre de instancia de pruebas — validar si producción usa el mismo
   nombre de base o uno distinto (ver docs/ESQUEMA_OBJETIVO_SQLSERVER.md §9).

   Decisión de HU-002.4: se ejecuta como script T-SQL directo (no migraciones EF
   Core) porque HU-003 (.NET/EF Core) todavía no existe como opción real.

   Conexión real (2026-09-22): host `tbsdatacenter`, base `dbTarifario_Nexo_Pruebas`,
   autenticación Windows (sin usuario/contraseña). Este script NO lee
   `settings.sqlserver_database_url` de `shared/config.py` — es un .sql plano para
   sqlcmd/SSMS, sin Python de por medio; ese setting es para cuando el backend (o un
   runner Python) se conecte, no para correr este script.

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -i 0001_esquema_objetivo.sql
   (`-E` = Windows Authentication / trusted connection, sin `-U`/`-P`) o vía SSMS/Azure
   Data Studio conectado con Windows Authentication a esa base (no la crea este
   script). Requiere permiso para CREATE SCHEMA/TABLE/VIEW dentro de ella.

   **Restricción real de dónde correrlo:** Windows Authentication solo funciona
   ejecutando el script directo desde Windows — NO desde los contenedores Docker
   (Linux) del proyecto, donde Windows Auth no funciona sin configurar Kerberos
   aparte (no vale la pena para un script de uso único). Se corre local, no vía
   `docker compose exec`.

   Precondición (HU-002.4, condición previa): las migraciones Alembic 0006/0007/
   0008 deben estar ya aplicadas sobre el Postgres de origen antes de exportar
   datos históricos — este script solo crea la ESTRUCTURA, no migra datos.

   Idempotencia: cada CREATE está guardado con comprobaciones de existencia, así
   que el script se puede volver a correr sin fallar si ya se ejecutó antes
   (no vuelve a crear objetos existentes, tampoco los actualiza — si el esquema
   objetivo cambia, este script hay que versionarlo, ej. 0002_*.sql).

   Nombres de tabla (corrección 2026-09-22, antes de la primera corrida real):
   se probó el prefijo `tbl`/`tblSap` primero y las tablas se llegaron a crear así
   en `dbo_cotifly` — el equipo decidió retirarlo (ver
   docs/ESQUEMA_OBJETIVO_SQLSERVER.md §9 y docs/HU_Backlog_Cotifly_ClientePro.md
   línea 218, desviación explícita del estándar `dotnet-enterprise-architect`).
   Este script ya crea las tablas en PascalCase plano (`Cotizaciones`,
   `SapMapeoTrqType`, etc.).

   `UsuariosWeb` (segunda corrección, 2026-09-22): se agregan `nombreUsuario`,
   `nombreCompleto`, `email` (mapeo de `tblSAPUsuariosApp`, §2.6 de la propuesta),
   ya construidas aunque sin poblar hasta HU-004 — mismo criterio que
   `costeoId`/`bdCliente`/`lineaNegocioId` en `Cotizaciones`. Sin asimetría con
   Postgres: la migración `0009_usuarios_web_clientepro` agregó las mismas 3
   columnas ahí (`nombre_usuario`/`nombre_completo`/`email`), en paralelo.

   Como este script solo crea si el objeto no existe (`IF OBJECT_ID(...) IS
   NULL`), cambiar una definición de tabla no altera lo que ya está creado en la
   base real — por eso el paso "0bis" de abajo suelta primero TODO lo que este
   mismo script haya creado en una corrida anterior (por el nombre actual, sin
   prefijo) y lo recrea ya con la forma corregida. Sin efecto en una base
   limpia (no encuentra nada que soltar). No se acumulan bloques de limpieza
   histórica versión tras versión — este es el único, siempre contra los
   nombres vigentes.
   ============================================================================ */

SET NOCOUNT ON;
GO

-- =============================================================================
-- 0. Conexión + schema (dentro de una base que ya existe)
-- =============================================================================
USE dbTarifario_Nexo_Pruebas;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'dbo_cotifly')
    EXEC('CREATE SCHEMA dbo_cotifly');
GO

-- =============================================================================
-- 0bis. Suelta todo lo que este script haya creado en una corrida anterior
--       (nombres vigentes, sin prefijo), para recrearlo con la forma corregida
--       más abajo. Sin efecto en una base limpia (no encuentra nada que soltar).
--       IMPORTANTE: esto tumba y recrea el schema completo (tablas vacías de
--       prueba, no datos de producción) — no usar este patrón una vez que
--       dbo_cotifly tenga datos reales que importe conservar.
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.vAjustesMargenPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo_cotifly.vAjustesMargenPorComercial;
GO
IF OBJECT_ID(N'dbo_cotifly.vActividadPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo_cotifly.vActividadPorComercial;
GO
IF OBJECT_ID(N'dbo_cotifly.CorreoDestinatarios', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.CorreoDestinatarios;
GO
IF OBJECT_ID(N'dbo_cotifly.MargenAjustes', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.MargenAjustes;
GO
IF OBJECT_ID(N'dbo_cotifly.Correos', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.Correos;
GO
IF OBJECT_ID(N'dbo_cotifly.EventosConversacion', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.EventosConversacion;
GO
IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.Cotizaciones;
GO
IF OBJECT_ID(N'dbo_cotifly.Sesiones', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.Sesiones;
GO
IF OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.UsuariosWeb;
GO
IF OBJECT_ID(N'dbo_cotifly.MargenesPolitica', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.MargenesPolitica;
GO
IF OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.SapMapeoTrqType;
GO
IF OBJECT_ID(N'dbo_cotifly.EventosHerramienta', N'U') IS NOT NULL
    DROP TABLE dbo_cotifly.EventosHerramienta;
GO

-- Collation usada en cada columna de texto (declarada por columna, no a nivel
-- de base — ver nota arriba). Un solo lugar para no repetir el literal 10 veces
-- sería ideal, pero T-SQL no tiene variables de tipo/collation reutilizables en
-- DDL — se repite explícitamente en cada columna nvarchar, como se decidió.

-- =============================================================================
-- 1. dbo_cotifly.MargenesPolitica (antes margenes_politica) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.MargenesPolitica', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.MargenesPolitica (
        id              INT IDENTITY(1,1)   NOT NULL,
        lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        margen          DECIMAL(9,4)        NOT NULL,
        metodoCalculo   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL CONSTRAINT df_margenesPolitica_metodoCalculo DEFAULT ('margen'),
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
-- 2. dbo_cotifly.SapMapeoTrqType (antes mapeo_trqtype) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.SapMapeoTrqType', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.SapMapeoTrqType (
        trqType         NVARCHAR(8)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        salesOrg        NVARCHAR(12)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL CONSTRAINT df_sapMapeoTrqType_salesOrg DEFAULT (''),
        lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        activo          BIT                 NOT NULL CONSTRAINT df_sapMapeoTrqType_activo DEFAULT (1),
        CONSTRAINT pk_sapMapeoTrqType PRIMARY KEY (trqType, salesOrg)
    );
    -- Nota (§5.10 del diseño): el modelo Postgres tiene además un UNIQUE(trqType,
    -- salesOrg) redundante con la propia PK compuesta — se omite acá a propósito
    -- (limpieza menor, no afecta comportamiento).
END
GO

-- =============================================================================
-- 3. dbo_cotifly.UsuariosWeb (antes usuarios_web) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.UsuariosWeb (
        id              INT                 NOT NULL, -- SIN IDENTITY: lo asigna SAP/ClientePro
        nombreUsuario   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        nombreCompleto  NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        email           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        telefono        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_usuariosWeb_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        ultimaSesion    DATETIMEOFFSET      NULL,
        CONSTRAINT pk_usuariosWeb PRIMARY KEY (id)
    );
    -- Actualización 2026-09-22: nombreUsuario/nombreCompleto/email (mapeo de
    -- tblSAPUsuariosApp, §2.6 de la propuesta) se construyen ya, sin poblar hasta
    -- HU-004 — mismo criterio que costeoId/bdCliente/lineaNegocioId en
    -- Cotizaciones. Asimetría con Postgres anotada en el header del script.
END
GO

-- =============================================================================
-- 4. dbo_cotifly.Cotizaciones (antes cotizaciones) — FK -> MargenesPolitica, UsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.Cotizaciones (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_cotizaciones_id DEFAULT (NEWID()),
        folio           NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL, -- acotado: no puede ser MAX con UNIQUE, ver nota §5.1
        lineaNegocioId  INT                 NULL,      -- lineaNegocio (texto) se retira, ver §3 del diseño
        clienteNombre   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        clienteIdSap    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        costeoId        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        bdCliente       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        costo           DECIMAL(18,2)       NULL,
        margenPolitica  DECIMAL(9,4)        NULL,
        margenAplicado  DECIMAL(9,4)        NULL,
        metodoCalculo   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        precioFinal     DECIMAL(18,2)       NULL,
        moneda          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL CONSTRAINT df_cotizaciones_moneda DEFAULT ('COP'),
        origenMargen    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        pdfPath         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        estadoCosteo    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        estadoEnvio     NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        spaceId         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        threadId        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        ganada          BIT                 NULL, -- HU-005, sin poblar
        motivoRechazo   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        marcadoAt       DATETIMEOFFSET      NULL, -- HU-005, sin poblar
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizaciones PRIMARY KEY (id),
        CONSTRAINT uq_cotizaciones_folio UNIQUE (folio),
        CONSTRAINT fk_cotizaciones_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id),
        CONSTRAINT fk_cotizaciones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id)
    );
    -- Nota `updatedAt` (§5.1 del diseño): en Postgres lo maneja SQLAlchemy a nivel
    -- de aplicación (onupdate=func.now()), no es un trigger — este script NO agrega
    -- un trigger de BD; quien escriba en esta tabla (backend .NET, HU-003.3) debe
    -- setear updatedAt explícitamente en cada UPDATE.
END
GO

-- =============================================================================
-- 5. dbo_cotifly.MargenAjustes (antes ajustes_margen) — FK -> Cotizaciones, UsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.MargenAjustes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.MargenAjustes (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_margenAjustes_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER    NULL,
        folio           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        margenPolitica  DECIMAL(9,4)        NULL,
        margenAplicado  DECIMAL(9,4)        NULL,
        diferencia      DECIMAL(9,4)        NULL,
        motivo          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_margenAjustes_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_margenAjustes PRIMARY KEY (id),
        CONSTRAINT fk_margenAjustes_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT fk_margenAjustes_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id)
    );
END
GO

-- =============================================================================
-- 6. dbo_cotifly.Correos (antes correos_enviados) — FK -> Cotizaciones, UsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Correos', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.Correos (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correos_id DEFAULT (NEWID()),
        cotizacionId    UNIQUEIDENTIFIER    NOT NULL,
        asunto          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        messageId       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        confirmadoPor   INT                 NULL, -- HU-004, sin poblar
        estado          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- enviado|fallido
        error           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_correos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_correos PRIMARY KEY (id),
        CONSTRAINT fk_correos_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.Cotizaciones (id),
        CONSTRAINT fk_correos_confirmadoPor FOREIGN KEY (confirmadoPor) REFERENCES dbo_cotifly.UsuariosWeb (id)
    );
    -- `para`/`cc` (arrays en Postgres) no se migran acá — ver CorreoDestinatarios.
END
GO

-- =============================================================================
-- 7. dbo_cotifly.CorreoDestinatarios (tabla nueva) — FK -> Correos
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CorreoDestinatarios', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.CorreoDestinatarios (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correoDestinatarios_id DEFAULT (NEWID()),
        correoId        UNIQUEIDENTIFIER    NOT NULL,
        email           NVARCHAR(320) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        tipo            NVARCHAR(10)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        CONSTRAINT pk_correoDestinatarios PRIMARY KEY (id),
        CONSTRAINT fk_correoDestinatarios_correoId FOREIGN KEY (correoId) REFERENCES dbo_cotifly.Correos (id),
        CONSTRAINT ck_correoDestinatarios_tipo CHECK (tipo IN ('para', 'cc'))
    );
    -- Invariante "al menos un tipo='para' por correoId" (§5.4 del diseño): no se
    -- puede expresar como CHECK de tabla — queda como validación de aplicación
    -- (HU-003.5), no del esquema.
END
GO

-- =============================================================================
-- 8. dbo_cotifly.Sesiones (antes sesiones) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Sesiones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.Sesiones (
        spaceId         NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL, -- acotado: es PK, ver nota §5.7
        threadId        NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        estado          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        ultimaActividad DATETIMEOFFSET      NOT NULL CONSTRAINT df_sesiones_ultimaActividad DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_sesiones PRIMARY KEY (spaceId, threadId),
        CONSTRAINT ck_sesiones_estado CHECK (estado IS NULL OR estado IN ('activa', 'archivada'))
    );
END
GO

-- =============================================================================
-- 9. dbo_cotifly.EventosConversacion (antes eventos_conversacion) — FK -> Sesiones (compuesta), UsuariosWeb
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.EventosConversacion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.EventosConversacion (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosConversacion_id DEFAULT (NEWID()),
        spaceId         NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- debe coincidir en tipo/collation con Sesiones.spaceId
        threadId        NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        usuarioId       INT                 NULL, -- HU-004, sin poblar
        rol             NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL, -- user|assistant
        contenido       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        herramientas    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_eventosConversacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_eventosConversacion PRIMARY KEY (id),
        CONSTRAINT fk_eventosConversacion_sesion FOREIGN KEY (spaceId, threadId) REFERENCES dbo_cotifly.Sesiones (spaceId, threadId),
        CONSTRAINT fk_eventosConversacion_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
        CONSTRAINT ck_eventosConversacion_herramientas_json CHECK (herramientas IS NULL OR ISJSON(herramientas) = 1)
    );
END
GO

-- =============================================================================
-- 10. dbo_cotifly.EventosHerramienta (antes eventos_herramienta) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.EventosHerramienta', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.EventosHerramienta (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosHerramienta_id DEFAULT (NEWID()),
        herramienta     NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        input           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        outputResumen   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        exito           BIT                 NULL,
        duracionMs      INT                 NULL,
        threadId        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- sin índice, se eliminó en 0006 (Postgres), no se recrea acá
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
IF OBJECT_ID(N'dbo_cotifly.vAjustesMargenPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo_cotifly.vAjustesMargenPorComercial;
GO

CREATE VIEW dbo_cotifly.vAjustesMargenPorComercial AS
SELECT
    a.usuarioId,
    mp.lineaNegocio,
    COUNT(*)            AS numAjustes,
    AVG(a.diferencia)   AS diferenciaPromedio,
    MIN(a.diferencia)   AS diferenciaMin,
    MAX(a.diferencia)   AS diferenciaMax,
    MAX(a.createdAt)    AS ultimoAjuste
FROM dbo_cotifly.MargenAjustes a
LEFT JOIN dbo_cotifly.Cotizaciones c ON c.id = a.cotizacionId
LEFT JOIN dbo_cotifly.MargenesPolitica mp ON mp.id = c.lineaNegocioId
GROUP BY a.usuarioId, mp.lineaNegocio;
GO

IF OBJECT_ID(N'dbo_cotifly.vActividadPorComercial', N'V') IS NOT NULL
    DROP VIEW dbo_cotifly.vActividadPorComercial;
GO

CREATE VIEW dbo_cotifly.vActividadPorComercial AS
SELECT
    c.usuarioId,
    COUNT(DISTINCT c.id)                                       AS numCotizaciones,
    SUM(CASE WHEN c.estadoEnvio = 'enviada' THEN 1 ELSE 0 END) AS numCotizacionesEnviadas,
    COALESCE(SUM(sub.numAjustes), 0)                           AS numAjustes,
    MAX(c.createdAt)                                           AS ultimaCotizacion
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

-- v_trazabilidad_cotizaciones se descartó en HU-002.2 (sin consumidor), no se migra.

/* ============================================================================
   Fin del script. Verificación rápida sugerida tras ejecutar:

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'
     ORDER BY TABLE_NAME;
     -- Debe listar las 9 tablas + CorreoDestinatarios (10 en total).

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS
     WHERE TABLE_SCHEMA = 'dbo_cotifly' ORDER BY TABLE_NAME;
     -- Debe listar vAjustesMargenPorComercial y vActividadPorComercial.
   ============================================================================ */
