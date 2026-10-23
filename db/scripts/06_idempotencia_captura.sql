/* ============================================================================
   Cotifly — 06 (#191, propuesto en #173): claves de idempotencia persistentes (Idempotency-Key)
   ============================================================================
   El backend la usa desde #191 (HU-026.1, parte C). Antes estaba en db/scripts/pendientes/ con "NO APLICAR". Desde #191 está en
   db/scripts/ y lo corren el ejecutador (ejecutar-bd.ps1 / .sh), db-init y levantar-local.*, por orden de nombre (después del 05).

   *** OBLIGATORIO ANTES DE DESPLEGAR LA IMAGEN DE #191 (lo aprueba y aplica el DBA) ***
   Sin la tabla, /health/ready responde 503 "esquema incompleto: falta 06": la API no degrada a memoria (ver "Sin la tabla").
   Verificación de solo lectura (debe dar un número, no NULL, y 1):
     SELECT OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') AS tabla,
            CASE WHEN COL_LENGTH(N'dbo_cotifly.CapturasIdempotentes', N'operacion') IS NULL THEN 0 ELSE 1 END AS versionDe191;

   Numeración (decisión de #191): conserva el número 06 con que se propuso en #173 (el DBA y los documentos ya lo llaman así; está
   libre en db/scripts/). Va por nombre entre el 05 y el 07; solo depende del 01 (el esquema dbo_cotifly).

   Por qué (deuda ADR-018 §12): POST /web/solicitudes-costeo, POST .../costeo/servicios-conexos, POST .../costeo/re-solicitar y
   PUT /web/cotizaciones/{folio}/solicitud aceptan Idempotency-Key. Hasta #191 la clave, su resultado y la captura "en curso" (folio
   confirmado antes de llamar a Nexo y el solicitudNexoId en cuanto se conoce) vivían en la memoria del proceso: se perdían al reiniciar
   la API y en un despliegue start-first (dos réplicas a la vez), y un reintento que cayera en la otra réplica volvía a crear la
   solicitud en Nexo. Con esta tabla:
     - la clave se reserva con un INSERT que choca con el UNIQUE (usuarioId, operacion, clave) si otra réplica ya la tomó: de dos
       POST simultáneos en réplicas distintas uno gana y el otro recibe 409 (ESTADO_NO_PERMITE, detalle.motivo = CAPTURA_EN_CURSO);
       en la misma réplica (el doble clic) el segundo espera al primero y recibe su respuesta, como antes;
     - el folio de la captura se anota en la fila DENTRO de la transacción que lo confirma, y el solicitudNexoId en cuanto Nexo lo
       devuelve: el reintento lee el folio y el id de aquí y no vuelve a llamar a Nexo;
     - la respuesta que consume la clave queda en `resultado` (JSON) y la repetición la devuelve sin volver a ejecutar.

   Qué hace: crea dbo_cotifly.CapturasIdempotentes (una fila por clave, comercial y operación):
     operacion      capturar_costeo | conexos | re_solicitar_costeo | editar_solicitud (sin CHECK de valores: una operación nueva
                    con Idempotency-Key no necesita otro script).
     huella         SHA-256 (hex) del cuerpo. Los números se comparan por valor (1.5 = 1.50). Misma clave con otra huella = 409
                    IDEMPOTENCY_KEY_REUTILIZADA.
     estado         en_curso  -> una petición la tiene tomada (tomadaPor) hasta enCursoHasta;
                    terminada -> `resultado` tiene la respuesta que consumió la clave;
                    libre     -> la petición terminó sin consumirla (error, o la API cayó a mitad); la conserva solo si anotó un folio.
     enCursoHasta   vence la toma: si la API se cae a mitad, la fila "en_curso" queda huérfana y, pasado este momento (10 min), la
                    siguiente petición con la clave la retoma (si ya había folio, sin volver a llamar a Nexo). Nunca bloquea para siempre.
     cotizacionId, folio, solicitudNexoId, scorePrioridad: la captura en curso (solo capturar_costeo).
     expiraAt       24 h (contrato IdempotencyKey). La API ignora las vencidas y las borra al reusar la clave; una tarea de la API
                    (cada hora) borra las demás vencidas en lotes de 1000. No hace falta un job del DBA.
   Sin FK a CotizacionesSolicitud a propósito: la fila se reserva antes de que exista el folio y se purga sola.
   Volumen: una fila por POST/PUT con clave en las últimas 24 h (decenas o cientos al día).

   Sin la tabla: la API NO degrada a memoria (con dos réplicas volvería el duplicado en Nexo sin que nadie lo note); la sonda
   /health/ready responde 503 "esquema incompleto: falta 06" y el despliegue no recibe tráfico hasta aplicar este script.

   Permisos: ninguno nuevo. cotifly_app ya tiene SELECT, INSERT, UPDATE y DELETE sobre el esquema dbo_cotifly.

   Idempotente y no destructivo. Si encuentra la tabla de la propuesta anterior (#173, sin la columna `operacion`, nunca usada por el
   backend) y está VACÍA, la recrea; con filas se detiene sin tocar nada.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF SCHEMA_ID(N'dbo_cotifly') IS NULL
    THROW 50001, N'No existe el esquema dbo_cotifly: corra primero 01_crear_esquema.sql.', 1;
GO

/* Propuesta anterior (#173): la tabla sin `operacion`. El backend nunca la usó; vacía se recrea con la forma de #191. */
IF OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo_cotifly.CapturasIdempotentes', N'operacion') IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo_cotifly.CapturasIdempotentes)
        THROW 50002, N'dbo_cotifly.CapturasIdempotentes tiene la forma de la propuesta de #173 y tiene filas: revísela con el equipo antes de aplicar el 06.', 1;
    DROP TABLE dbo_cotifly.CapturasIdempotentes;
    PRINT N'06: se retiró la tabla vacía de la propuesta de #173.';
END
GO

IF OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') IS NULL
BEGIN
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
        scorePrioridad    DECIMAL(9,4)  NULL,                                                 -- captura: el que Nexo devolvió con el id (09)
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
    PRINT N'06 aplicado: dbo_cotifly.CapturasIdempotentes creada.';
END
GO

/* Limpieza de vencidas (DELETE ... WHERE expiraAt <= @ahora). */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_capturasIdempotentes_expiraAt'
               AND object_id = OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes'))
    CREATE INDEX ix_capturasIdempotentes_expiraAt ON dbo_cotifly.CapturasIdempotentes (expiraAt);
GO

/* Reintento de un folio (POST .../costeo/reintentar): la captura registrada con ese folio, con cualquier clave. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_capturasIdempotentes_folio'
               AND object_id = OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes'))
    CREATE INDEX ix_capturasIdempotentes_folio ON dbo_cotifly.CapturasIdempotentes (folio)
        INCLUDE (solicitudNexoId, scorePrioridad, expiraAt)
        WHERE folio IS NOT NULL;
GO

/* Verificación (solo lectura). */
SELECT OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U') AS tabla,
       (SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'uq_capturasIdempotentes_usuarioId_operacion_clave') AS unicos,
       (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes')
           AND name IN (N'ix_capturasIdempotentes_expiraAt', N'ix_capturasIdempotentes_folio')) AS indices;
GO

/* Reversa (solo con aprobación y con la imagen anterior a #191 desplegada; la de #191 la exige en /health/ready):
   DROP TABLE dbo_cotifly.CapturasIdempotentes;
*/
