/* ============================================================================
   Cotifly — 21: política de seguros por valor de la mercancía (tablas, carga inicial y código del conexo)
   ============================================================================
   HU-046.2 (#268). La consume HU-046.3 (#269, backend). Dueña: @scalderon-bs (Datos).
   Origen: carga aprobada en HU-046.1 (#267) y docs/backlog/2026-11-02_DECISIONES_HU-046_POLITICA_SEGUROS.md.

   Qué hace (NO destructivo e idempotente: solo crea, agrega y siembra; no borra ni cambia valores):

   1. Cuatro tablas nuevas. Las reglas viven en datos: un cambio de la política es un cambio de filas, no un despliegue.

        PoliticaSeguroTramos ──< PoliticaSeguroTramoMedidas >── PoliticaSeguroMedidas ──< PoliticaSeguroMedidaConexos

      PoliticaSeguroTramos         rango de valor de la mercancía de la ruta: valorDesde EXCLUSIVO y valorHasta INCLUIDO
                                   (250.000.000 exacto cae en "> 150 – 250 M"); valorHasta NULL = sin tope. Pesos fijos.
                                   proyectoEspecial marca los tramos de 700 M en adelante. Quién autoriza NO se guarda
                                   (decisión 8). Una ruta con valor 0 no cae en ningún tramo (como una sin valor: aviso).
      PoliticaSeguroMedidas        catálogo de medidas (codigo único). esCobrable = 1 solo en las que se cumplen
                                   agregando un conexo de Nexo (los acompañantes).
      PoliticaSeguroTramoMedidas   qué medida aplica en cada tramo y con qué nivel: 'recomendada' u 'obligatoria'.
                                   PK (tramoId, medidaId).
      PoliticaSeguroMedidaConexos  códigos del catálogo de Nexo que cumplen cada medida (sin FK: es otra base).
                                   La obligación se cumple con CUALQUIERA de los códigos de la medida.
                                   PK (medidaId, codigoServicio).

   2. En CotizacionServiciosConexos: codigoServicio NVARCHAR(40) NULL (el código del catálogo de Nexo, p. ej. AVT_AUTO).
      Las filas existentes quedan en NULL; el backend las completa (HU-046.3).

   3. Carga inicial (la aprobada en #267), SOLO si las 4 tablas están vacías: una política ya cambiada con el script de
      operación no se pisa al volver a correr este.

   4. Validaciones (en cada corrida; si algo falla, THROW y no se aplica nada):
        - los tramos activos empiezan en 0, son contiguos (cada valorDesde = valorHasta del anterior), no se solapan ni
          dejan huecos, y solo el último no tiene tope;
        - toda medida 'obligatoria' tiene al menos un conexo (si no, nunca se podría cumplir).

   Dónde corre: después del 20 (o del 01); no depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 / .sh) lo
   toma solo, en orden de nombre. El backend no lee nada de esto hasta HU-046.3, así que se puede aplicar antes del
   backend sin romper nada.

   Respaldo: no hace falta — tablas nuevas y una columna nueva NULL; no se modifica ningún valor existente.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON, que exige el índice filtrado;
   -f 65001: el archivo es UTF-8, los nombres de los tramos llevan "≤" y "–"). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 21_politica_seguros.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondición
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionServiciosConexos: aplique primero 01_crear_esquema.sql.', 1;
GO

-- =============================================================================
-- 1. Tablas y columna (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroTramos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.PoliticaSeguroTramos (
            id                  INT IDENTITY(1,1)   NOT NULL,
            nombre              NVARCHAR(60)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            valorDesde          DECIMAL(18,2)       NOT NULL,   -- exclusivo
            valorHasta          DECIMAL(18,2)       NULL,       -- incluido; NULL = sin tope
            proyectoEspecial    BIT                 NOT NULL CONSTRAINT df_politicaSeguroTramos_proyectoEspecial DEFAULT (0),
            nota                NVARCHAR(500) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            isActive            BIT                 NOT NULL CONSTRAINT df_politicaSeguroTramos_isActive DEFAULT (1),
            updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroTramos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_politicaSeguroTramos PRIMARY KEY (id),
            CONSTRAINT ck_politicaSeguroTramos_valores CHECK (valorDesde >= 0 AND (valorHasta IS NULL OR valorHasta > valorDesde))
        );
        CREATE UNIQUE INDEX uq_politicaSeguroTramos_valorDesde_activo
            ON dbo_cotifly.PoliticaSeguroTramos (valorDesde) WHERE isActive = 1;
        PRINT N'21: tabla dbo_cotifly.PoliticaSeguroTramos creada.';
    END
    ELSE
        PRINT N'21: dbo_cotifly.PoliticaSeguroTramos ya existe, no se toca.';

    IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroMedidas', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.PoliticaSeguroMedidas (
            id                  INT IDENTITY(1,1)   NOT NULL,
            codigo              NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            nombre              NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            descripcion         NVARCHAR(300) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            esCobrable          BIT                 NOT NULL CONSTRAINT df_politicaSeguroMedidas_esCobrable DEFAULT (0),
            isActive            BIT                 NOT NULL CONSTRAINT df_politicaSeguroMedidas_isActive DEFAULT (1),
            updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroMedidas_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_politicaSeguroMedidas PRIMARY KEY (id),
            CONSTRAINT uq_politicaSeguroMedidas_codigo UNIQUE (codigo)
        );
        PRINT N'21: tabla dbo_cotifly.PoliticaSeguroMedidas creada.';
    END
    ELSE
        PRINT N'21: dbo_cotifly.PoliticaSeguroMedidas ya existe, no se toca.';

    IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroTramoMedidas', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.PoliticaSeguroTramoMedidas (
            tramoId             INT                 NOT NULL,
            medidaId            INT                 NOT NULL,
            nivel               NVARCHAR(20)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            nota                NVARCHAR(300) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroTramoMedidas_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_politicaSeguroTramoMedidas PRIMARY KEY (tramoId, medidaId),
            CONSTRAINT fk_politicaSeguroTramoMedidas_tramoId FOREIGN KEY (tramoId) REFERENCES dbo_cotifly.PoliticaSeguroTramos (id),
            CONSTRAINT fk_politicaSeguroTramoMedidas_medidaId FOREIGN KEY (medidaId) REFERENCES dbo_cotifly.PoliticaSeguroMedidas (id),
            CONSTRAINT ck_politicaSeguroTramoMedidas_nivel CHECK (nivel IN ('recomendada', 'obligatoria'))
        );
        CREATE INDEX ix_politicaSeguroTramoMedidas_medidaId ON dbo_cotifly.PoliticaSeguroTramoMedidas (medidaId);
        PRINT N'21: tabla dbo_cotifly.PoliticaSeguroTramoMedidas creada.';
    END
    ELSE
        PRINT N'21: dbo_cotifly.PoliticaSeguroTramoMedidas ya existe, no se toca.';

    IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroMedidaConexos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.PoliticaSeguroMedidaConexos (
            medidaId            INT                 NOT NULL,
            codigoServicio      NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- catálogo de Nexo, sin FK
            updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroMedidaConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_politicaSeguroMedidaConexos PRIMARY KEY (medidaId, codigoServicio),
            CONSTRAINT fk_politicaSeguroMedidaConexos_medidaId FOREIGN KEY (medidaId) REFERENCES dbo_cotifly.PoliticaSeguroMedidas (id)
        );
        PRINT N'21: tabla dbo_cotifly.PoliticaSeguroMedidaConexos creada.';
    END
    ELSE
        PRINT N'21: dbo_cotifly.PoliticaSeguroMedidaConexos ya existe, no se toca.';

    IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'codigoServicio') IS NULL
    BEGIN
        ALTER TABLE dbo_cotifly.CotizacionServiciosConexos
            ADD codigoServicio NVARCHAR(40) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;
        PRINT N'21: columna CotizacionServiciosConexos.codigoServicio agregada.';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'21 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- =============================================================================
-- 2. Carga inicial (solo con las 4 tablas vacías) y validaciones, en otra transacción
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramos WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidas WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramoMedidas WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos WITH (UPDLOCK, HOLDLOCK))
    BEGIN
        INSERT INTO dbo_cotifly.PoliticaSeguroTramos (nombre, valorDesde, valorHasta, proyectoEspecial, nota) VALUES
            (N'≤ 150 M',           0,          150000000,  0, NULL),
            (N'> 150 – 250 M',     150000000,  250000000,  0, NULL),
            (N'> 250 – 350 M',     250000000,  350000000,  0, NULL),
            (N'> 350 – 500 M',     350000000,  500000000,  0, NULL),
            (N'> 500 – 700 M',     500000000,  700000000,  0, NULL),
            (N'> 700 – 1.000 M',   700000000,  1000000000, 1, N'Proyecto especial.'),
            (N'> 1.000 M',         1000000000, NULL,       1, N'Proyecto especial. Caso a caso: la operación se analiza aparte.');

        INSERT INTO dbo_cotifly.PoliticaSeguroMedidas (codigo, nombre, descripcion, esCobrable) VALUES
            (N'esquema_basico',        N'Esquema básico de seguridad',             NULL, 0),
            (N'gps',                   N'GPS',                                     NULL, 0),
            (N'candado_satelital',     N'Candado satelital',                       N'No hay conexo en Nexo: solo se recomienda.', 0),
            (N'restriccion_nocturna',  N'Restricción de tránsito nocturno',        NULL, 0),
            (N'acompanante_vehicular', N'Acompañante vehicular (parcial o total)', N'Se cumple con cualquier acompañante, parcial o total.', 1),
            (N'acompanante_total',     N'Acompañante vehicular total',             N'Se cumple con un acompañante total.', 1),
            (N'incluir_seguro',        N'Incluir el seguro de la mercancía',       N'Si el cliente no tiene póliza propia, incluye el seguro de la mercancía en la cotización.', 0);

        INSERT INTO dbo_cotifly.PoliticaSeguroTramoMedidas (tramoId, medidaId, nivel)
        SELECT t.id, m.id, v.nivel
        FROM (VALUES
            (N'≤ 150 M',         N'esquema_basico',        'recomendada'),
            (N'> 150 – 250 M',   N'esquema_basico',        'recomendada'),
            (N'> 150 – 250 M',   N'candado_satelital',     'recomendada'),
            (N'> 250 – 350 M',   N'esquema_basico',        'recomendada'),
            (N'> 250 – 350 M',   N'gps',                   'recomendada'),
            (N'> 250 – 350 M',   N'candado_satelital',     'recomendada'),
            (N'> 250 – 350 M',   N'incluir_seguro',        'recomendada'),
            (N'> 350 – 500 M',   N'esquema_basico',        'recomendada'),
            (N'> 350 – 500 M',   N'gps',                   'recomendada'),
            (N'> 350 – 500 M',   N'candado_satelital',     'recomendada'),
            (N'> 350 – 500 M',   N'restriccion_nocturna',  'recomendada'),
            (N'> 350 – 500 M',   N'acompanante_vehicular', 'obligatoria'),
            (N'> 350 – 500 M',   N'incluir_seguro',        'recomendada'),
            (N'> 500 – 700 M',   N'esquema_basico',        'recomendada'),
            (N'> 500 – 700 M',   N'gps',                   'recomendada'),
            (N'> 500 – 700 M',   N'candado_satelital',     'recomendada'),
            (N'> 500 – 700 M',   N'restriccion_nocturna',  'recomendada'),
            (N'> 500 – 700 M',   N'acompanante_vehicular', 'obligatoria'),
            (N'> 500 – 700 M',   N'incluir_seguro',        'recomendada'),
            (N'> 700 – 1.000 M', N'esquema_basico',        'recomendada'),
            (N'> 700 – 1.000 M', N'gps',                   'recomendada'),
            (N'> 700 – 1.000 M', N'candado_satelital',     'recomendada'),
            (N'> 700 – 1.000 M', N'restriccion_nocturna',  'recomendada'),
            (N'> 700 – 1.000 M', N'acompanante_total',     'obligatoria'),
            (N'> 700 – 1.000 M', N'incluir_seguro',        'recomendada')
        ) AS v (tramo, medida, nivel)
        JOIN dbo_cotifly.PoliticaSeguroTramos AS t ON t.nombre = v.tramo
        JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.codigo = v.medida;

        IF @@ROWCOUNT <> 25
            THROW 50002, N'21: la carga de PoliticaSeguroTramoMedidas no cruzó las 25 filas (nombre de tramo o código de medida mal escrito). No se aplicó nada.', 1;

        INSERT INTO dbo_cotifly.PoliticaSeguroMedidaConexos (medidaId, codigoServicio)
        SELECT m.id, v.codigoServicio
        FROM (VALUES
            (N'acompanante_vehicular', N'AVP_MOTO'),
            (N'acompanante_vehicular', N'AVP_MOTO_ARMADA'),
            (N'acompanante_vehicular', N'AVT_AUTO'),
            (N'acompanante_vehicular', N'AVT_MOTO'),
            (N'acompanante_vehicular', N'AVT_MOTO_ARMADA'),
            (N'acompanante_total',     N'AVT_AUTO'),
            (N'acompanante_total',     N'AVT_MOTO'),
            (N'acompanante_total',     N'AVT_MOTO_ARMADA')
        ) AS v (medida, codigoServicio)
        JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.codigo = v.medida;

        PRINT N'21: carga inicial de la política sembrada (7 tramos, 7 medidas, 25 reglas, 8 conexos).';
    END
    ELSE
        PRINT N'21: la política ya tiene datos, no se siembra (se respeta lo que haya cambiado operación).';

    -- Validación 1: tramos activos contiguos, desde 0, sin solapes ni huecos, y solo el último sin tope.
    DECLARE @errorTramos NVARCHAR(400) = (
        SELECT TOP (1) motivo FROM (
            SELECT CASE
                       WHEN anterior.id IS NULL AND t.valorDesde <> 0 THEN N'el primer tramo no empieza en 0 (' + t.nombre + N')'
                       WHEN anterior.id IS NOT NULL AND anterior.valorHasta IS NULL THEN N'un tramo sin tope no es el último (' + anterior.nombre + N')'
                       WHEN anterior.id IS NOT NULL AND t.valorDesde > anterior.valorHasta THEN N'hueco entre ' + anterior.nombre + N' y ' + t.nombre
                       WHEN anterior.id IS NOT NULL AND t.valorDesde < anterior.valorHasta THEN N'solape entre ' + anterior.nombre + N' y ' + t.nombre
                   END AS motivo
            FROM dbo_cotifly.PoliticaSeguroTramos AS t
            OUTER APPLY (SELECT TOP (1) a.id, a.nombre, a.valorHasta
                         FROM dbo_cotifly.PoliticaSeguroTramos AS a
                         WHERE a.isActive = 1 AND a.valorDesde < t.valorDesde
                         ORDER BY a.valorDesde DESC) AS anterior
            WHERE t.isActive = 1
        ) AS x
        WHERE motivo IS NOT NULL);
    IF @errorTramos IS NULL
       AND EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramos WHERE isActive = 1)
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramos WHERE isActive = 1 AND valorHasta IS NULL)
        SET @errorTramos = N'ningún tramo activo queda sin tope (el último debe tener valorHasta NULL)';
    IF @errorTramos IS NOT NULL
    BEGIN
        DECLARE @mensajeTramos NVARCHAR(2048) = N'21: los tramos activos no son válidos: ' + @errorTramos + N'. No se aplicó nada.';
        THROW 50003, @mensajeTramos, 1;
    END

    -- Validación 2: una medida obligatoria tiene que poder cumplirse con algún conexo.
    DECLARE @sinConexo NVARCHAR(400) = (
        SELECT TOP (1) m.codigo + N' (tramo ' + t.nombre + N')'
        FROM dbo_cotifly.PoliticaSeguroTramoMedidas AS tm
        JOIN dbo_cotifly.PoliticaSeguroTramos AS t ON t.id = tm.tramoId AND t.isActive = 1
        JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.id = tm.medidaId AND m.isActive = 1
        WHERE tm.nivel = 'obligatoria'
          AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos AS c WHERE c.medidaId = m.id));
    IF @sinConexo IS NOT NULL
    BEGIN
        DECLARE @mensajeConexo NVARCHAR(2048) = N'21: la medida obligatoria ' + @sinConexo + N' no tiene ningún conexo que la cumpla. No se aplicó nada.';
        THROW 50004, @mensajeConexo, 1;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'21 NO se aplicó completo (carga inicial o validaciones): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'21 aplicado: política de seguros (4 tablas) y CotizacionServiciosConexos.codigoServicio.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (7 tramos contiguos, 7 medidas, 25 reglas, 8 conexos, la columna nueva):

     SELECT id, nombre, valorDesde, valorHasta, proyectoEspecial, isActive FROM dbo_cotifly.PoliticaSeguroTramos ORDER BY valorDesde;
     SELECT t.nombre AS tramo, m.codigo AS medida, tm.nivel,
            STRING_AGG(c.codigoServicio, ', ') AS conexos
     FROM dbo_cotifly.PoliticaSeguroTramoMedidas tm
     JOIN dbo_cotifly.PoliticaSeguroTramos t ON t.id = tm.tramoId
     JOIN dbo_cotifly.PoliticaSeguroMedidas m ON m.id = tm.medidaId
     LEFT JOIN dbo_cotifly.PoliticaSeguroMedidaConexos c ON c.medidaId = m.id
     GROUP BY t.valorDesde, t.nombre, m.codigo, tm.nivel
     ORDER BY t.valorDesde, tm.nivel DESC, m.codigo;
     SELECT COL_LENGTH('dbo_cotifly.CotizacionServiciosConexos', 'codigoServicio') AS codigoServicio_bytes;  -- 80

   Tramo de una ruta (la regla que usa el backend):
     SELECT nombre FROM dbo_cotifly.PoliticaSeguroTramos
     WHERE isActive = 1 AND @valorMercancia > valorDesde AND (valorHasta IS NULL OR @valorMercancia <= valorHasta);

   Reversa (solo antes de desplegar un backend que las mapee):
     ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP COLUMN codigoServicio;
     DROP TABLE dbo_cotifly.PoliticaSeguroMedidaConexos;
     DROP TABLE dbo_cotifly.PoliticaSeguroTramoMedidas;
     DROP TABLE dbo_cotifly.PoliticaSeguroMedidas;
     DROP TABLE dbo_cotifly.PoliticaSeguroTramos;
   ============================================================================ */
