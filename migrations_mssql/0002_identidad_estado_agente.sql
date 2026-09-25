/* ============================================================================
   Cotifly — 0002: identidad (login SSO ClientePro) y estado del agente
   ============================================================================
   HU-003.3 (#9). Diseño: backend-dotnet/specs/decisiones/ADR-004 (estado de
   conversación / slot-filling) y ADR-009 (login SSO — Cliente Pro); patrón de
   scripts en ADR-008 (EF Core Database First: el esquema SOLO cambia con
   scripts `migrations_mssql/000N_*.sql`; no hay migraciones EF).

   *** EXTENSIÓN ADITIVA PENDIENTE DE APROBACIÓN — no ejecutar en tbsdatacenter
       sin el visto bueno del equipo de datos ***
   HU-003.3 dice "no rediseñar el esquema aquí". Este script NO toca nada de lo
   que creó el 0001: solo AGREGA columnas nullable, tablas nuevas e índices que
   el diseño .NET necesita. El DDL queda sujeto a la revisión y aprobación de
   Sara y scalderon-bs (equipo de datos) antes de aplicarlo en el entorno real.
   Redacta: Duban (backend).

   Qué agrega (todo en dbo_cotifly, dentro de dbTarifario_Nexo_Pruebas):
     1. Sesiones.estadoAgente  NVARCHAR(MAX) NULL + CHECK ISJSON  (ADR-004)
        Sesiones.usuarioId     UNIQUEIDENTIFIER NULL + FK UsuariosWeb (ADR-004)
     1bis. UsuariosWeb.idUsuarioExterno BIGINT NULL, único filtrado: vínculo con
        tblSAPUsuariosApp.ID para la verificación doble del login (ADR-009)
     2. Roles            roles internos (comercial | gerente_comercial) y su rol
                         externo de ClientePro (tblSAPRoles.IDROLES) (ADR-009)
        UsuariosRoles    rol interno asignado a cada usuario en su primer login
                         (verificación doble) (ADR-009)
        Ambas reemplazan a la RolesMapeo del borrador anterior (nunca aplicado).
     3. TokensRefresco   hash SHA-256 del refresh token, rotación (ADR-009)
     4. TokensRevocados  jti revocados hasta su vencimiento (ADR-009)
     5. Índices para las consultas frecuentes del backend .NET (listado del
        comercial, dedupe de correo, historial de conversación, barrido de
        sesiones, purga de tokens).

   Idempotente y NO destructivo: cada ADD/CREATE/INSERT va guardado con
   comprobaciones de existencia; se puede correr varias veces (la semilla de Roles
   no pisa los idRolExterno que haya fijado el DBA). A diferencia del 0001 NO suelta
   ni recrea nada (el 0001 advierte que ese patrón no sirve una vez haya datos).

   *** PASO DE UN SOLO SENTIDO: después de este script NO se re-ejecuta el 0001 ***
   TokensRefresco, TokensRevocados, UsuariosRoles y Sesiones.usuarioId tienen FK a UsuariosWeb.
   El paso 0bis del 0001 suelta tablas en orden y NO falla hasta llegar a
   `DROP TABLE UsuariosWeb` (bloqueado por esas FK). Re-ejecutarlo DESTRUYE antes:
     - las vistas vAjustesMargenPorComercial y vActividadPorComercial, y
     - 6 tablas con sus datos: CorreoDestinatarios, MargenAjustes, Correos,
       EventosConversacion, Cotizaciones y Sesiones.
   Con `sqlcmd -b` se detiene en ese DROP y deja el esquema a medias. Sin `-b`
   (o en SSMS) sigue: suelta también MargenesPolitica, SapMapeoTrqType y
   EventosHerramienta, y recrea todo menos UsuariosWeb en la forma del 0001, es
   decir, Sesiones SIN estadoAgente/usuarioId y sin los índices de este script.
   Guarda propuesta para el 0001 (PR #40, no se modifica aquí), al inicio de su
   paso 0bis:
     IF OBJECT_ID(N'dbo_cotifly.TokensRefresco', N'U') IS NOT NULL
         THROW 50002, '0002 ya aplicado: el 0001 no debe re-ejecutarse', 1;
   Desde aquí el esquema evoluciona solo con scripts 000N que alteran sin destruir.

   Collation: igual que el 0001, `Latin1_General_100_CI_AS_SC_UTF8` explícita en
   cada columna de texto. Única excepción: TokensRefresco.tokenHash usa
   `Latin1_General_100_BIN2` (hexadecimal ASCII, comparación exacta byte a byte).

   Precondición: el 0001 ya aplicado (existe el schema dbo_cotifly con sus 10
   tablas). Si no, el script se detiene con un error explícito.

   Cómo ejecutarlo (igual que el 0001):
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -i 0002_identidad_estado_agente.sql
   Las pruebas de integración del backend .NET (Testcontainers, SQL Server 2019)
   aplican 0001 y luego 0002, partidos por `GO`, y comparan el modelo EF con el
   esquema resultante.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

IF OBJECT_ID(N'dbo_cotifly.Sesiones', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NULL
    THROW 50001, 'dbo_cotifly no existe o está incompleto: aplique primero 0001_script_bd_cotifly_sqlserver.sql.', 1;
GO

-- =============================================================================
-- 1. dbo_cotifly.Sesiones — estado del agente (JSON) y dueño de la sesión
--    (ADR-004). Columnas nullable: las sesiones existentes quedan válidas.
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.Sesiones', N'estadoAgente') IS NULL
    ALTER TABLE dbo_cotifly.Sesiones
        ADD estadoAgente NVARCHAR(MAX) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_sesiones_estadoAgente_json'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Sesiones'))
    ALTER TABLE dbo_cotifly.Sesiones
        ADD CONSTRAINT ck_sesiones_estadoAgente_json CHECK (estadoAgente IS NULL OR ISJSON(estadoAgente) = 1);
GO

IF COL_LENGTH(N'dbo_cotifly.Sesiones', N'usuarioId') IS NULL
    ALTER TABLE dbo_cotifly.Sesiones
        ADD usuarioId UNIQUEIDENTIFIER NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'fk_sesiones_usuarioId'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Sesiones'))
    ALTER TABLE dbo_cotifly.Sesiones
        ADD CONSTRAINT fk_sesiones_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id);
GO

-- =============================================================================
-- 1bis. dbo_cotifly.UsuariosWeb.idUsuarioExterno — vínculo con el almacén corporativo
--    (ADR-009, verificación doble). Guarda tblSAPUsuariosApp.ID del usuario al que se
--    le creó la cuenta. NO es la clave de búsqueda (esa sigue siendo nombreUsuario,
--    HU-003.6): sirve para detectar que el NOMBREUSUARIO cambió en el almacén (mismo ID
--    con otro nombre -> 401 y revisión, en vez de crear una cuenta duplicada) o que un
--    nombre pasó a otra persona. BIGINT: tblSAPUsuariosApp.ID es NUMERIC sin decimales.
--    Nullable: filas creadas antes quedan sin vínculo y se vinculan en su próximo login.
-- =============================================================================
IF COL_LENGTH(N'dbo_cotifly.UsuariosWeb', N'idUsuarioExterno') IS NULL
    ALTER TABLE dbo_cotifly.UsuariosWeb
        ADD idUsuarioExterno BIGINT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'uq_usuariosWeb_idUsuarioExterno'
               AND object_id = OBJECT_ID(N'dbo_cotifly.UsuariosWeb'))
    CREATE UNIQUE INDEX uq_usuariosWeb_idUsuarioExterno ON dbo_cotifly.UsuariosWeb (idUsuarioExterno)
        WHERE idUsuarioExterno IS NOT NULL;
GO

-- =============================================================================
-- 2. dbo_cotifly.Roles — roles internos de Cotifly y su rol externo (ADR-009)
--    Reemplaza a la tabla RolesMapeo del borrador anterior de este script (nunca se
--    aplicó). Un rol interno por fila (`codigo`), mapeado a lo sumo a un rol de
--    ClientePro (`idRolExterno` = dbsolucionesti.dbo.tblSAPRoles.IDROLES; la
--    DESCRIPCION externa es solo informativa y no se usa para mapear).
--    Semilla: los dos roles internos SIN rol externo: los IDROLES de "Comercial" y
--    "Gerente Comercial" todavía no se conocen. Mientras idRolExterno sea NULL nadie
--    puede iniciar sesión con ese rol (fail-closed). El DBA los fija con:
--      UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'comercial';
--      UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'gerente_comercial';
--    Re-ejecutar este script no pisa esos valores (la semilla solo inserta si falta).
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.Roles', N'U') IS NULL
BEGIN
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
END
GO

-- Un rol externo mapea a un solo rol interno (varios NULL conviven: índice filtrado).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'uq_roles_idRolExterno'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Roles'))
    CREATE UNIQUE INDEX uq_roles_idRolExterno ON dbo_cotifly.Roles (idRolExterno)
        WHERE idRolExterno IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = N'comercial')
    INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'comercial', N'Comercial');
GO

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = N'gerente_comercial')
    INSERT INTO dbo_cotifly.Roles (codigo, descripcion) VALUES (N'gerente_comercial', N'Gerente comercial');
GO

-- =============================================================================
-- 2bis. dbo_cotifly.UsuariosRoles — rol interno asignado a cada usuario (ADR-009)
--    Se llena en el primer login (JIT) con el rol que resulta del rol externo. En los
--    logins siguientes el rol externo actual debe coincidir con la asignación activa;
--    si no, 401 hasta que un gerente/admin lo revise (`revisadoPor`, `revisadoAt`):
--    la asignación NO se re-sincroniza sola.
--    Un usuario tiene a lo sumo UNA asignación activa (índice único filtrado) y no
--    repite rol (UNIQUE usuarioId, rolId; `activo` = 0 conserva el historial).
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.UsuariosRoles', N'U') IS NULL
BEGIN
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
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'uq_usuariosRoles_usuarioId_activo'
               AND object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles'))
    CREATE UNIQUE INDEX uq_usuariosRoles_usuarioId_activo ON dbo_cotifly.UsuariosRoles (usuarioId)
        WHERE activo = 1;
GO

-- =============================================================================
-- 3. dbo_cotifly.TokensRefresco — refresh tokens opacos (ADR-009)
--    Solo se guarda el SHA-256 en hexadecimal minúscula (64 caracteres), nunca
--    el token. `familiaId` agrupa la cadena de rotación: reutilizar un token ya
--    rotado revoca toda la familia. `reemplazadoPorId` encadena la rotación; la
--    rotación atómica es un único UPDATE condicional
--    (... WHERE id = @id AND revocadoAt IS NULL AND reemplazadoPorId IS NULL
--    AND expiraAt > @ahora): de dos rotaciones simultáneas solo una afecta la fila.
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.TokensRefresco', N'U') IS NULL
BEGIN
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
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tokensRefresco_usuarioId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.TokensRefresco'))
    CREATE INDEX ix_tokensRefresco_usuarioId ON dbo_cotifly.TokensRefresco (usuarioId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tokensRefresco_familiaId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.TokensRefresco'))
    CREATE INDEX ix_tokensRefresco_familiaId ON dbo_cotifly.TokensRefresco (familiaId);
GO

-- =============================================================================
-- 4. dbo_cotifly.TokensRevocados — jti de access tokens revocados en logout
--    (ADR-009). Se conservan hasta `expiraAt` (vencimiento del JWT, 60 min) y
--    luego los purga un proceso en segundo plano.
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.TokensRevocados', N'U') IS NULL
BEGIN
    CREATE TABLE dbo_cotifly.TokensRevocados (
        jti                 NVARCHAR(64)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
        usuarioId           UNIQUEIDENTIFIER    NULL,
        expiraAt            DATETIMEOFFSET      NOT NULL,
        createdAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_tokensRevocados_createdAt DEFAULT (SYSDATETIMEOFFSET()),
        CONSTRAINT pk_tokensRevocados PRIMARY KEY (jti),
        CONSTRAINT fk_tokensRevocados_usuarioId FOREIGN KEY (usuarioId) REFERENCES dbo_cotifly.UsuariosWeb (id)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_tokensRevocados_expiraAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.TokensRevocados'))
    CREATE INDEX ix_tokensRevocados_expiraAt ON dbo_cotifly.TokensRevocados (expiraAt);
GO

-- =============================================================================
-- 5. Índices de consulta sobre tablas del 0001 (no cambian columnas ni datos)
-- =============================================================================
-- "Mis cotizaciones": WHERE usuarioId = @u ORDER BY createdAt DESC (máx. 100).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_cotizaciones_usuarioId_createdAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones'))
    CREATE INDEX ix_cotizaciones_usuarioId_createdAt ON dbo_cotifly.Cotizaciones (usuarioId, createdAt DESC);
GO

-- Dedupe de correo (5 min) e historial de envíos por cotización.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_correos_cotizacionId_createdAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Correos'))
    CREATE INDEX ix_correos_cotizacionId_createdAt ON dbo_cotifly.Correos (cotizacionId, createdAt DESC);
GO

-- Destinatarios de un correo (FK sin índice en el 0001).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_correoDestinatarios_correoId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.CorreoDestinatarios'))
    CREATE INDEX ix_correoDestinatarios_correoId ON dbo_cotifly.CorreoDestinatarios (correoId);
GO

-- Ajustes de margen por cotización (FK sin índice en el 0001; lo usa la vista de métricas).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_margenAjustes_cotizacionId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.MargenAjustes'))
    CREATE INDEX ix_margenAjustes_cotizacionId ON dbo_cotifly.MargenAjustes (cotizacionId);
GO

-- Historial de conversación en orden cronológico.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_eventosConversacion_sesion_createdAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.EventosConversacion'))
    CREATE INDEX ix_eventosConversacion_sesion_createdAt ON dbo_cotifly.EventosConversacion (spaceId, threadId, createdAt);
GO

-- Barrido de sesiones inactivas (estado = 'activa' AND ultimaActividad < @umbral).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_sesiones_ultimaActividad'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Sesiones'))
    CREATE INDEX ix_sesiones_ultimaActividad ON dbo_cotifly.Sesiones (ultimaActividad) INCLUDE (estado);
GO

-- Sesiones de un usuario (aislamiento por dueño en /web/chat).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_sesiones_usuarioId'
               AND object_id = OBJECT_ID(N'dbo_cotifly.Sesiones'))
    CREATE INDEX ix_sesiones_usuarioId ON dbo_cotifly.Sesiones (usuarioId);
GO

/* ============================================================================
   Fin del script. Verificación rápida sugerida tras ejecutar:

     SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_NAME = 'Sesiones'
       AND COLUMN_NAME IN ('estadoAgente', 'usuarioId');
     -- Debe devolver 2 filas (nvarchar, uniqueidentifier).

     SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
     WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'
     ORDER BY TABLE_NAME;
     -- Debe listar 14 tablas: las 10 del 0001 + Roles, UsuariosRoles,
     -- TokensRefresco y TokensRevocados.

     SELECT codigo, idRolExterno FROM dbo_cotifly.Roles ORDER BY codigo;
     -- 2 filas (comercial, gerente_comercial); idRolExterno NULL hasta que el DBA
     -- fije los IDROLES de ClientePro (ver sección 2).
   ============================================================================ */
