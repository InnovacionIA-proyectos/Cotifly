/* ============================================================================
   Cotifly — 20: seguro de la mercancía por ruta (tasa parametrizada y columnas en la ruta)
   ============================================================================
   HU-045.1 (#263). La consume HU-045.2 (cálculo en el backend). Dueña: @scalderon-bs (Datos).
   Origen: política de seguros NOGE0076 y docs/backlog/2026-11-02_HUS_SEGURO_Y_POLITICA.md.

   Qué hace (NO destructivo e idempotente: solo crea y agrega; no borra ni cambia valores):

   1. Tabla nueva dbo_cotifly.TarifasSeguro — la tasa del seguro, con su historia:
          id            UNIQUEIDENTIFIER PK         DEFAULT NEWID(), como el resto del esquema
          tasa          DECIMAL(9,6)     NOT NULL   0.003450 = 0,345 % (CHECK > 0 y < 1)
          vigenteDesde  DATETIMEOFFSET   NOT NULL   desde cuándo rige (informativo: se usa la activa)
          isActive      BIT              NOT NULL   DEFAULT 1
          updatedAt     DATETIMEOFFSET   NOT NULL   DEFAULT SYSDATETIMEOFFSET()
      Índice único filtrado uq_tarifasSeguro_activa (WHERE isActive = 1): solo UNA tasa activa.
      Las tasas viejas se desactivan, no se borran. Para cambiarla: scripts/operacion/cambiar_tasa_seguro.sql.
      Fila inicial: tasa 0.003450 activa, solo si la tabla está vacía (no pisa una tasa ya cambiada).
      DECIMAL(9,6) y no (9,4) como los márgenes: con 4 decimales 0,345 % quedaría redondeado a 0.0035.

   2. En CotizacionLineasSolicitud (la ruta):
          incluyeSeguro       BIT            NOT NULL   DEFAULT 0  la casilla por ruta
          tasaSeguroAplicada  DECIMAL(9,6)   NULL       copia de la tasa con la que se calculó
          costoSeguro         DECIMAL(18,2)  NULL       valorMercancia × tasaSeguroAplicada (sin margen)
      La tasa se copia en la ruta para que un cambio de tasa no mueva las cotizaciones ya hechas
      (igual que margenAplicado). costoSeguro va aparte: NO entra en costoFinal ni lleva margen.
      CHECKs:
          ck_cotizacionLineasSolicitud_seguroMontos: costoSeguro >= 0, tasaSeguroAplicada > 0 y < 1,
              y si hay costoSeguro hay tasaSeguroAplicada (el costo sale de la tasa).
          ck_cotizacionLineasSolicitud_seguroIncluido: si incluyeSeguro = 0, costoSeguro y
              tasaSeguroAplicada quedan en NULL.
      Con incluyeSeguro = 1 y sin valorMercancia, costoSeguro puede quedar NULL: si eso bloquea
      finalizar lo decide el backend (HU-045.2), no la base.

   Las filas existentes quedan con incluyeSeguro = 0 (el DEFAULT de la columna NOT NULL): ninguna
   cotización cambia de valor.

   Dónde corre: después del 19 (o del 01); no depende de ningún otro ajuste. El ejecutador
   (ejecutar-bd.ps1 / .sh) lo toma solo, en orden de nombre. El backend no lee estas columnas hasta
   HU-045.2, así que se puede aplicar antes del backend sin romper nada.

   Respaldo: no hace falta — tabla nueva y columnas nuevas; no se modifica ningún valor existente.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON, que exige el
   índice filtrado). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 20_seguro_mercancia.sql
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
IF OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene CotizacionLineasSolicitud: aplique primero 01_crear_esquema.sql.', 1;
GO

-- =============================================================================
-- 1. Tabla TarifasSeguro y columnas de la ruta (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo_cotifly.TarifasSeguro', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo_cotifly.TarifasSeguro (
            id              UNIQUEIDENTIFIER    NOT NULL CONSTRAINT df_tarifasSeguro_id DEFAULT (NEWID()),
            tasa            DECIMAL(9,6)        NOT NULL,
            vigenteDesde    DATETIMEOFFSET      NOT NULL,
            isActive        BIT                 NOT NULL CONSTRAINT df_tarifasSeguro_isActive DEFAULT (1),
            updatedAt       DATETIMEOFFSET      NOT NULL CONSTRAINT df_tarifasSeguro_updatedAt DEFAULT (SYSDATETIMEOFFSET()),
            CONSTRAINT pk_tarifasSeguro PRIMARY KEY (id),
            CONSTRAINT ck_tarifasSeguro_tasa CHECK (tasa > 0 AND tasa < 1)
        );
        CREATE UNIQUE INDEX uq_tarifasSeguro_activa ON dbo_cotifly.TarifasSeguro (isActive) WHERE isActive = 1;
        PRINT N'20: tabla dbo_cotifly.TarifasSeguro creada.';
    END
    ELSE
        PRINT N'20: dbo_cotifly.TarifasSeguro ya existe, no se toca.';

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'incluyeSeguro') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD incluyeSeguro BIT NOT NULL
            CONSTRAINT df_cotizacionLineasSolicitud_incluyeSeguro DEFAULT (0);

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'tasaSeguroAplicada') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD tasaSeguroAplicada DECIMAL(9, 6) NULL;

    IF COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'costoSeguro') IS NULL
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud ADD costoSeguro DECIMAL(18, 2) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'20 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- Fila inicial y CHECKs en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce
-- las columnas al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.TarifasSeguro WITH (UPDLOCK, HOLDLOCK))
    BEGIN
        INSERT INTO dbo_cotifly.TarifasSeguro (tasa, vigenteDesde, isActive)
        VALUES (0.003450, SYSDATETIMEOFFSET(), 1);
        PRINT N'20: tasa inicial 0.003450 (0,345 %) activa.';
    END

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_seguroMontos'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'))
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_seguroMontos
            CHECK ((costoSeguro IS NULL OR costoSeguro >= 0)
                   AND (tasaSeguroAplicada IS NULL OR (tasaSeguroAplicada > 0 AND tasaSeguroAplicada < 1))
                   AND (costoSeguro IS NULL OR tasaSeguroAplicada IS NOT NULL));

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_seguroIncluido'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud'))
        ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud WITH CHECK ADD CONSTRAINT ck_cotizacionLineasSolicitud_seguroIncluido
            CHECK (incluyeSeguro = 1 OR (costoSeguro IS NULL AND tasaSeguroAplicada IS NULL));

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'20 NO se aplicó completo (tasa inicial y CHECKs del seguro): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'20 aplicado: TarifasSeguro y seguro de la mercancía en CotizacionLineasSolicitud.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (una tasa activa en 0.003450, tres columnas, dos CHECKs, ninguna
   ruta con seguro):

     SELECT id, tasa, vigenteDesde, isActive FROM dbo_cotifly.TarifasSeguro;
     SELECT c.name AS columna, TYPE_NAME(c.user_type_id) AS tipo, c.precision, c.scale, c.is_nullable
     FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionLineasSolicitud')
       AND c.name IN ('incluyeSeguro', 'tasaSeguroAplicada', 'costoSeguro');
     SELECT name, definition FROM sys.check_constraints
     WHERE name IN ('ck_tarifasSeguro_tasa', 'ck_cotizacionLineasSolicitud_seguroMontos', 'ck_cotizacionLineasSolicitud_seguroIncluido');
     SELECT COUNT(*) AS rutas_con_seguro FROM dbo_cotifly.CotizacionLineasSolicitud WHERE incluyeSeguro = 1;  -- 0

   Reversa (solo antes de desplegar un backend que las mapee):
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_seguroIncluido;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT ck_cotizacionLineasSolicitud_seguroMontos;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP CONSTRAINT df_cotizacionLineasSolicitud_incluyeSeguro;
     ALTER TABLE dbo_cotifly.CotizacionLineasSolicitud DROP COLUMN incluyeSeguro, tasaSeguroAplicada, costoSeguro;
     DROP TABLE dbo_cotifly.TarifasSeguro;
   ============================================================================ */
