/* ============================================================================
   Cotifly — Esquema completo de dbo_cotifly para una instalación desde cero (SQL Server 2019 o posterior)
   ============================================================================
   Crea el schema `dbo_cotifly` con TODAS sus tablas, columnas, llaves, restricciones, índices y el
   procedimiento almacenado, en su forma vigente. Es el mismo esquema que tiene la base compartida
   (`dbTarifario_Nexo_Pruebas`): el que dejan las migraciones de `db/migrations_mssql/` (0001 a 0010)
   seguidas de los ajustes `db/scripts/02` a `22`, pero en un solo paso.

   Solo estructura: NO inserta datos. La configuración inicial (líneas de negocio, mapeo de SAP TM,
   roles, tasa del seguro y política de seguros) la siembran los archivos de `db/seeder/`.

   Uso: SOLO en una base donde `dbo_cotifly` todavía no tiene tablas. Si ya las tiene, el script
   avisa y no ejecuta nada (no suelta, no recrea y no crea tablas sueltas; vale en sqlcmd y en SSMS).
   Una base existente se pone al día con los ajustes de `db/scripts/` (02 en adelante), que son
   idempotentes: sobre una base instalada con este script no cambian nada. El ejecutador
   (`db/ejecutores/ejecutar-bd.ps1` / `.sh`) decide solo: base sin tablas -> este script; después,
   en los dos casos, los ajustes y los seeders.

   Todo corre en UNA transacción (XACT_ABORT): con sqlcmd -b (o con el ejecutador), si algo falla no
   queda nada creado. En SSMS, ejecutarlo completo y revisar el primer error antes de reintentar.

   NO crea la base de datos: se conecta a la que indique `-d` (sqlcmd) y crea el schema adentro.
   El ejecutador puede crearla con `-CrearBase` / `-c` (collation Latin1_General_100_CI_AS_SC_UTF8).

   Collation: `Latin1_General_100_CI_AS_SC_UTF8` explícita en cada columna de texto (la base puede
   tener otra y no se le toca). Excepciones, a propósito:
     - `Latin1_General_100_BIN2` (comparación exacta) en TokensRefresco.tokenHash y en
       CapturasIdempotentes.clave y .huella;
     - SapMargenCliente, SapMargenSyncLog y CotizacionArchivosLicitacion.origenLectura toman la
       collation de la base, como las crean los ajustes 17 y 22: así una base nueva y una base
       ajustada quedan idénticas.

   Nombres de restricciones: son los mismos que deja una base migrada y ajustada. Por eso las tablas
   renombradas (CotizacionesSolicitud, CotizacionMercancias) conservan en sus restricciones los
   nombres `cotizaciones` y `cotizacionPosiciones`. El orden de las columnas es el de una base
   instalada antes y ajustada después (las columnas de los ajustes van al final de cada tabla).

   Mantenimiento: todo cambio de esquema se hace con un ajuste nuevo en `db/scripts/` (idempotente)
   Y el mismo cambio aquí, en el mismo PR. La prueba es comparar el catálogo de una base instalada
   con este script contra una base con las migraciones y todos los ajustes: deben quedar idénticas
   (procedimiento en db/README.md, "Verificar").

   Cómo ejecutarlo a mano:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i cotifly_esquema.sql
   (-E = autenticación de Windows, -C = confiar en el certificado, -I = QUOTED_IDENTIFIER ON, que
   exigen los índices filtrados, -b = detenerse en el primer error, -f 65001 = leer el archivo como
   UTF-8). Requiere permiso para CREATE SCHEMA, TABLE y PROCEDURE.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondición, transacción y schema
-- =============================================================================
-- Si ya hay tablas: avisa y apaga la ejecución (SET NOEXEC ON) para que NINGÚN bloque siguiente cree nada,
-- ni siquiera en SSMS, que de otro modo seguiría después del error. Al final se vuelve a encender.
IF EXISTS (SELECT 1 FROM sys.schemas AS s JOIN sys.tables AS t ON t.schema_id = s.schema_id WHERE s.name = N'dbo_cotifly')
BEGIN
    RAISERROR (N'dbo_cotifly ya tiene tablas: este script es solo para una instalación desde cero y no se ejecutó nada. Para una base existente use los ajustes de db/scripts/ (o el ejecutador, que lo decide solo).', 16, 1);
    SET NOEXEC ON;
END
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dbo_cotifly')
    EXEC (N'CREATE SCHEMA dbo_cotifly');
GO

-- =============================================================================
-- 1. Catálogos de configuración
-- =============================================================================

-- Líneas de negocio y su margen de política. `mapeoLineasNexo` = nombre equivalente en el
-- enum LineaNegocio de Nexo (NULL a propósito para las que no tienen equivalente).
CREATE TABLE dbo_cotifly.MargenesPolitica (
    id              INT IDENTITY(1,1)   NOT NULL,
    lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    mapeoLineasNexo NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    margen          DECIMAL(9,4)        NOT NULL,
    metodoCalculo   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL CONSTRAINT df_margenesPolitica_metodoCalculo DEFAULT ('margen'),
    isActive        BIT                 NOT NULL CONSTRAINT df_margenesPolitica_isActive DEFAULT (1),
    updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_margenesPolitica_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_margenesPolitica PRIMARY KEY (id),
    CONSTRAINT uq_margenesPolitica_lineaNegocio UNIQUE (lineaNegocio)
);
GO

-- Mapeo del TrqType de SAP TM a la línea de negocio. `lineaNegocioId` es la referencia a la línea
-- (FK); `lineaNegocio` conserva el texto mientras el backend lo siga leyendo.
CREATE TABLE dbo_cotifly.SapMapeoTrqType (
    trqType         NVARCHAR(8)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    salesOrg        NVARCHAR(12)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL CONSTRAINT df_sapMapeoTrqType_salesOrg DEFAULT (''),
    lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    activo          BIT                 NOT NULL CONSTRAINT df_sapMapeoTrqType_activo DEFAULT (1),
    lineaNegocioId  INT                 NOT NULL,
    CONSTRAINT pk_sapMapeoTrqType PRIMARY KEY (trqType, salesOrg),
    CONSTRAINT fk_sapMapeoTrqType_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id)
);
GO

-- Tasa del seguro de la mercancía, con su historia: una sola activa (índice único filtrado); las
-- anteriores se desactivan, no se borran. DECIMAL(9,6): 0.003450 = 0,345 % sin redondeo.
CREATE TABLE dbo_cotifly.TarifasSeguro (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_tarifasSeguro_id DEFAULT (NEWID()),
    tasa            DECIMAL(9,6)        NOT NULL,
    vigenteDesde    DATETIMEOFFSET      NOT NULL,
    isActive        BIT                 NOT NULL CONSTRAINT df_tarifasSeguro_isActive DEFAULT (1),
    updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_tarifasSeguro_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_tarifasSeguro PRIMARY KEY (id),
    CONSTRAINT ck_tarifasSeguro_tasa CHECK (tasa > 0 AND tasa < 1)
);
GO

CREATE UNIQUE INDEX uq_tarifasSeguro_activa ON dbo_cotifly.TarifasSeguro (isActive) WHERE isActive = 1;
GO

-- Política de seguros por valor de la mercancía. Las reglas viven en datos (un cambio de la política
-- es un cambio de filas):
--   PoliticaSeguroTramos ──< PoliticaSeguroTramoMedidas >── PoliticaSeguroMedidas ──< PoliticaSeguroMedidaConexos
-- Tramo: valorDesde EXCLUSIVO y valorHasta INCLUIDO; valorHasta NULL = sin tope. Solo un tramo activo
-- por valorDesde.
CREATE TABLE dbo_cotifly.PoliticaSeguroTramos (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroTramos_id DEFAULT (NEWID()),
    nombre              NVARCHAR(60)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    valorDesde          DECIMAL(18,2)       NOT NULL,   -- exclusivo
    valorHasta          DECIMAL(18,2)       NULL,       -- incluido; NULL = sin tope
    isActive            BIT                 NOT NULL CONSTRAINT df_politicaSeguroTramos_isActive DEFAULT (1),
    updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroTramos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_politicaSeguroTramos PRIMARY KEY (id),
    CONSTRAINT ck_politicaSeguroTramos_valores CHECK (valorDesde >= 0 AND (valorHasta IS NULL OR valorHasta > valorDesde))
);
GO

CREATE UNIQUE INDEX uq_politicaSeguroTramos_valorDesde_activo
    ON dbo_cotifly.PoliticaSeguroTramos (valorDesde) WHERE isActive = 1;
GO

-- Catálogo de medidas de seguridad. esCobrable = 1 en las que se cumplen agregando un conexo de Nexo.
CREATE TABLE dbo_cotifly.PoliticaSeguroMedidas (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroMedidas_id DEFAULT (NEWID()),
    codigo              NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    nombre              NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    esCobrable          BIT                 NOT NULL CONSTRAINT df_politicaSeguroMedidas_esCobrable DEFAULT (0),
    isActive            BIT                 NOT NULL CONSTRAINT df_politicaSeguroMedidas_isActive DEFAULT (1),
    updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroMedidas_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_politicaSeguroMedidas PRIMARY KEY (id),
    CONSTRAINT uq_politicaSeguroMedidas_codigo UNIQUE (codigo)
);
GO

-- Qué medida aplica en cada tramo y con qué nivel ('recomendada' u 'obligatoria').
CREATE TABLE dbo_cotifly.PoliticaSeguroTramoMedidas (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroTramoMedidas_id DEFAULT (NEWID()),
    tramoId             UNIQUEIDENTIFIER    NOT NULL,
    medidaId            UNIQUEIDENTIFIER    NOT NULL,
    nivel               NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroTramoMedidas_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_politicaSeguroTramoMedidas PRIMARY KEY (id),
    CONSTRAINT uq_politicaSeguroTramoMedidas_tramoId_medidaId UNIQUE (tramoId, medidaId),
    CONSTRAINT fk_politicaSeguroTramoMedidas_tramoId FOREIGN KEY (tramoId) REFERENCES dbo_cotifly.PoliticaSeguroTramos (id),
    CONSTRAINT fk_politicaSeguroTramoMedidas_medidaId FOREIGN KEY (medidaId) REFERENCES dbo_cotifly.PoliticaSeguroMedidas (id),
    CONSTRAINT ck_politicaSeguroTramoMedidas_nivel CHECK (nivel IN ('recomendada', 'obligatoria'))
);
GO

CREATE INDEX ix_politicaSeguroTramoMedidas_medidaId ON dbo_cotifly.PoliticaSeguroTramoMedidas (medidaId);
GO

-- Códigos del catálogo de Nexo que cumplen cada medida (sin FK: es otra base). La medida se cumple con
-- CUALQUIERA de sus códigos.
CREATE TABLE dbo_cotifly.PoliticaSeguroMedidaConexos (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroMedidaConexos_id DEFAULT (NEWID()),
    medidaId            UNIQUEIDENTIFIER    NOT NULL,
    codigoServicio      NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- catálogo de Nexo, sin FK
    updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroMedidaConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_politicaSeguroMedidaConexos PRIMARY KEY (id),
    CONSTRAINT uq_politicaSeguroMedidaConexos_medidaId_codigoServicio UNIQUE (medidaId, codigoServicio),
    CONSTRAINT fk_politicaSeguroMedidaConexos_medidaId FOREIGN KEY (medidaId) REFERENCES dbo_cotifly.PoliticaSeguroMedidas (id)
);
GO

-- =============================================================================
-- 2. Identidad: usuarios, roles y tokens
-- =============================================================================

-- `id` lo genera Cotifly (no es el id de SAP). `idUsuarioExterno` = tblSAPUsuariosApp.ID.
CREATE TABLE dbo_cotifly.UsuariosWeb (
    id               UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_usuariosWeb_id DEFAULT (NEWID()),
    nombreUsuario    NVARCHAR(255) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    nombreCompleto   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    email            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    telefono         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    regional         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt        DATETIMEOFFSET      NOT NULL CONSTRAINT df_usuariosWeb_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    ultimaSesion     DATETIMEOFFSET      NULL,
    idUsuarioExterno BIGINT              NULL,
    CONSTRAINT pk_usuariosWeb PRIMARY KEY (id)
);
GO

CREATE UNIQUE INDEX uq_usuariosWeb_nombreUsuario ON dbo_cotifly.UsuariosWeb (nombreUsuario) WHERE nombreUsuario IS NOT NULL;
CREATE UNIQUE INDEX uq_usuariosWeb_idUsuarioExterno ON dbo_cotifly.UsuariosWeb (idUsuarioExterno) WHERE idUsuarioExterno IS NOT NULL;
GO

-- Roles internos y su rol externo de ClientePro (idRolExterno NULL = nadie entra con ese rol).
CREATE TABLE dbo_cotifly.Roles (
    id              INT IDENTITY(1,1)   NOT NULL,
    codigo          NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    descripcion     NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    idRolExterno    INT                 NULL,
    activo          BIT                 NOT NULL CONSTRAINT df_roles_activo DEFAULT (1),
    updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_roles_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_roles PRIMARY KEY (id),
    CONSTRAINT uq_roles_codigo UNIQUE (codigo),
    CONSTRAINT ck_roles_codigo CHECK (codigo IN ('comercial', 'gerente_comercial', 'director'))
);
GO

CREATE UNIQUE INDEX uq_roles_idRolExterno ON dbo_cotifly.Roles (idRolExterno) WHERE idRolExterno IS NOT NULL;
GO

-- Roles internos asignados a cada usuario: varias asignaciones activas por usuario (multi-rol), pero
-- el mismo rol no se asigna dos veces (uq_usuariosRoles_usuarioId_rolId).
CREATE TABLE dbo_cotifly.UsuariosRoles (
    id              INT IDENTITY(1,1)   NOT NULL,
    usuarioId       UNIQUEIDENTIFIER    NOT NULL,
    rolId           INT                 NOT NULL,
    asignadoAt      DATETIMEOFFSET      NOT NULL CONSTRAINT df_usuariosRoles_asignadoAt DEFAULT (SYSDATETIMEOFFSET()),
    activo          BIT                 NOT NULL CONSTRAINT df_usuariosRoles_activo DEFAULT (1),
    revisadoPor     UNIQUEIDENTIFIER    NULL,
    revisadoAt      DATETIMEOFFSET      NULL,
    CONSTRAINT pk_usuariosRoles PRIMARY KEY (id),
    CONSTRAINT uq_usuariosRoles_usuarioId_rolId UNIQUE (usuarioId, rolId),
    CONSTRAINT fk_usuariosRoles_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT fk_usuariosRoles_rolId FOREIGN KEY (rolId) REFERENCES dbo_cotifly.Roles (id),
    CONSTRAINT fk_usuariosRoles_revisadoPor FOREIGN KEY (revisadoPor) REFERENCES dbo_cotifly.UsuariosWeb (id)
);
GO

-- Refresh tokens opacos: solo se guarda el SHA-256 en hexadecimal (64 caracteres). `rolSesion` = rol
-- interno con el que se emitió la sesión (el refresh lo conserva).
CREATE TABLE dbo_cotifly.TokensRefresco (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_tokensRefresco_id DEFAULT (NEWID()),
    usuarioId           UNIQUEIDENTIFIER    NOT NULL,
    tokenHash           CHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
    familiaId           UNIQUEIDENTIFIER    NOT NULL,
    createdAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_tokensRefresco_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    expiraAt            DATETIMEOFFSET      NOT NULL,
    revocadoAt          DATETIMEOFFSET      NULL,
    reemplazadoPorId    UNIQUEIDENTIFIER    NULL,
    rolSesion           NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    CONSTRAINT pk_tokensRefresco PRIMARY KEY (id),
    CONSTRAINT uq_tokensRefresco_tokenHash UNIQUE (tokenHash),
    CONSTRAINT fk_tokensRefresco_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT fk_tokensRefresco_reemplazadoPorId FOREIGN KEY (reemplazadoPorId) REFERENCES dbo_cotifly.TokensRefresco (id),
    CONSTRAINT ck_tokensRefresco_vigencia CHECK (expiraAt > createdAt),
    CONSTRAINT ck_tokensRefresco_rolSesion CHECK (rolSesion IS NULL OR rolSesion IN ('comercial', 'gerente_comercial', 'director'))
);
GO

CREATE INDEX ix_tokensRefresco_usuarioId ON dbo_cotifly.TokensRefresco (usuarioId);
CREATE INDEX ix_tokensRefresco_familiaId ON dbo_cotifly.TokensRefresco (familiaId);
GO

-- jti de los access tokens revocados en logout, hasta su vencimiento.
CREATE TABLE dbo_cotifly.TokensRevocados (
    jti                 NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    usuarioId           UNIQUEIDENTIFIER    NULL,
    expiraAt            DATETIMEOFFSET      NOT NULL,
    createdAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_tokensRevocados_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_tokensRevocados PRIMARY KEY (jti),
    CONSTRAINT fk_tokensRevocados_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id)
);
GO

CREATE INDEX ix_tokensRevocados_expiraAt ON dbo_cotifly.TokensRevocados (expiraAt);
GO

-- Idempotency-Key persistente: una fila por clave, comercial y operación (capturar_costeo | conexos |
-- re_solicitar_costeo | editar_solicitud). Sin FK a la cotización a propósito: la fila se reserva antes
-- de que exista el folio y se purga sola al vencer (expiraAt, 24 h).
CREATE TABLE dbo_cotifly.CapturasIdempotentes (
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_capturasIdempotentes_id DEFAULT (NEWID()),
    usuarioId         UNIQUEIDENTIFIER NOT NULL,                                          -- el comercial (UsuariosWeb.id)
    operacion         NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- capturar_costeo | conexos | re_solicitar_costeo | editar_solicitud
    clave             NVARCHAR(64)  COLLATE Latin1_General_100_BIN2 NOT NULL,             -- Idempotency-Key (8 a 64), exacta
    huella            CHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,             -- SHA-256 del cuerpo, en hex
    estado            NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- en_curso | terminada | libre
    tomadaPor         UNIQUEIDENTIFIER NULL,                                              -- la petición que la tiene o la tuvo
    enCursoHasta      DATETIMEOFFSET NULL,                                                -- vence la toma (huérfana tras una caída)
    resultado         NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,       -- JSON de la respuesta guardada
    cotizacionId      UNIQUEIDENTIFIER NULL,                                              -- captura: el folio confirmado
    folio             NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    solicitudNexoId   NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,       -- captura: id de Nexo en cuanto se conoce
    scorePrioridad    DECIMAL(9,4)  NULL,                                                 -- captura: el que Nexo devolvió con el id
    createdAt         DATETIMEOFFSET NOT NULL CONSTRAINT df_capturasIdempotentes_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt         DATETIMEOFFSET NOT NULL CONSTRAINT df_capturasIdempotentes_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    expiraAt          DATETIMEOFFSET NOT NULL,                                            -- 24 h
    CONSTRAINT pk_capturasIdempotentes PRIMARY KEY (id),
    CONSTRAINT uq_capturasIdempotentes_usuarioId_operacion_clave UNIQUE (usuarioId, operacion, clave),
    CONSTRAINT ck_capturasIdempotentes_estado CHECK (estado IN (N'en_curso', N'terminada', N'libre')),
    CONSTRAINT ck_capturasIdempotentes_clave CHECK (LEN(clave) BETWEEN 8 AND 64),
    CONSTRAINT ck_capturasIdempotentes_operacion CHECK (LEN(operacion) BETWEEN 1 AND 40),
    CONSTRAINT ck_capturasIdempotentes_enCurso CHECK (estado <> N'en_curso' OR (tomadaPor IS NOT NULL AND enCursoHasta IS NOT NULL)),
    CONSTRAINT ck_capturasIdempotentes_terminada CHECK (estado <> N'terminada' OR resultado IS NOT NULL),
    CONSTRAINT ck_capturasIdempotentes_resultado_json CHECK (resultado IS NULL OR ISJSON(resultado) = 1)
);
GO

-- Limpieza de vencidas y reintento de un folio con cualquier clave.
CREATE INDEX ix_capturasIdempotentes_expiraAt ON dbo_cotifly.CapturasIdempotentes (expiraAt);
CREATE INDEX ix_capturasIdempotentes_folio ON dbo_cotifly.CapturasIdempotentes (folio)
    INCLUDE (solicitudNexoId, scorePrioridad, expiraAt)
    WHERE folio IS NOT NULL;
GO

-- =============================================================================
-- 3. Conversación del agente
-- =============================================================================

-- Una sesión = un hilo de Google Chat o de la consola web. `estadoAgente` = JSON del slot-filling.
CREATE TABLE dbo_cotifly.Sesiones (
    spaceId         NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    threadId        NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    estado          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    ultimaActividad DATETIMEOFFSET      NOT NULL CONSTRAINT df_sesiones_ultimaActividad DEFAULT (SYSDATETIMEOFFSET()),
    estadoAgente    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    usuarioId       UNIQUEIDENTIFIER    NULL,
    CONSTRAINT pk_sesiones PRIMARY KEY (spaceId, threadId),
    CONSTRAINT ck_sesiones_estado CHECK (estado IS NULL OR estado IN ('activa', 'archivada')),
    CONSTRAINT ck_sesiones_estadoAgente_json CHECK (estadoAgente IS NULL OR ISJSON(estadoAgente) = 1),
    CONSTRAINT fk_sesiones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id)
);
GO

CREATE INDEX ix_sesiones_ultimaActividad ON dbo_cotifly.Sesiones (ultimaActividad) INCLUDE (estado);
CREATE INDEX ix_sesiones_usuarioId ON dbo_cotifly.Sesiones (usuarioId);
GO

CREATE TABLE dbo_cotifly.EventosConversacion (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosConversacion_id DEFAULT (NEWID()),
    spaceId         NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    threadId        NVARCHAR(128) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    usuarioId       UNIQUEIDENTIFIER    NULL,
    rol             NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    contenido       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    herramientas    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_eventosConversacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_eventosConversacion PRIMARY KEY (id),
    CONSTRAINT fk_eventosConversacion_sesion FOREIGN KEY (spaceId, threadId) REFERENCES dbo_cotifly.Sesiones (spaceId, threadId),
    CONSTRAINT fk_eventosConversacion_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT ck_eventosConversacion_herramientas_json CHECK (herramientas IS NULL OR ISJSON(herramientas) = 1)
);
GO

CREATE INDEX ix_eventosConversacion_sesion_createdAt ON dbo_cotifly.EventosConversacion (spaceId, threadId, createdAt);
GO

CREATE TABLE dbo_cotifly.EventosHerramienta (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_eventosHerramienta_id DEFAULT (NEWID()),
    herramienta     NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    input           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    outputResumen   NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    exito           BIT                 NULL,
    duracionMs      INT                 NULL,
    threadId        NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_eventosHerramienta_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_eventosHerramienta PRIMARY KEY (id),
    CONSTRAINT ck_eventosHerramienta_input_json CHECK (input IS NULL OR ISJSON(input) = 1),
    CONSTRAINT ck_eventosHerramienta_outputResumen_json CHECK (outputResumen IS NULL OR ISJSON(outputResumen) = 1)
);
GO

-- =============================================================================
-- 4. Cotizaciones
-- =============================================================================

-- La solicitud (cabecera): cliente, estado general, PDF, seguimiento del costeo con Nexo, precio total,
-- resultado diferido a Nexo y finalización.
CREATE TABLE dbo_cotifly.CotizacionesSolicitud (
    id                          UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizaciones_id DEFAULT (NEWID()),
    folio                       NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    clienteNombre               NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    nitCliente                  NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    origenCliente               NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- 'cliente' | 'prospecto'
    tipoSolicitud               NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- 'spot' | 'licitacion'
    deadline                    DATETIMEOFFSET   NULL,
    precioFinal                 DECIMAL(18,2)    NULL,                                         -- suma de precioFinalLinea de sus rutas
    moneda                      NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL CONSTRAINT df_cotizaciones_moneda DEFAULT ('COP'),
    trmAplicada                 DECIMAL(18,4)    NOT NULL CONSTRAINT df_cotizaciones_trmAplicada DEFAULT (0),   -- 0 cuando moneda = COP
    pdfPath                     NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    pdfDesactualizado           BIT              NOT NULL CONSTRAINT df_cotizaciones_pdfDesactualizado DEFAULT (0),
    usuarioId                   UNIQUEIDENTIFIER NULL,
    spaceId                     NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    threadId                    NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    ganada                      BIT              NULL,
    motivoRechazo               NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    version                     INT              NOT NULL CONSTRAINT df_cotizaciones_version DEFAULT (1),
    versionNegocio              INT              NOT NULL CONSTRAINT df_cotizaciones_versionNegocio DEFAULT (0),   -- concurrencia optimista
    marcadoAt                   DATETIMEOFFSET   NULL,
    costeoListoAt               DATETIMEOFFSET   NULL,
    createdAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizaciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizaciones_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    estadoEnvio                 NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    estadoCosteo                NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- resumen de sus rutas
    cotizacionNexoId            NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    solicitudNexoId             NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- id de la SOLICITUD de Nexo
    motivoEstadoCosteo          NVARCHAR(1000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    costeoEnviadoAt             DATETIMEOFFSET   NULL,
    costeoSiguienteConsultaAt   DATETIMEOFFSET   NULL,
    costeoConsultasFallidas     INT              NOT NULL CONSTRAINT df_cotizaciones_costeoConsultasFallidas DEFAULT (0),
    textosCotizacion            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- JSON: asunto e introducción del PDF
    notas                      NVARCHAR(2000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- nota para la Mesa de Nexo
    scorePrioridad              DECIMAL(9,4)     NULL,   -- scorePrioridad que Nexo devuelve al crear la solicitud
    -- Resultado (ganada/perdida) guardado en Cotifly mientras la cotización no esté entregada en Nexo;
    -- el polling del costeo lo envía una sola vez cuando la Mesa la entregue.
    sincronizacionResultadoNexo NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- pendiente | sincronizado | no_sincronizable
    comentarioResultadoPendiente NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    resultadoSincronizadoAt     DATETIMEOFFSET   NULL,
    errorSincronizacionResultado NVARCHAR(1000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    intentosSincronizacionResultado INT          NOT NULL CONSTRAINT df_cotizaciones_intentosSincronizacionResultado DEFAULT (0),
    -- Finalización: el comercial acepta el precio y desde ahí queda congelado. Aparte de estadoCosteo,
    -- que el polling recalcula desde Nexo en cada vuelta.
    finalizadaAt                DATETIMEOFFSET   NULL,
    finalizadaPor               UNIQUEIDENTIFIER NULL,
    CONSTRAINT pk_cotizaciones PRIMARY KEY (id),
    CONSTRAINT uq_cotizaciones_folio UNIQUE (folio),
    CONSTRAINT fk_cotizaciones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT fk_cotizaciones_finalizadaPor FOREIGN KEY (finalizadaPor) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT ck_cotizaciones_origenCliente CHECK (origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto')),
    CONSTRAINT ck_cotizaciones_tipoSolicitud CHECK (tipoSolicitud IS NULL OR tipoSolicitud IN ('spot', 'licitacion')),
    CONSTRAINT ck_cotizaciones_estadoEnvio CHECK (estadoEnvio IS NULL OR estadoEnvio IN ('sin_enviar', 'enviada', 'reenviada')),
    CONSTRAINT ck_cotizaciones_versionNegocio CHECK (versionNegocio >= 0),
    CONSTRAINT ck_cotizaciones_textosCotizacion_json CHECK (textosCotizacion IS NULL OR ISJSON(textosCotizacion) = 1),
    CONSTRAINT ck_cotizaciones_estadoCosteo CHECK (
        estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido', 'rechazada')),
    CONSTRAINT ck_cotizaciones_sincronizacionResultadoNexo
        CHECK (sincronizacionResultadoNexo IS NULL OR sincronizacionResultadoNexo IN ('pendiente', 'sincronizado', 'no_sincronizable')),
    CONSTRAINT ck_cotizaciones_intentosSincronizacionResultado CHECK (intentosSincronizacionResultado >= 0),
    CONSTRAINT ck_cotizaciones_finalizada
        CHECK ((finalizadaAt IS NULL AND finalizadaPor IS NULL) OR (finalizadaAt IS NOT NULL AND finalizadaPor IS NOT NULL))
);
GO

CREATE INDEX ix_cotizaciones_usuarioId_createdAt ON dbo_cotifly.CotizacionesSolicitud (usuarioId, createdAt DESC);
-- Agenda del polling: solo las solicitudes que todavía se consultan en Nexo.
CREATE INDEX ix_cotizaciones_costeoSiguienteConsultaAt ON dbo_cotifly.CotizacionesSolicitud (costeoSiguienteConsultaAt)
    WHERE costeoSiguienteConsultaAt IS NOT NULL;
GO

-- La ruta: una fila por ruta que pidió el comercial. Tiene lo que es igual para todos sus camiones,
-- los totales de costo, el margen, el precio, el resultado del cubicador, la cola de la cubicación en
-- segundo plano, el vehículo que eligió el comercial y el seguro de la mercancía.
CREATE TABLE dbo_cotifly.CotizacionLineasSolicitud (
    id                          UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_id DEFAULT (NEWID()),
    cotizacionSolicitudId       UNIQUEIDENTIFIER NOT NULL,
    orden                       INT              NOT NULL,
    rutaOrigenIndice            INT              NULL,   -- fila del Excel de la que salió (base 0); 0 en una cotización simple
    lineaNegocioId              INT              NULL,
    origenMunicipioId           INT              NULL,   -- id de tbl_municipios de Nexo (sin FK: es de otra base)
    origenNombre                NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre
    destinoMunicipioId          INT              NULL,
    destinoNombre               NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre
    tipologia                   NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    claseViaje                  NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    claseContenedor             NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    exclusivo                   BIT              NULL,
    consolidado                 BIT              NULL,
    retornaContenedor           BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_retornaContenedor DEFAULT (0),
    levanteContenedorDiferente  BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_levanteContenedorDiferente DEFAULT (0),
    ciudadDevolucionMunicipioId INT              NULL,
    ciudadDevolucionNombre      NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre
    ciudadLevanteMunicipioId    INT              NULL,
    ciudadLevanteNombre         NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre
    horasCargue                 DECIMAL(6,2)     NULL,
    horasDescargue              DECIMAL(6,2)     NULL,
    pesoKg                      DECIMAL(12,2)    NULL,   -- peso total que declaró el comercial para la ruta
    valorMercancia              DECIMAL(18,2)    NULL,   -- cuánto vale la mercancía (no es un costo)
    valorLevanteContenedor      DECIMAL(18,2)    NULL,   -- un levante; se copia a cada camión
    costoFletesTotal            DECIMAL(18,2)    NULL,
    costoRetornoContenedorTotal DECIMAL(18,2)    NULL,
    costoLevanteContenedorTotal DECIMAL(18,2)    NULL,
    costoServiciosConexos       DECIMAL(18,2)    NULL,
    costoFinal                  DECIMAL(18,2)    NULL,   -- fletes + retorno + levante + conexos
    margenPoliticaSugerido      DECIMAL(9,4)     NULL,
    margenSAPSugerido           DECIMAL(9,4)     NULL,
    margenAplicado              DECIMAL(9,4)     NULL,
    origenMargen                NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    metodoCalculo               NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    precioFinalLinea            DECIMAL(18,2)    NULL,   -- costoFinal con el margen aplicado
    estadoCosteo                NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- resumen de sus camiones
    createdAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt                   DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    -- Resultado del cubicador.
    idRecomendacion             UNIQUEIDENTIFIER NULL,   -- corrida del cubicador, para reconsultarla
    resultadoCubicacion         NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- ASIGNADO | SIN_SELECCION_AUTOMATICA
    motivoSinSeleccion          NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    condicionada                BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_condicionada DEFAULT (0),
    -- Cola de la cubicación de la licitación en segundo plano. estadoCubicacion NULL = la ruta se cubicó al guardarla.
    estadoCubicacion            NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- pendiente | cubicada | fallida
    cubicacionIntentos          INT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_cubicacionIntentos DEFAULT (0),
    cubicacionDisponibleAt      DATETIMEOFFSET   NULL,   -- desde cuándo una réplica puede tomar la ruta
    errorCubicacion             NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    avisosCubicacion            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- JSON de advertencias del cubicador
    pesoConTaraKg               DECIMAL(12,2)    NULL,   -- peso con la tara de los contenedores (clase de viaje Contenedor)
    -- Vehículo que eligió el comercial: respaldo cuando el cubicador no puede o no escoge.
    tipoVehiculoElegido         NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    carroceriaElegida           NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    -- Seguro de la mercancía: aparte de costoFinal y sin margen. La tasa se copia para que un cambio de
    -- tasa no mueva las cotizaciones ya hechas.
    incluyeSeguro               BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_incluyeSeguro DEFAULT (0),
    tasaSeguroAplicada          DECIMAL(9,6)     NULL,
    costoSeguro                 DECIMAL(18,2)    NULL,   -- valorMercancia × tasaSeguroAplicada
    CONSTRAINT pk_cotizacionLineasSolicitud PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionLineasSolicitud_cotizacionId FOREIGN KEY (cotizacionSolicitudId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_cotizacionLineasSolicitud_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id),
    CONSTRAINT uq_cotizacionLineasSolicitud_cotizacionId_orden UNIQUE (cotizacionSolicitudId, orden),
    CONSTRAINT ck_cotizacionLineasSolicitud_orden CHECK (orden >= 1),
    CONSTRAINT ck_cotizacionLineasSolicitud_filaOrigen CHECK (rutaOrigenIndice IS NULL OR rutaOrigenIndice >= 0),
    CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo CHECK (
        estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido', 'rechazada')),
    CONSTRAINT ck_cotizacionLineasSolicitud_municipios CHECK (
        (origenMunicipioId IS NULL OR origenMunicipioId > 0) AND (destinoMunicipioId IS NULL OR destinoMunicipioId > 0)),
    CONSTRAINT ck_cotizacionLineasSolicitud_municipiosContenedor CHECK (
        (ciudadDevolucionMunicipioId IS NULL OR ciudadDevolucionMunicipioId > 0)
        AND (ciudadLevanteMunicipioId IS NULL OR ciudadLevanteMunicipioId > 0)),
    CONSTRAINT ck_cotizacionLineasSolicitud_costos CHECK (
        (valorLevanteContenedor IS NULL OR valorLevanteContenedor >= 0)
        AND (costoFletesTotal IS NULL OR costoFletesTotal >= 0)
        AND (costoRetornoContenedorTotal IS NULL OR costoRetornoContenedorTotal >= 0)
        AND (costoLevanteContenedorTotal IS NULL OR costoLevanteContenedorTotal >= 0)
        AND (costoServiciosConexos IS NULL OR costoServiciosConexos >= 0)
        AND (costoFinal IS NULL OR costoFinal >= 0)
        AND (precioFinalLinea IS NULL OR precioFinalLinea >= 0)),
    CONSTRAINT ck_cotizacionLineasSolicitud_horas CHECK (
        (horasCargue IS NULL OR (horasCargue >= 0 AND horasCargue <= 240))
        AND (horasDescargue IS NULL OR (horasDescargue >= 0 AND horasDescargue <= 240))),
    CONSTRAINT ck_cotizacionLineasSolicitud_origenMargen CHECK (
        origenMargen IS NULL OR origenMargen IN ('sap_tm', 'politica_linea', 'negociado')),
    CONSTRAINT ck_cotizacionLineasSolicitud_resultadoCubicacion CHECK (
        resultadoCubicacion IS NULL OR resultadoCubicacion IN ('ASIGNADO', 'SIN_SELECCION_AUTOMATICA')),
    -- El motivo solo existe cuando el cubicador no pudo escoger (ISNULL: un resultado NULL no lo burla).
    CONSTRAINT ck_cotizacionLineasSolicitud_motivoSinSeleccion CHECK (
        motivoSinSeleccion IS NULL
        OR (motivoSinSeleccion IN ('EXTRADIMENSIONADO', 'SIN_CANDIDATOS_DIMENSIONALES', 'SUPERA_K_MAX',
                                   'SIN_CARROCERIA_COMPATIBLE', 'BUSQUEDA_AGOTADA')
            AND ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA')),
    CONSTRAINT ck_cotizacionLineasSolicitud_estadoCubicacion CHECK (
        estadoCubicacion IS NULL OR estadoCubicacion IN ('pendiente', 'cubicada', 'fallida')),
    CONSTRAINT ck_cotizacionLineasSolicitud_cubicacionIntentos CHECK (cubicacionIntentos >= 0),
    CONSTRAINT ck_cotizacionLineasSolicitud_avisosCubicacion CHECK (avisosCubicacion IS NULL OR ISJSON(avisosCubicacion) = 1),
    CONSTRAINT ck_cotizacionLineasSolicitud_pesoConTaraKg
        CHECK (pesoConTaraKg IS NULL OR pesoConTaraKg > 0),
    -- Si hay costo de seguro hay tasa; la tasa es una fracción entre 0 y 1.
    CONSTRAINT ck_cotizacionLineasSolicitud_seguroMontos
        CHECK ((costoSeguro IS NULL OR costoSeguro >= 0)
               AND (tasaSeguroAplicada IS NULL OR (tasaSeguroAplicada > 0 AND tasaSeguroAplicada < 1))
               AND (costoSeguro IS NULL OR tasaSeguroAplicada IS NOT NULL)),
    -- Sin seguro, ni tasa ni costo.
    CONSTRAINT ck_cotizacionLineasSolicitud_seguroIncluido
        CHECK (incluyeSeguro = 1 OR (costoSeguro IS NULL AND tasaSeguroAplicada IS NULL))
);
GO

-- Cola que lee el proceso de cubicación en segundo plano, en orden de llegada.
CREATE INDEX ix_cotizacionLineasSolicitud_cubicacionPendiente
    ON dbo_cotifly.CotizacionLineasSolicitud (cubicacionDisponibleAt, createdAt)
    WHERE estadoCubicacion = N'pendiente';
GO

-- Un camión por fila: si el cubicador recomienda 7 camiones para una ruta, son 7 filas. Cada camión es
-- una línea de la solicitud en Nexo.
CREATE TABLE dbo_cotifly.CotizacionVehiculosLineaSolicitud (
    id                      UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_id DEFAULT (NEWID()),
    lineaSolicitudId        UNIQUEIDENTIFIER NOT NULL,
    idRecomendacionVehiculo UNIQUEIDENTIFIER NULL,  -- fila de la Tabla 7 del cubicador; NULL si no salió de una recomendación
    tipoVehiculo            NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- código (3S3, 2S2...); NULL = sin selección automática
    carroceria              NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    pesoKg                  DECIMAL(12,2)    NULL,
    costoFlete              DECIMAL(18,2)    NULL,  -- lo devuelve Nexo
    costoRetornoContenedor  DECIMAL(18,2)    NULL,  -- lo devuelve Nexo
    costoLevanteContenedor  DECIMAL(18,2)    NULL,  -- copia de CotizacionLineasSolicitud.valorLevanteContenedor
    lineaSolicitudNexoId    NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    estadoCosteo            NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt               DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt               DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionVehiculosLineaSolicitud_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    volumenTotalM3          DECIMAL(12,3)    NULL,  -- del cubicador
    porcentajeLlenadoVolumen DECIMAL(5,2)    NULL,  -- 0 a 100
    porcentajeLlenadoPeso   DECIMAL(5,2)     NULL,  -- 0 a 100
    ordenVehiculo           INT              NULL,  -- orden del camión en su ruta (el primero lleva los conexos en Nexo)
    motivoRechazo           NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- motivo de la Mesa de Costeo de Nexo
    rechazadaAt             DATETIMEOFFSET   NULL,  -- cuándo vio Cotifly el rechazo
    CONSTRAINT pk_cotizacionVehiculosLineaSolicitud PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionVehiculosLineaSolicitud_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_montos CHECK (
        (pesoKg IS NULL OR pesoKg >= 0)
        AND (costoFlete IS NULL OR costoFlete >= 0)
        AND (costoRetornoContenedor IS NULL OR costoRetornoContenedor >= 0)
        AND (costoLevanteContenedor IS NULL OR costoLevanteContenedor >= 0)),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo CHECK (
        estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido', 'rechazada')),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_llenado CHECK (
        (volumenTotalM3 IS NULL OR volumenTotalM3 >= 0)
        AND (porcentajeLlenadoVolumen IS NULL OR (porcentajeLlenadoVolumen >= 0 AND porcentajeLlenadoVolumen <= 100))
        AND (porcentajeLlenadoPeso IS NULL OR (porcentajeLlenadoPeso >= 0 AND porcentajeLlenadoPeso <= 100))),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_ordenVehiculo CHECK (ordenVehiculo IS NULL OR ordenVehiculo >= 1),
    -- Motivo y fecha solo con el camión rechazado (ISNULL: un estadoCosteo NULL no lo burla).
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_rechazo CHECK (
        ISNULL(estadoCosteo, N'') = N'rechazada' OR (motivoRechazo IS NULL AND rechazadaAt IS NULL))
);
GO

CREATE INDEX ix_cotizacionVehiculosLineaSolicitud_lineaSolicitudId
    ON dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId);
-- Filtrado: admite varios camiones sin orden (NULL) en la misma ruta.
CREATE UNIQUE INDEX uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo
    ON dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, ordenVehiculo) WHERE ordenVehiculo IS NOT NULL;
GO

-- Lo que el comercial declara que va a mandar en la ruta (es lo que se le envía al cubicador). Una
-- mercancía sin las tres medidas es "no cubicable" (solo descripción, cantidad o peso).
CREATE TABLE dbo_cotifly.CotizacionMercancias (
    id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionPosiciones_id DEFAULT (NEWID()),
    lineaSolicitudId    UNIQUEIDENTIFIER NOT NULL,
    orden               INT              NOT NULL,
    nombreMercancia     NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    cantidad            INT              NOT NULL CONSTRAINT df_cotizacionPosiciones_cantidad DEFAULT (1),
    largoCm             DECIMAL(10,2)    NULL,
    anchoCm             DECIMAL(10,2)    NULL,
    altoCm              DECIMAL(10,2)    NULL,
    pesoKg              DECIMAL(12,2)    NULL,
    createdAt           DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionPosiciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    codigoMercancia     NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- código del catálogo del cubicador (ej. BEBIDAS)
    CONSTRAINT pk_cotizacionPosiciones PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionMercancias_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionMercancias_lineaSolicitudId_orden UNIQUE (lineaSolicitudId, orden),
    CONSTRAINT ck_cotizacionPosiciones_cantidad CHECK (cantidad >= 1),
    -- Las tres medidas NULL o las tres > 0 (IS NOT NULL explícito: "NULL > 0" daría UNKNOWN y pasaría).
    CONSTRAINT ck_cotizacionPosiciones_medidas
        CHECK (((largoCm IS NULL AND anchoCm IS NULL AND altoCm IS NULL)
                OR (largoCm IS NOT NULL AND anchoCm IS NOT NULL AND altoCm IS NOT NULL AND largoCm > 0 AND anchoCm > 0 AND altoCm > 0))
               AND (pesoKg IS NULL OR pesoKg >= 0))
);
GO

-- Servicios conexos de la ruta. Son globales a la ruta (no se reparten por camión): la cantidad ya dice
-- cuántos. `precio` es el costo unitario (antes del margen); `precioVenta`, lo que paga el cliente.
CREATE TABLE dbo_cotifly.CotizacionServiciosConexos (
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionServiciosConexos_id DEFAULT (NEWID()),
    lineaSolicitudId  UNIQUEIDENTIFIER NOT NULL,
    idServicioConexo  NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,  -- id del catálogo o de la fila de la cotización de Nexo
    nombreServicioConexo NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- copia del nombre
    precio            DECIMAL(18,2)    NOT NULL,  -- unitario, al momento de aplicarlo (costo, antes del margen)
    cantidad          DECIMAL(10,2)    NOT NULL,
    createdAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    idServicioConexoCatalogo NVARCHAR(64) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- id del CATÁLOGO de Nexo
    categoriaServicioConexo NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- categoría del catálogo (logistico | adicional | ...)
    precioVenta       DECIMAL(18,2)    NOT NULL,  -- precio de venta (lo que paga el cliente)
    codigoServicio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- código del catálogo de Nexo (p. ej. AVT_AUTO)
    CONSTRAINT pk_cotizacionServiciosConexos PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionServiciosConexos_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionServiciosConexos_lineaSolicitudId_idServicioConexo UNIQUE (lineaSolicitudId, idServicioConexo),
    CONSTRAINT ck_cotizacionServiciosConexos_montos
        CHECK (precio >= 0 AND cantidad > 0 AND precioVenta >= 0)
);
GO

-- Excel original del pliego de una licitación (máximo 10 MB, el mismo límite de Nexo). `origenLectura`:
-- cómo se leyó (NULL = 'analizar_excel').
CREATE TABLE dbo_cotifly.CotizacionArchivosLicitacion (
    cotizacionId    UNIQUEIDENTIFIER NOT NULL,
    nombreArchivo   NVARCHAR(255) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    tipoContenido   NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    tamanoBytes     INT              NOT NULL,
    sha256          NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    hoja            NVARCHAR(31)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    contenido       VARBINARY(MAX)   NOT NULL,
    createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionArchivosLicitacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    origenLectura   VARCHAR(20)      NULL,   -- analizar_excel | leer_estructura (collation de la base, como el ajuste 22)
    CONSTRAINT pk_cotizacionArchivosLicitacion PRIMARY KEY (cotizacionId),
    CONSTRAINT fk_cotizacionArchivosLicitacion_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT ck_cotizacionArchivosLicitacion_tamano CHECK (tamanoBytes BETWEEN 1 AND 10485760 AND DATALENGTH(contenido) = tamanoBytes),
    CONSTRAINT ck_cotizacionArchivosLicitacion_origenLectura
        CHECK (origenLectura IS NULL OR origenLectura IN ('analizar_excel', 'leer_estructura'))
);
GO

-- PDF anteriores de una cotización (el archivo no se borra; aquí queda su referencia).
CREATE TABLE dbo_cotifly.CotizacionPdfsHistorial (
    id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionPdfsHistorial_id DEFAULT (NEWID()),
    cotizacionId    UNIQUEIDENTIFIER NOT NULL,
    pdfPath         NVARCHAR(400) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    motivo          NVARCHAR(30)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    usuarioId       UNIQUEIDENTIFIER NULL,
    createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionPdfsHistorial_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionPdfsHistorial PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionPdfsHistorial_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_cotizacionPdfsHistorial_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT ck_cotizacionPdfsHistorial_motivo CHECK (motivo IN ('ajuste_margen', 'servicios_conexos', 'textos', 're_solicitud'))
);
GO

CREATE INDEX ix_cotizacionPdfsHistorial_cotizacionId ON dbo_cotifly.CotizacionPdfsHistorial (cotizacionId);
GO

-- =============================================================================
-- 5. Márgenes y correos
-- =============================================================================

-- Ajustes de margen que hizo un comercial. `lineaSolicitudId` = la ruta ajustada.
CREATE TABLE dbo_cotifly.MargenAjustes (
    id               UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_margenAjustes_id DEFAULT (NEWID()),
    cotizacionId     UNIQUEIDENTIFIER    NULL,
    folio            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    margenPolitica   DECIMAL(9,4)        NULL,
    margenAplicado   DECIMAL(9,4)        NULL,
    diferencia       DECIMAL(9,4)        NULL,
    motivo           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    usuarioId        UNIQUEIDENTIFIER    NULL,
    createdAt        DATETIMEOFFSET      NOT NULL CONSTRAINT df_margenAjustes_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    lineaSolicitudId UNIQUEIDENTIFIER    NULL,
    CONSTRAINT pk_margenAjustes PRIMARY KEY (id),
    CONSTRAINT fk_margenAjustes_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_margenAjustes_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT fk_margenAjustes_lineaSolicitudId FOREIGN KEY (lineaSolicitudId) REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id)
);
GO

CREATE INDEX ix_margenAjustes_cotizacionId ON dbo_cotifly.MargenAjustes (cotizacionId);
CREATE INDEX ix_margenAjustes_lineaSolicitudId ON dbo_cotifly.MargenAjustes (lineaSolicitudId) WHERE lineaSolicitudId IS NOT NULL;
GO

-- Un envío de la cotización por correo. Sale por MailSender, que lo entrega en segundo plano: Cotifly
-- guarda su id y consulta el estado hasta un estado final (siguienteConsultaAt NULL).
CREATE TABLE dbo_cotifly.Correos (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correos_id DEFAULT (NEWID()),
    cotizacionId    UNIQUEIDENTIFIER    NOT NULL,
    asunto          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    messageId       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    confirmadoPor   UNIQUEIDENTIFIER    NULL,
    estado          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- encolado | enviando | enviado | fallido
    error           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_correos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    idMailSender    UNIQUEIDENTIFIER    NULL,   -- id del correo en MailSender
    redirigidoA     NVARCHAR(320) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- buzón de pruebas fuera de producción
    intentosEntrega INT                 NULL,   -- intentos de entrega al relay (attemptCount de MailSender)
    enviadoAt       DATETIMEOFFSET      NULL,   -- cuándo el relay lo aceptó
    estadoActualizadoAt DATETIMEOFFSET  NULL,   -- última consulta del estado en MailSender
    siguienteConsultaAt DATETIMEOFFSET  NULL,   -- agenda del worker de correos (NULL = estado final)
    encoladoAt      DATETIMEOFFSET      NULL,   -- última aceptación de MailSender (NULL = se usa createdAt)
    CONSTRAINT pk_correos PRIMARY KEY (id),
    CONSTRAINT fk_correos_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_correos_confirmadoPor FOREIGN KEY (confirmadoPor) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT ck_correos_estado
        CHECK (estado IS NULL OR estado IN ('encolado', 'enviando', 'enviado', 'fallido'))
);
GO

CREATE INDEX ix_correos_cotizacionId_createdAt ON dbo_cotifly.Correos (cotizacionId, createdAt DESC);
CREATE INDEX ix_correos_siguienteConsultaAt ON dbo_cotifly.Correos (siguienteConsultaAt) WHERE siguienteConsultaAt IS NOT NULL;
CREATE UNIQUE INDEX ux_correos_idMailSender ON dbo_cotifly.Correos (idMailSender) WHERE idMailSender IS NOT NULL;
GO

-- Destinatarios de un correo. Que haya al menos un 'para' por correo lo valida la aplicación.
CREATE TABLE dbo_cotifly.CorreoDestinatarios (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correoDestinatarios_id DEFAULT (NEWID()),
    correoId        UNIQUEIDENTIFIER    NOT NULL,
    email           NVARCHAR(320) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    tipo            NVARCHAR(10)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    CONSTRAINT pk_correoDestinatarios PRIMARY KEY (id),
    CONSTRAINT fk_correoDestinatarios_correoId FOREIGN KEY (correoId) REFERENCES dbo_cotifly.Correos (id),
    CONSTRAINT ck_correoDestinatarios_tipo CHECK (tipo IN ('para', 'cc'))
);
GO

CREATE INDEX ix_correoDestinatarios_correoId ON dbo_cotifly.CorreoDestinatarios (correoId);
GO

-- =============================================================================
-- 6. Copia local del margen recomendado de SAP TM
-- =============================================================================
-- Sin COLLATE explícita: toman la de la base, igual que en las bases ajustadas con el 17.

-- Estado actual: una fila por (salesOrgId, trqType, bpSap), con el NIT ya resuelto para cruzar con
-- CotizacionesSolicitud. `activo` = 0 cuando SAP deja de traer la combinación (no se borra).
CREATE TABLE dbo_cotifly.SapMargenCliente
(
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_sapMargenCliente_id DEFAULT (NEWID()) CONSTRAINT pk_sapMargenCliente PRIMARY KEY,
    salesOrgId        NVARCHAR(20)     NOT NULL,
    trqType           NVARCHAR(8)      NOT NULL,
    bpSap             NVARCHAR(20)     NOT NULL,
    nit               NVARCHAR(20)     NULL,
    margenRecomendado DECIMAL(9, 4)    NOT NULL,
    activo            BIT              NOT NULL CONSTRAINT df_sapMargenCliente_activo DEFAULT (1),
    creadoAt          DATETIMEOFFSET   NOT NULL CONSTRAINT df_sapMargenCliente_creadoAt DEFAULT (SYSDATETIMEOFFSET()),
    actualizadoAt     DATETIMEOFFSET   NOT NULL CONSTRAINT df_sapMargenCliente_actualizadoAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT uq_sapMargenCliente_clave UNIQUE (salesOrgId, trqType, bpSap)
);
GO

CREATE INDEX idx_sapMargenCliente_nit ON dbo_cotifly.SapMargenCliente (nit) WHERE nit IS NOT NULL;
GO

-- Log de corridas de la sincronización (append-only): una fila por ejecución, no por registro de SAP.
CREATE TABLE dbo_cotifly.SapMargenSyncLog
(
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_sapMargenSyncLog_id DEFAULT (NEWID()) CONSTRAINT pk_sapMargenSyncLog PRIMARY KEY,
    iniciadoAt        DATETIMEOFFSET  NOT NULL CONSTRAINT df_sapMargenSyncLog_iniciadoAt DEFAULT (SYSDATETIMEOFFSET()),
    finalizadoAt      DATETIMEOFFSET  NULL,
    filasInsertadas   INT             NULL,
    filasActualizadas INT             NULL,
    filasDesactivadas INT             NULL,
    status            NVARCHAR(20)    NOT NULL,
    mensajeError      NVARCHAR(2000)  NULL,
    CONSTRAINT ck_sapMargenSyncLog_status CHECK (status IN (N'ok', N'error', N'omitido'))
);
GO

CREATE INDEX idx_sapMargenSyncLog_iniciado ON dbo_cotifly.SapMargenSyncLog (iniciadoAt DESC);

-- sp_sincronizar_margen_sap: upsert desde #SapMargenStage (tabla temporal que crea y llena el llamador en
-- su misma conexión) y registro en el log, en una transacción con sp_getapplock para que dos corridas no
-- se solapen. El texto del procedimiento es idéntico al del ajuste 17 (su definición queda igual en las
-- dos clases de base).
GO

CREATE OR ALTER PROCEDURE dbo_cotifly.sp_sincronizar_margen_sap
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @iniciadoAt DATETIMEOFFSET = SYSDATETIMEOFFSET();
    DECLARE @lockResultado INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Evita que dos corridas se solapen (igual que EmbarQA con fo_snapshot).
        EXEC @lockResultado = sp_getapplock
             @Resource = N'sp_sincronizar_margen_sap',
             @LockMode = N'Exclusive',
             @LockOwner = N'Transaction',
             @LockTimeout = 0;

        IF @lockResultado < 0
        BEGIN
            ROLLBACK;
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
            VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'omitido', N'Ya hay otra corrida en curso (sp_getapplock no se pudo tomar).');
            RETURN;
        END

        DECLARE @filasInsertadas INT, @filasActualizadas INT, @filasDesactivadas INT;
        DECLARE @acciones TABLE (accion NVARCHAR(10) NOT NULL, activoResultante BIT NOT NULL);

        -- Una sola fila MATCHED por combinación: "cambió el valor" y "estaba desactivada y reaparece en SAP"
        -- quedan en la misma rama (las dos son, en la práctica, poner la fila al día); NOT MATCHED BY SOURCE
        -- es la única rama que apaga una fila.
        -- #SapMargenStage la crea y llena el llamador (PowerShell) en esta misma conexión antes de este EXEC;
        -- no es una tabla del esquema, por eso no aparece en el CREATE OR ALTER de más arriba.
        MERGE dbo_cotifly.SapMargenCliente AS destino
        USING #SapMargenStage AS origen
            ON destino.salesOrgId = origen.salesOrgId
           AND destino.trqType = origen.trqType
           AND destino.bpSap = origen.bpSap
        WHEN MATCHED AND (destino.margenRecomendado <> origen.margenRecomendado
                           OR destino.activo = 0
                           OR ISNULL(destino.nit, N'') <> ISNULL(origen.nit, N'')) THEN
            UPDATE SET margenRecomendado = origen.margenRecomendado,
                       nit = origen.nit,
                       activo = 1,
                       actualizadoAt = SYSDATETIMEOFFSET()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (salesOrgId, trqType, bpSap, nit, margenRecomendado)
            VALUES (origen.salesOrgId, origen.trqType, origen.bpSap, origen.nit, origen.margenRecomendado)
        WHEN NOT MATCHED BY SOURCE AND destino.activo = 1 THEN
            UPDATE SET activo = 0, actualizadoAt = SYSDATETIMEOFFSET()
        OUTPUT $action, inserted.activo INTO @acciones (accion, activoResultante);

        SELECT @filasInsertadas = SUM(CASE WHEN accion = N'INSERT' THEN 1 ELSE 0 END),
               @filasActualizadas = SUM(CASE WHEN accion = N'UPDATE' AND activoResultante = 1 THEN 1 ELSE 0 END),
               @filasDesactivadas = SUM(CASE WHEN accion = N'UPDATE' AND activoResultante = 0 THEN 1 ELSE 0 END)
        FROM @acciones;

        INSERT INTO dbo_cotifly.SapMargenSyncLog
            (iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, status)
        VALUES
            (@iniciadoAt, SYSDATETIMEOFFSET(), ISNULL(@filasInsertadas, 0), ISNULL(@filasActualizadas, 0), ISNULL(@filasDesactivadas, 0), N'ok');

        COMMIT;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;
        INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
        VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'error', LEFT(ERROR_MESSAGE(), 2000));
        THROW;
    END CATCH
END
GO

-- =============================================================================
-- 7. Fin
-- =============================================================================
COMMIT TRANSACTION;
PRINT N'Esquema dbo_cotifly creado (28 tablas y 1 procedimiento). Falta sembrar la configuración inicial (db/seeder/).';
GO

SET NOEXEC OFF;
GO

/* ============================================================================
   Verificación rápida sugerida tras ejecutar:

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;
     -- 28 tablas: CapturasIdempotentes, CorreoDestinatarios, Correos, CotizacionArchivosLicitacion,
     --   CotizacionLineasSolicitud, CotizacionMercancias, CotizacionPdfsHistorial,
     --   CotizacionServiciosConexos, CotizacionVehiculosLineaSolicitud, CotizacionesSolicitud,
     --   EventosConversacion, EventosHerramienta, MargenAjustes, MargenesPolitica,
     --   PoliticaSeguroMedidaConexos, PoliticaSeguroMedidas, PoliticaSeguroTramoMedidas,
     --   PoliticaSeguroTramos, Roles, SapMapeoTrqType, SapMargenCliente, SapMargenSyncLog, Sesiones,
     --   TarifasSeguro, TokensRefresco, TokensRevocados, UsuariosRoles, UsuariosWeb.

     SELECT name FROM sys.procedures WHERE SCHEMA_NAME(schema_id) = 'dbo_cotifly';
     -- sp_sincronizar_margen_sap

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = 'dbo_cotifly';
     -- ninguna.
   ============================================================================ */
