/* ============================================================================
   Cotifly — Esquema objetivo SQL Server 2019 (HU-002.4 + Fase 2)
   ============================================================================
   Fuente de diseño: docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md — no rediseña nada, es la
   traducción literal de ese documento a T-SQL ejecutable. Script único: crea
   todo el esquema (tablas, FKs, constraints, vistas) y siembra la configuración
   base de una sola vez, en su forma FINAL — no hay una "Fase 1" intermedia que
   luego se altera; lo que antes eran 3 scripts separados (creación, seed,
   ampliación Fase 2) quedó consolidado acá el 2026-09-24 porque nada de esto
   está en producción todavía (se publica de cero).

   Destino real: NO es una base de datos nueva dedicada a Cotifly. Es un schema
   nuevo (`dbo_cotifly`) dentro de una base que YA EXISTE:
   `dbTarifario_Nexo_Pruebas`. Por eso este script:
     - No crea ninguna base de datos ni toca su COMPATIBILITY_LEVEL (no es
       nuestra para tocarle configuración a nivel de base).
     - Todos los objetos van calificados con el schema `dbo_cotifly.`, no `dbo.`.
     - La collation `Latin1_General_100_CI_AS_SC_UTF8` se declara EXPLÍCITAMENTE
       en cada columna de texto (nvarchar), no a nivel de base — la base ya
       existe y puede tener su propia collation fijada, no se asume ni se toca.

   Confirmar antes de ejecutar en el entorno real: `dbTarifario_Nexo_Pruebas`
   suena a nombre de instancia de pruebas — validar si producción usa el mismo
   nombre de base o uno distinto (ver docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md §9).

   Decisión de HU-002.4: se ejecuta como script T-SQL directo (no migraciones EF
   Core) porque HU-003 (.NET/EF Core) todavía no existe como opción real.

   Conexión real: host `tbsdatacenter`, base `dbTarifario_Nexo_Pruebas`,
   autenticación Windows (sin usuario/contraseña). Este script NO lee
   `settings.sqlserver_database_url` de `shared/config.py` — es un .sql plano para
   sqlcmd/SSMS, sin Python de por medio; ese setting es para cuando el backend (o un
   runner Python) se conecte, no para correr este script.

   Cómo ejecutarlo:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -i 0001_script_bd_cotifly_sqlserver.sql
   (`-E` = Windows Authentication / trusted connection, sin `-U`/`-P`; `-C` = confiar
   en el certificado del servidor) o vía SSMS/Azure Data Studio conectado con Windows
   Authentication a esa base (no la crea este script). Requiere permiso para
   CREATE SCHEMA/TABLE/VIEW dentro de ella.

   **Restricción real de dónde correrlo:** Windows Authentication solo funciona
   ejecutando el script directo desde Windows — NO desde los contenedores Docker
   (Linux) del proyecto, donde Windows Auth no funciona sin configurar Kerberos
   aparte (no vale la pena para un script de uso único). Se corre local, no vía
   `docker compose exec`.

   Precondición (HU-002.4, condición previa): las migraciones Alembic 0006-0009
   deben estar ya aplicadas sobre el Postgres de origen antes de exportar datos
   históricos — este script solo crea la ESTRUCTURA. **Decisión (HU-002.4,
   2026-09-22): no se migra ningún dato histórico** de Postgres (volumen real
   prácticamente nulo, nada en producción todavía) — todas las tablas quedan
   vacías salvo las 2 de configuración (`MargenesPolitica`, `SapMapeoTrqType`),
   sembradas por este mismo script.

   Nombres de tabla: PascalCase plano, sin prefijo (`Cotizaciones`,
   `SapMapeoTrqType`, etc.) — se probó el prefijo `tbl`/`tblSap` del estándar
   `dotnet-enterprise-architect` primero y el equipo lo retiró (desviación
   explícita, ver docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md §9 y
   docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md línea 218).

   `UsuariosWeb.id` es `UNIQUEIDENTIFIER` (UUID generado por Cotifly), NO el id
   de SAP — no existe columna de id de SAP en este esquema. El aprovisionamiento
   (HU-003.6) hace match contra `tblSAPUsuariosApp` (dbsolucionesti) y contra
   `DimEjecutivo` (DWCubosEBSSAP, para `regional`) por `nombreUsuario`, no por
   ningún id numérico externo — de ahí el índice único filtrado sobre esa
   columna más abajo. `nombreUsuario`/`nombreCompleto`/`email` (mapeo de
   `tblSAPUsuariosApp`, §2.6 de la propuesta) y `regional` (cruce con
   `DimEjecutivo`) se construyen ya, sin poblar hasta HU-004, mismo criterio que
   `costeoId`/`nitCliente`/`lineaNegocioId` en `Cotizaciones`. Postgres tiene el
   equivalente exacto de `nombreUsuario`/`nombreCompleto`/`email` desde
   `db/migrations/versions/0009_usuarios_web_clientepro.py` (snake_case) — sin
   asimetría entre motores en esas 3; `regional` no tiene equivalente en
   Postgres (columna nueva, solo SQL Server).

   Conexión externa nueva requerida (fuera del alcance de este script): la base
   `DWCubosEBSSAP` (tabla `DimEjecutivo`, columna `REGIONAL`) para poblar
   `UsuariosWeb.regional` -- tercera conexión externa de solo lectura del
   backend, junto a `dbsolucionesti` (tblSAPUsuariosApp) y Nexo. No se agrega
   config de conexión en este script (eso es shared/config.py, código de
   aplicación).

   Nota de negocio (sin cambio de esquema, documentado para que no se pierda):
   el rol (Comercial / Gerente Comercial) NO se persiste en UsuariosWeb -- se
   consulta en vivo desde los roles que ya expone la sesión de ClientePro,
   igual que hoy filtra el ítem de menú "Cotizador" (HU-004.3).

   Idempotencia: cada CREATE/ADD está guardado con comprobaciones de existencia,
   así que el script se puede volver a correr sin fallar si ya se ejecutó antes.
   Como primer paso suelta TODO lo que haya quedado de una corrida anterior
   (nombres vigentes, cualquier forma previa) y lo recrea ya en su forma final
   -- sin efecto en una base limpia (no encuentra nada que soltar). IMPORTANTE:
   esto tumba y recrea el schema completo (tablas vacías de prueba, no datos de
   producción) -- no usar este patrón una vez que `dbo_cotifly` tenga datos
   reales que importe conservar; en ese punto, los cambios de esquema futuros
   deben ir en un script nuevo versionado (ej. `0002_*.sql`) que altere sin
   destruir.
   ============================================================================ */

SET NOCOUNT ON;
GO

-- Requerido para el índice filtrado de UsuariosWeb.nombreUsuario más abajo
-- (§3) -- SQL Server exige QUOTED_IDENTIFIER ON para crear índices filtrados/
-- indexed views/índices sobre columnas computadas, y el default de sqlcmd en
-- este entorno vino en OFF (error real encontrado: Msg 1934). SET options son
-- de sesión y persisten entre batches (GO), así que basta con fijarlo una vez
-- acá arriba para todo el resto del script.
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
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
--       (cualquier forma previa), para recrearlo ya en su forma final. Sin
--       efecto en una base limpia (no encuentra nada que soltar).
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
-- de base — ver nota arriba). Un solo lugar para no repetir el literal muchas
-- veces sería ideal, pero T-SQL no tiene variables de tipo/collation
-- reutilizables en DDL — se repite explícitamente en cada columna nvarchar.

-- =============================================================================
-- 1. dbo_cotifly.MargenesPolitica (antes margenes_politica) — sin dependencias
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.MargenesPolitica', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.MargenesPolitica (
        id              INT IDENTITY(1,1)   NOT NULL,
        lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        mapeoLineasNexo NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- Fase 2: nombre equivalente en el enum de Nexo; 4/8 líneas sin equivalente, NULL a propósito
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
--    id: UNIQUEIDENTIFIER (UUID generado por Cotifly) -- NO es el id de SAP,
--    no existe columna de id de SAP en este esquema (ver nota del header).
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.UsuariosWeb (
        id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_usuariosWeb_id DEFAULT (NEWID()),
        nombreUsuario   NVARCHAR(255) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- acotado: es clave de índice único, no puede ser MAX (ver nota §5.8)
        nombreCompleto  NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        email           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        telefono        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        regional        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- Fase 2: cruce nombreUsuario -> DimEjecutivo.ID/REGIONAL (DWCubosEBSSAP)
        createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_usuariosWeb_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        ultimaSesion    DATETIMEOFFSET      NULL,
        CONSTRAINT pk_usuariosWeb PRIMARY KEY (id)
    );
    -- Índice único filtrado sobre nombreUsuario -- es la clave real de "¿ya
    -- existe este usuario?" en el aprovisionamiento (HU-003.6). Filtrado
    -- (WHERE ... IS NOT NULL) en vez de UNIQUE de tabla: un UNIQUE normal en
    -- SQL Server solo permite UNA fila con NULL; con el filtro, cualquier
    -- cantidad de filas sin nombreUsuario todavía (sin poblar hasta HU-004)
    -- conviven sin problema, y la unicidad solo se exige entre las que sí lo
    -- tienen. La collation de este esquema ya es case-insensitive (CI), así
    -- que la comparación/unicidad no distingue mayúsculas/minúsculas a nivel
    -- de motor -- normalizar a mayúscula en la app es una decisión de estilo,
    -- no una necesidad técnica para que el match/índice funcionen.
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'uq_usuariosWeb_nombreUsuario')
    CREATE UNIQUE INDEX uq_usuariosWeb_nombreUsuario ON dbo_cotifly.UsuariosWeb (nombreUsuario) WHERE nombreUsuario IS NOT NULL;
GO

-- =============================================================================
-- 4. dbo_cotifly.Cotizaciones (antes cotizaciones) — FK -> MargenesPolitica,
--    UsuariosWeb, auto-FK (versionado)
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.Cotizaciones (
        id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_cotizaciones_id DEFAULT (NEWID()),
        folio               NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL, -- acotado: no puede ser MAX con UNIQUE, ver nota §5.1
        lineaNegocioId      INT                 NULL,      -- lineaNegocio (texto) se retira, ver §3 del diseño
        clienteNombre       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        nitCliente          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar. Corrección 2026-09-24: reemplaza a clienteIdSap (texto libre, duplicada con bdCliente) y a bdCliente (renombrada). Guarda el NIT, no el clienteId interno de Nexo -- ese id cambia de formato según el origen (BP para clientes reales de DimBpSolicitante, UUID random para prospectos de tblProspectos, sin relación entre sí), el NIT sí es estable y existe para ambos casos.
        origenCliente       NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- Fase 2: 'cliente' (DimBPSolicitante) o 'prospecto' (tblProspectos), según Nexo
        costeoId            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        costo               DECIMAL(18,2)       NULL,
        margenPolitica      DECIMAL(9,4)        NULL,
        margenAplicado      DECIMAL(9,4)        NULL,
        origenMargen        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        metodoCalculo       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        precioFinal         DECIMAL(18,2)       NULL,
        moneda              NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL CONSTRAINT df_cotizaciones_moneda DEFAULT ('COP'),
        trmAplicada         DECIMAL(18,4)       NOT NULL CONSTRAINT df_cotizaciones_trmAplicada DEFAULT (0), -- Fase 2: TRM cuando moneda='USD'; 0 (no NULL) cuando moneda='COP'
        pdfPath             NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        estadoCosteo        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        estadoEnvio         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        usuarioId           UNIQUEIDENTIFIER    NULL, -- HU-004, sin poblar
        spaceId             NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        threadId            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
        ganada              BIT                 NULL, -- HU-005, sin poblar
        motivoRechazo       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL, -- HU-005, sin poblar
        version             INT                 NOT NULL CONSTRAINT df_cotizaciones_version DEFAULT (1), -- Fase 2: versionado -- el folio de una v2+ reutiliza el folio base de la v1 con sufijo -vN (ver shared/models.py:49), no hay FK a la v1 (cotizacionOrigenId se quitó, 2026-09-24)
        marcadoAt           DATETIMEOFFSET      NULL, -- HU-005, sin poblar
        costeoListoAt       DATETIMEOFFSET      NULL, -- Fase 2: momento en que pasó a costeado/autocosteo, ver HU-005.7
        createdAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_cotizaciones_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_cotizaciones PRIMARY KEY (id),
        CONSTRAINT uq_cotizaciones_folio UNIQUE (folio),
        CONSTRAINT fk_cotizaciones_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id),
        CONSTRAINT fk_cotizaciones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
        CONSTRAINT ck_cotizaciones_origenCliente CHECK (origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto'))
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
        usuarioId       UNIQUEIDENTIFIER    NULL, -- HU-004, sin poblar
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
        confirmadoPor   UNIQUEIDENTIFIER    NULL, -- HU-004, sin poblar
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
        usuarioId       UNIQUEIDENTIFIER    NULL, -- HU-004, sin poblar
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

-- =============================================================================
-- 12. Seed de configuración: MargenesPolitica + SapMapeoTrqType
--     (equivalente a db/migrations/versions/0002_seed_config.py del lado Postgres)
--     Sin esto, la app fallaría con "línea de negocio desconocida" en la
--     primera cotización -- no es dato histórico, es configuración que la app
--     necesita desde el día uno. Valores verificados en vivo contra el
--     Postgres de dev, no copiados de la documentación a ciegas -- ver nota
--     de ITR abajo. mapeoLineasNexo: catálogo real de Nexo confirmado con el
--     equipo (2026-09-23); 4 de las 8 líneas no tienen equivalente y quedan
--     NULL a propósito.
-- =============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.MargenesPolitica)
BEGIN
    -- Nota: BASE_DE_DATOS.md afirma que ITR está al 50% ("una al 50%, ITR,
    -- intencional"), pero el dato real (Postgres en vivo + migración
    -- 0002_seed_config.py) es 0.15 igual que las otras 7 líneas -- ese dato
    -- del documento está desactualizado, no se usó acá.
    INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive, mapeoLineasNexo) VALUES
    ('Nacional',          0.15, 'margen', 1, 'Nacional'),
    ('Distribucion',      0.15, 'margen', 1, NULL),
    ('Importacion',       0.15, 'margen', 1, 'PuertoAInterior'),
    ('Exportacion',       0.15, 'margen', 1, 'InteriorAPuerto'),
    ('Internacional',     0.15, 'margen', 1, 'Andino'),
    ('Almacenamiento',    0.15, 'margen', 1, NULL),
    ('Almacenamiento3PL', 0.15, 'margen', 1, NULL),
    ('ITR',               0.15, 'margen', 1, NULL);
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType)
BEGIN
    INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo) VALUES
    ('OPTN', '', 'Nacional', 1),
    ('OPDS', '', 'Distribucion', 1),
    ('OPIM', '', 'Importacion', 1),
    ('OPEX', '', 'Exportacion', 1),
    ('OPTI', '', 'Internacional', 1),
    ('OHUB', '', 'Almacenamiento', 1),
    ('OH3P', '', 'Almacenamiento3PL', 1),
    ('OPSC', '', 'ITR', 1);
END
GO

/* ============================================================================
   Fin del script. Verificación rápida sugerida tras ejecutar:

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'
     ORDER BY TABLE_NAME;
     -- Debe listar las 9 tablas + CorreoDestinatarios (10 en total).

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS
     WHERE TABLE_SCHEMA = 'dbo_cotifly' ORDER BY TABLE_NAME;
     -- Debe listar vAjustesMargenPorComercial y vActividadPorComercial.

     SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_NAME = 'UsuariosWeb' AND COLUMN_NAME = 'id';
     -- Debe devolver 'uniqueidentifier'.

     SELECT lineaNegocio, margen, mapeoLineasNexo FROM dbo_cotifly.MargenesPolitica ORDER BY lineaNegocio;
     -- 8 filas, margen=0.15 en todas; mapeoLineasNexo poblado en 4, NULL en las otras 4.

     SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType;
     -- Debe devolver 8.
   ============================================================================ */
