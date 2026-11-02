/* ============================================================================
   Cotifly — OPERACIÓN (HU-045.1, #263): cambiar la tasa del seguro de la mercancía
   ============================================================================
   *** REQUIERE APROBACIÓN DE LA TASA NUEVA. No lo corre el ejecutador ni el backend. ***

   Qué hace (en una sola transacción):
     1. Desactiva la tasa vigente de dbo_cotifly.TarifasSeguro (isActive = 0, updatedAt = ahora).
     2. Inserta la tasa nueva, activa, con vigenteDesde = ahora.
   No borra nada: las tasas viejas quedan como historia. Las cotizaciones ya calculadas no cambian,
   porque cada ruta guarda su tasaSeguroAplicada; la tasa nueva solo aplica a los cálculos nuevos.

   Antes de ejecutar: cambie @tasaNueva abajo. Va como fracción, no como porcentaje:
   0,345 % = 0.003450; 0,4 % = 0.004000. Si se deja en NULL, se detiene sin cambiar nada.

   Idempotente: si la tasa activa ya es @tasaNueva, no cambia nada (lo avisa).
   Falla sin cambiar nada si no existe TarifasSeguro (falta el 20) o si la tasa no está entre 0 y 1.

   Ejecutar con: sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i cambiar_tasa_seguro.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_tarifasSeguro_activa).
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.TarifasSeguro', N'U') IS NULL
    THROW 50001, N'No existe dbo_cotifly.TarifasSeguro: aplique primero scripts/20_seguro_mercancia.sql.', 1;
GO

SET XACT_ABORT ON;

DECLARE @tasaNueva DECIMAL(9, 6) = NULL;   -- <<< CAMBIAR: la tasa nueva como fracción (0.003450 = 0,345 %)

IF @tasaNueva IS NULL
    THROW 50002, N'Falta @tasaNueva: edite el script con la tasa aprobada. No se cambió nada.', 1;
IF @tasaNueva <= 0 OR @tasaNueva >= 1
    THROW 50003, N'@tasaNueva debe estar entre 0 y 1 (fracción, no porcentaje). No se cambió nada.', 1;

BEGIN TRANSACTION;

DECLARE @actual DECIMAL(9, 6) =
    (SELECT tasa FROM dbo_cotifly.TarifasSeguro WITH (UPDLOCK, HOLDLOCK) WHERE isActive = 1);

IF @actual = @tasaNueva
BEGIN
    COMMIT TRANSACTION;
    PRINT N'La tasa activa ya es ' + CONVERT(NVARCHAR(20), @tasaNueva) + N'. No se cambió nada.';
END
ELSE
BEGIN
    UPDATE dbo_cotifly.TarifasSeguro
    SET isActive = 0, updatedAt = SYSDATETIMEOFFSET()
    WHERE isActive = 1;

    INSERT INTO dbo_cotifly.TarifasSeguro (tasa, vigenteDesde, isActive)
    VALUES (@tasaNueva, SYSDATETIMEOFFSET(), 1);

    COMMIT TRANSACTION;
    PRINT N'Tasa del seguro: ' + ISNULL(CONVERT(NVARCHAR(20), @actual), N'(ninguna activa)')
        + N' -> ' + CONVERT(NVARCHAR(20), @tasaNueva) + N'.';
END

SELECT id, tasa, vigenteDesde, isActive, updatedAt
FROM dbo_cotifly.TarifasSeguro
ORDER BY id DESC;
GO
