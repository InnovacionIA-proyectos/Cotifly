/* ============================================================================
   Seeder 04 — Tasa inicial del seguro de la mercancía (dbo_cotifly.TarifasSeguro)
   ============================================================================
   Siembra la tasa 0.003450 (0,345 %, política de seguros NOGE0076) activa. Sin una tasa activa el
   backend no puede calcular el seguro de una ruta (responde error al marcar "incluir seguro").

   Idempotente: solo inserta si la tabla está VACÍA. Una tasa que ya se cambió con
   scripts/operacion/cambiar_tasa_seguro.sql (que desactiva la anterior y deja la nueva activa) no
   se pisa ni se duplica al volver a correrlo.

   Requiere la tabla TarifasSeguro (esquema/cotifly_esquema.sql o scripts/20_seguro_mercancia.sql).
   Se corre con la base ya seleccionada (-d). No crea nada.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.TarifasSeguro', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.TarifasSeguro: instale primero el esquema (esquema/cotifly_esquema.sql) o aplique scripts/20_seguro_mercancia.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.TarifasSeguro WITH (UPDLOCK, HOLDLOCK))
    BEGIN
        INSERT INTO dbo_cotifly.TarifasSeguro (tasa, vigenteDesde, isActive)
        VALUES (0.003450, SYSDATETIMEOFFSET(), 1);
        PRINT N'Seeder 04: tasa inicial del seguro 0.003450 (0,345 %) activa.';
    END
    ELSE
        PRINT N'Seeder 04: TarifasSeguro ya tiene filas, no se siembra (se respeta la tasa vigente).';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'Seeder 04 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* Verificación: SELECT tasa, vigenteDesde, isActive FROM dbo_cotifly.TarifasSeguro;
   -- en una base nueva, 1 fila: 0.003450 activa. */
