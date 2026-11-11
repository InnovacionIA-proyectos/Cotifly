/* ============================================================================
   Cotifly — 21: política de seguros por valor de la mercancía (tablas y código del conexo)
   ============================================================================
   HU-046.2 (#268). La consume HU-046.3 (#269, backend). Dueña: @scalderon-bs (Datos).
   Origen: carga aprobada en HU-046.1 (#267) y docs/backlog/2026-11-02_DECISIONES_HU-046_POLITICA_SEGUROS.md.

   Qué hace (NO destructivo e idempotente: solo crea y agrega; no borra ni cambia valores). Solo estructura:
   la carga inicial de la política y sus validaciones están en seeder/05_politica_seguros.sql, que el ejecutador
   corre después de los ajustes (así llegan igual a una base nueva y a una existente).

   1. Cuatro tablas nuevas. Las reglas viven en datos: un cambio de la política es un cambio de filas, no un despliegue.

        PoliticaSeguroTramos ──< PoliticaSeguroTramoMedidas >── PoliticaSeguroMedidas ──< PoliticaSeguroMedidaConexos

      PoliticaSeguroTramos         rango de valor de la mercancía de la ruta: valorDesde EXCLUSIVO y valorHasta INCLUIDO
                                   (250.000.000 exacto cae en "> 150 – 250 M"); valorHasta NULL = sin tope. Pesos fijos.
                                   Ni quién autoriza ni el proyecto especial se guardan (decisión 8; sin nota desde el
                                   2026-11-03). Una ruta con valor 0 no cae en ningún tramo (como una sin valor: aviso).
      PoliticaSeguroMedidas        catálogo de medidas (codigo único). esCobrable = 1 solo en las que se cumplen
                                   agregando un conexo de Nexo (los acompañantes).
      PoliticaSeguroTramoMedidas   qué medida aplica en cada tramo y con qué nivel: 'recomendada' u 'obligatoria'.
                                   Único (tramoId, medidaId).
      PoliticaSeguroMedidaConexos  códigos del catálogo de Nexo que cumplen cada medida (sin FK: es otra base).
                                   La obligación se cumple con CUALQUIERA de los códigos de la medida.
                                   Único (medidaId, codigoServicio).

      Todas con id UNIQUEIDENTIFIER DEFAULT NEWID(), como el resto del esquema.

   2. En CotizacionServiciosConexos: codigoServicio NVARCHAR(40) NULL (el código del catálogo de Nexo, p. ej. AVT_AUTO).
      idServicioConexo e idServicioConexoCatalogo guardan el id de una fila de tarifa (Nexo tiene una por trayecto), no
      el código: sin codigoServicio no se puede saber si el conexo es un acompañante. Las filas existentes quedan en NULL;
      el backend las completa (HU-046.3).

   Dónde corre: en una base existente, después del 20; no depende de ningún otro ajuste. Una base instalada con
   esquema/cotifly_esquema.sql ya trae todo esto y el script no cambia nada. El ejecutador
   (db/ejecutores/ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend no lee nada de esto hasta
   HU-046.3, así que se puede aplicar antes del backend sin romper nada.

   Respaldo: no hace falta — tablas nuevas y una columna nueva NULL; no se modifica ningún valor existente.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON, que exige el índice filtrado;
   -f 65001: los archivos son UTF-8). Sin USE: corre sobre la base que se indique. Después, el seeder de la carga:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 21_politica_seguros.sql
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i ../seeder/05_politica_seguros.sql
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
    THROW 50001, N'dbo_cotifly no tiene CotizacionServiciosConexos: instale primero el esquema (esquema/cotifly_esquema.sql) o las migraciones.', 1;
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
            id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroTramos_id DEFAULT (NEWID()),
            nombre              NVARCHAR(60)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            valorDesde          DECIMAL(18,2)       NOT NULL,   -- exclusivo
            valorHasta          DECIMAL(18,2)       NULL,       -- incluido; NULL = sin tope
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
            id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroMedidas_id DEFAULT (NEWID()),
            codigo              NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
            nombre              NVARCHAR(100) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,
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
        CREATE INDEX ix_politicaSeguroTramoMedidas_medidaId ON dbo_cotifly.PoliticaSeguroTramoMedidas (medidaId);
        PRINT N'21: tabla dbo_cotifly.PoliticaSeguroTramoMedidas creada.';
    END
    ELSE
        PRINT N'21: dbo_cotifly.PoliticaSeguroTramoMedidas ya existe, no se toca.';

    IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroMedidaConexos', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.PoliticaSeguroMedidaConexos (
            id                  UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_politicaSeguroMedidaConexos_id DEFAULT (NEWID()),
            medidaId            UNIQUEIDENTIFIER    NOT NULL,
            codigoServicio      NVARCHAR(40)  COLLATE Latin1_General_100_CI_AS_SC_UTF8 NOT NULL,   -- catálogo de Nexo, sin FK
            updatedAt           DATETIMEOFFSET      NOT NULL CONSTRAINT df_politicaSeguroMedidaConexos_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_politicaSeguroMedidaConexos PRIMARY KEY (id),
            CONSTRAINT uq_politicaSeguroMedidaConexos_medidaId_codigoServicio UNIQUE (medidaId, codigoServicio),
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

PRINT N'21 aplicado: política de seguros (4 tablas) y CotizacionServiciosConexos.codigoServicio.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (las 4 tablas y la columna nueva; los datos, después del seeder 05: 7 tramos
   contiguos, 7 medidas, 25 reglas, 8 conexos):

     SELECT id, nombre, valorDesde, valorHasta, isActive FROM dbo_cotifly.PoliticaSeguroTramos ORDER BY valorDesde;
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
