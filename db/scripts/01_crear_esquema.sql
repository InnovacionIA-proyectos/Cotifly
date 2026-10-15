/* ============================================================================
   Cotifly — Instalación desde cero del esquema dbo_cotifly (SQL Server 2019)
   ============================================================================
   Crea el schema `dbo_cotifly` con TODAS sus tablas, llaves, restricciones e índices,
   ya en su forma final (la del diseño HU-021, docs/database/PROPUESTA_
   REESTRUCTURACION_COTIZACIONES.md). Es lo que dejan los scripts de `db/migrations_mssql/` 0001 a 0010
   aplicados en orden (menos las dos vistas del borrador de métricas, que retira
   db/scripts/08 desde HU-014.3, #152), pero en un solo paso (0010, borrador pendiente de revisión de
   @scalderon-bs: nombres de municipio de la ruta, id de la solicitud de Nexo y nombre del
   conexo; decisión 2 de HU-021). Una base instalada con este script NO corre migraciones.

   NO inserta datos: la configuración inicial (líneas de negocio, mapeo SAP, roles) la
   siembran los archivos de `db/seeder/`. El ejecutador (`ejecutar-bd.ps1` /
   `ejecutar-bd.sh`) corre primero este script y después los seeders.

   Uso: SOLO en una base donde `dbo_cotifly` todavía no tiene tablas. Si ya las tiene, el
   script no ejecuta nada (no suelta, no recrea y no crea tablas sueltas; vale en sqlcmd y en
   SSMS). Para una base que ya tiene el esquema viejo se usan las migraciones
   (db/migrations_mssql/), no este.

   NO crea la base de datos: se conecta a la que le indique `-d` (sqlcmd) y crea el schema
   adentro. El ejecutador puede crearla con `-CrearBase`.

   Collation: `Latin1_General_100_CI_AS_SC_UTF8` explícita en cada columna de texto (la base
   puede tener otra y no se le toca). Única excepción: TokensRefresco.tokenHash, que usa
   `Latin1_General_100_BIN2` (hexadecimal ASCII, comparación exacta).

   Nombres de restricciones: son los mismos que deja una base migrada con el 0009. Por eso
   las tablas renombradas (CotizacionesSolicitud, CotizacionMercancias) conservan en sus
   restricciones los nombres `cotizaciones` y `cotizacionPosiciones`.

   Cómo ejecutarlo a mano:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -i 01_crear_esquema.sql
   (-E = autenticación de Windows, -C = confiar en el certificado, -I = QUOTED_IDENTIFIER
   ON, -b = detenerse en el primer error). Requiere permiso para CREATE SCHEMA/TABLE.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondición y schema
-- =============================================================================
-- Si ya hay tablas: avisa y apaga la ejecución (SET NOEXEC ON) para que NINGÚN bloque siguiente cree nada,
-- ni siquiera en SSMS, que de otro modo seguiría después del error. Al final se vuelve a encender.
IF EXISTS (SELECT 1 FROM sys.schemas AS s JOIN sys.tables AS t ON t.schema_id = s.schema_id WHERE s.name = N'dbo_cotifly')
BEGIN
    RAISERROR (N'dbo_cotifly ya tiene tablas: este script es solo para una instalación desde cero y no se ejecutó nada. Para una base existente use las migraciones (db/migrations_mssql/).', 16, 1);
    SET NOEXEC ON;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dbo_cotifly')
    EXEC (N'CREATE SCHEMA dbo_cotifly');
GO

-- =============================================================================
-- 1. Catálogos de configuración (sin dependencias)
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

-- Mapeo del TrqType de SAP TM a la línea de negocio.
CREATE TABLE dbo_cotifly.SapMapeoTrqType (
    trqType         NVARCHAR(8)   COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    salesOrg        NVARCHAR(12)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL CONSTRAINT df_sapMapeoTrqType_salesOrg DEFAULT (''),
    lineaNegocio    NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    activo          BIT                 NOT NULL CONSTRAINT df_sapMapeoTrqType_activo DEFAULT (1),
    CONSTRAINT pk_sapMapeoTrqType PRIMARY KEY (trqType, salesOrg)
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
    CONSTRAINT ck_roles_codigo CHECK (codigo IN ('comercial', 'gerente_comercial'))
);
GO

CREATE UNIQUE INDEX uq_roles_idRolExterno ON dbo_cotifly.Roles (idRolExterno) WHERE idRolExterno IS NOT NULL;
GO

-- Rol interno asignado a cada usuario (a lo sumo una asignación activa por usuario).
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

CREATE UNIQUE INDEX uq_usuariosRoles_usuarioId_activo ON dbo_cotifly.UsuariosRoles (usuarioId) WHERE activo = 1;
GO

-- Refresh tokens opacos: solo se guarda el SHA-256 en hexadecimal (64 caracteres).
CREATE TABLE dbo_cotifly.TokensRefresco (
    id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_tokensRefresco_id DEFAULT (NEWID()),
    usuarioId           UNIQUEIDENTIFIER    NOT NULL,
    tokenHash           CHAR(64)      COLLATE Latin1_General_100_BIN2 NOT NULL,
    familiaId           UNIQUEIDENTIFIER    NOT NULL,
    createdAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_tokensRefresco_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    expiraAt            DATETIMEOFFSET      NOT NULL,
    revocadoAt          DATETIMEOFFSET      NULL,
    reemplazadoPorId    UNIQUEIDENTIFIER    NULL,
    CONSTRAINT pk_tokensRefresco PRIMARY KEY (id),
    CONSTRAINT uq_tokensRefresco_tokenHash UNIQUE (tokenHash),
    CONSTRAINT fk_tokensRefresco_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT fk_tokensRefresco_reemplazadoPorId FOREIGN KEY (reemplazadoPorId) REFERENCES dbo_cotifly.TokensRefresco (id),
    CONSTRAINT ck_tokensRefresco_vigencia CHECK (expiraAt > createdAt)
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

-- La solicitud: cliente, estado general, PDF, seguimiento del costeo con Nexo y precio total.
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
    solicitudNexoId             NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- id de la SOLICITUD de Nexo (0010; ex costeoId)
    motivoEstadoCosteo          NVARCHAR(1000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    costeoEnviadoAt             DATETIMEOFFSET   NULL,
    costeoSiguienteConsultaAt   DATETIMEOFFSET   NULL,
    costeoConsultasFallidas     INT              NOT NULL CONSTRAINT df_cotizaciones_costeoConsultasFallidas DEFAULT (0),
    textosCotizacion            NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- JSON: asunto e introducción del PDF
    notas                      NVARCHAR(2000) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- nota para la Mesa de Nexo
    CONSTRAINT pk_cotizaciones PRIMARY KEY (id),
    CONSTRAINT uq_cotizaciones_folio UNIQUE (folio),
    CONSTRAINT fk_cotizaciones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id),
    CONSTRAINT ck_cotizaciones_origenCliente CHECK (origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto')),
    CONSTRAINT ck_cotizaciones_tipoSolicitud CHECK (tipoSolicitud IS NULL OR tipoSolicitud IN ('spot', 'licitacion')),
    CONSTRAINT ck_cotizaciones_estadoEnvio CHECK (estadoEnvio IS NULL OR estadoEnvio IN ('sin_enviar', 'enviada', 'reenviada')),
    CONSTRAINT ck_cotizaciones_versionNegocio CHECK (versionNegocio >= 0),
    CONSTRAINT ck_cotizaciones_textosCotizacion_json CHECK (textosCotizacion IS NULL OR ISJSON(textosCotizacion) = 1),
    CONSTRAINT ck_cotizaciones_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'))
);
GO

CREATE INDEX ix_cotizaciones_usuarioId_createdAt ON dbo_cotifly.CotizacionesSolicitud (usuarioId, createdAt DESC);
-- Agenda del polling: solo las solicitudes que todavía se consultan en Nexo.
CREATE INDEX ix_cotizaciones_costeoSiguienteConsultaAt ON dbo_cotifly.CotizacionesSolicitud (costeoSiguienteConsultaAt)
    WHERE costeoSiguienteConsultaAt IS NOT NULL;
GO

-- La ruta: una fila por ruta que pidió el comercial. Tiene lo que es igual para todos sus camiones,
-- los totales de costo, el margen y el precio.
CREATE TABLE dbo_cotifly.CotizacionLineasSolicitud (
    id                          UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_id DEFAULT (NEWID()),
    cotizacionSolicitudId       UNIQUEIDENTIFIER NOT NULL,
    orden                       INT              NOT NULL,
    rutaOrigenIndice            INT              NULL,   -- fila del Excel de la que salió (base 0); 0 en una cotización simple
    lineaNegocioId              INT              NULL,
    origenMunicipioId           INT              NULL,   -- id de tbl_municipios de Nexo (sin FK: es de otra base)
    origenNombre                NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre (0010)
    destinoMunicipioId          INT              NULL,
    destinoNombre               NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre (0010)
    tipologia                   NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    claseViaje                  NVARCHAR(50)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    claseContenedor             NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    exclusivo                   BIT              NULL,
    consolidado                 BIT              NULL,
    retornaContenedor           BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_retornaContenedor DEFAULT (0),
    levanteContenedorDiferente  BIT              NOT NULL CONSTRAINT df_cotizacionLineasSolicitud_levanteContenedorDiferente DEFAULT (0),
    ciudadDevolucionMunicipioId INT              NULL,
    ciudadDevolucionNombre      NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre (0010)
    ciudadLevanteMunicipioId    INT              NULL,
    ciudadLevanteNombre         NVARCHAR(150) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- copia del nombre (0010)
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
    CONSTRAINT pk_cotizacionLineasSolicitud PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionLineasSolicitud_cotizacionId FOREIGN KEY (cotizacionSolicitudId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_cotizacionLineasSolicitud_lineaNegocioId FOREIGN KEY (lineaNegocioId) REFERENCES dbo_cotifly.MargenesPolitica (id),
    CONSTRAINT uq_cotizacionLineasSolicitud_cotizacionId_orden UNIQUE (cotizacionSolicitudId, orden),
    CONSTRAINT ck_cotizacionLineasSolicitud_orden CHECK (orden >= 1),
    CONSTRAINT ck_cotizacionLineasSolicitud_filaOrigen CHECK (rutaOrigenIndice IS NULL OR rutaOrigenIndice >= 0),
    CONSTRAINT ck_cotizacionLineasSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido')),
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
        origenMargen IS NULL OR origenMargen IN ('sap_tm', 'politica_linea', 'negociado'))
);
GO

-- Un camión por fila: si el cubicador recomienda 7 camiones para una ruta, son 7 filas.
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
    CONSTRAINT pk_cotizacionVehiculosLineaSolicitud PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionVehiculosLineaSolicitud_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_montos CHECK (
        (pesoKg IS NULL OR pesoKg >= 0)
        AND (costoFlete IS NULL OR costoFlete >= 0)
        AND (costoRetornoContenedor IS NULL OR costoRetornoContenedor >= 0)
        AND (costoLevanteContenedor IS NULL OR costoLevanteContenedor >= 0)),
    CONSTRAINT ck_cotizacionVehiculosLineaSolicitud_estadoCosteo CHECK (estadoCosteo IS NULL OR estadoCosteo IN
        ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido'))
);
GO

CREATE INDEX ix_cotizacionVehiculosLineaSolicitud_lineaSolicitudId
    ON dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId);
GO

-- Lo que el comercial declara que va a mandar en la ruta (es lo que se le envía al cubicador).
CREATE TABLE dbo_cotifly.CotizacionMercancias (
    id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionPosiciones_id DEFAULT (NEWID()),
    lineaSolicitudId    UNIQUEIDENTIFIER NOT NULL,
    orden               INT              NOT NULL,
    idTipoMercancia     UNIQUEIDENTIFIER NULL,  -- tiposMercancia del cubicador; el backend lo exige en las filas nuevas
    nombreMercancia     NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    cantidad            INT              NOT NULL CONSTRAINT df_cotizacionPosiciones_cantidad DEFAULT (1),
    largoCm             DECIMAL(10,2)    NOT NULL,
    anchoCm             DECIMAL(10,2)    NOT NULL,
    altoCm              DECIMAL(10,2)    NOT NULL,
    pesoKg              DECIMAL(12,2)    NULL,
    createdAt           DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionPosiciones_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionPosiciones PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionMercancias_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionMercancias_lineaSolicitudId_orden UNIQUE (lineaSolicitudId, orden),
    CONSTRAINT ck_cotizacionPosiciones_cantidad CHECK (cantidad >= 1),
    CONSTRAINT ck_cotizacionPosiciones_medidas CHECK (largoCm > 0 AND anchoCm > 0 AND altoCm > 0 AND (pesoKg IS NULL OR pesoKg >= 0))
);
GO

-- Servicios conexos de la ruta. Son globales a la ruta (no se reparten por camión): la cantidad ya dice cuántos.
CREATE TABLE dbo_cotifly.CotizacionServiciosConexos (
    id                UNIQUEIDENTIFIER NOT NULL CONSTRAINT df_cotizacionServiciosConexos_id DEFAULT (NEWID()),
    lineaSolicitudId  UNIQUEIDENTIFIER NOT NULL,
    idServicioConexo  NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,  -- id del catálogo de Nexo
    nombreServicioConexo NVARCHAR(200) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,  -- copia del nombre (0010)
    precio            DECIMAL(18,2)    NOT NULL,  -- unitario, al momento de aplicarlo (costo, antes del margen)
    cantidad          DECIMAL(10,2)    NOT NULL,
    createdAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    updatedAt         DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionServiciosConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionServiciosConexos PRIMARY KEY (id),
    CONSTRAINT fk_cotizacionServiciosConexos_lineaSolicitudId FOREIGN KEY (lineaSolicitudId)
        REFERENCES dbo_cotifly.CotizacionLineasSolicitud (id),
    CONSTRAINT uq_cotizacionServiciosConexos_lineaSolicitudId_idServicioConexo UNIQUE (lineaSolicitudId, idServicioConexo),
    CONSTRAINT ck_cotizacionServiciosConexos_montos CHECK (precio >= 0 AND cantidad > 0)
);
GO

-- Excel original del pliego de una licitación (máximo 10 MB, el mismo límite de Nexo).
CREATE TABLE dbo_cotifly.CotizacionArchivosLicitacion (
    cotizacionId    UNIQUEIDENTIFIER NOT NULL,
    nombreArchivo   NVARCHAR(255) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    tipoContenido   NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    tamanoBytes     INT              NOT NULL,
    sha256          NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    hoja            NVARCHAR(31)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
    contenido       VARBINARY(MAX)   NOT NULL,
    createdAt       DATETIMEOFFSET   NOT NULL CONSTRAINT df_cotizacionArchivosLicitacion_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_cotizacionArchivosLicitacion PRIMARY KEY (cotizacionId),
    CONSTRAINT fk_cotizacionArchivosLicitacion_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT ck_cotizacionArchivosLicitacion_tamano CHECK (tamanoBytes BETWEEN 1 AND 10485760 AND DATALENGTH(contenido) = tamanoBytes)
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

CREATE TABLE dbo_cotifly.Correos (
    id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_correos_id DEFAULT (NEWID()),
    cotizacionId    UNIQUEIDENTIFIER    NOT NULL,
    asunto          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    messageId       NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    confirmadoPor   UNIQUEIDENTIFIER    NULL,
    estado          NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,   -- enviado | fallido
    error           NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
    createdAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_correos_createdAt DEFAULT (SYSDATETIMEOFFSET()),
    CONSTRAINT pk_correos PRIMARY KEY (id),
    CONSTRAINT fk_correos_cotizacionId FOREIGN KEY (cotizacionId) REFERENCES dbo_cotifly.CotizacionesSolicitud (id),
    CONSTRAINT fk_correos_confirmadoPor FOREIGN KEY (confirmadoPor) REFERENCES dbo_cotifly.UsuariosWeb (id)
);
GO

CREATE INDEX ix_correos_cotizacionId_createdAt ON dbo_cotifly.Correos (cotizacionId, createdAt DESC);
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

PRINT N'Esquema dbo_cotifly creado. Falta sembrar la configuración inicial (db/seeder/).';
GO

SET NOEXEC OFF;
GO

/* ============================================================================
   Verificación rápida sugerida tras ejecutar:

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;
     -- 20 tablas: CorreoDestinatarios, Correos, CotizacionArchivosLicitacion,
     --   CotizacionLineasSolicitud, CotizacionMercancias, CotizacionPdfsHistorial,
     --   CotizacionServiciosConexos, CotizacionVehiculosLineaSolicitud, CotizacionesSolicitud,
     --   EventosConversacion, EventosHerramienta, MargenAjustes, MargenesPolitica, Roles,
     --   SapMapeoTrqType, Sesiones, TokensRefresco, TokensRevocados, UsuariosRoles, UsuariosWeb.

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_SCHEMA = 'dbo_cotifly';
     -- ninguna: las vistas del borrador de métricas se retiraron (HU-014.3, db/scripts/08).
   ============================================================================ */
