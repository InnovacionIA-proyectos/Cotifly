/* ============================================================================
   Cotifly — OPERACIÓN (HU-046.2, #268): cambiar la política de seguros por valor de la mercancía
   ============================================================================
   *** REQUIERE APROBACIÓN DEL CAMBIO DE POLÍTICA. No lo corre el ejecutador ni el backend. ***

   La política vive en 4 tablas (script 21): un cambio es un cambio de filas, sin desplegar el backend. Las
   cotizaciones finalizadas no se recalculan (HU-036); el cambio aplica a las que todavía no están finalizadas.

   Cómo usarlo:
     1. Copie UNO de los ejemplos de abajo (están comentados) en el bloque "CAMBIO", ajuste los valores y quite
        el comentario.
     2. Ejecute el archivo completo. El cambio y la validación corren en una sola transacción: si los tramos quedan
        con un hueco o un solape, o una medida obligatoria queda sin conexos, se deshace todo.
     3. Revise el resumen que imprime al final.

   Sin ejemplo descomentado, el script no cambia nada: solo valida y muestra la política actual.

   Ejecutar con: sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i cambiar_politica_seguros.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado; -f 65001: los nombres de los tramos llevan "≤" y "–").
   ============================================================================

   ---------------------------------------------------------------------------
   EJEMPLO 1 — Cambiar el nivel de una medida en un tramo
   (p. ej., que el acompañante vehicular sea obligatorio desde 250 M).
   Si la medida todavía no está en ese tramo, el INSERT la agrega; si ya está, el UPDATE le cambia el nivel.
   ---------------------------------------------------------------------------
   DECLARE @tramo NVARCHAR(60) = N'> 250 – 350 M', @medida NVARCHAR(40) = N'acompanante_vehicular', @nivel NVARCHAR(20) = N'obligatoria';
   DECLARE @tramoId INT = (SELECT id FROM dbo_cotifly.PoliticaSeguroTramos WHERE nombre = @tramo AND isActive = 1);
   DECLARE @medidaId INT = (SELECT id FROM dbo_cotifly.PoliticaSeguroMedidas WHERE codigo = @medida AND isActive = 1);
   IF @tramoId IS NULL OR @medidaId IS NULL THROW 50010, N'No existe el tramo activo o la medida activa indicados.', 1;
   UPDATE dbo_cotifly.PoliticaSeguroTramoMedidas SET nivel = @nivel, updatedAt = SYSDATETIMEOFFSET()
   WHERE tramoId = @tramoId AND medidaId = @medidaId;
   IF @@ROWCOUNT = 0
       INSERT INTO dbo_cotifly.PoliticaSeguroTramoMedidas (tramoId, medidaId, nivel) VALUES (@tramoId, @medidaId, @nivel);
   -- Para QUITAR la medida del tramo:
   -- DELETE FROM dbo_cotifly.PoliticaSeguroTramoMedidas WHERE tramoId = @tramoId AND medidaId = @medidaId;

   ---------------------------------------------------------------------------
   EJEMPLO 2 — Agregar un conexo de Nexo a una medida
   (p. ej., si Nexo crea un acompañante nuevo). Verificar antes que el código existe en tbl_servicios_conexos de Nexo.
   ---------------------------------------------------------------------------
   DECLARE @medida NVARCHAR(40) = N'acompanante_total', @codigoServicio NVARCHAR(40) = N'AVT_CODIGO_NUEVO';
   DECLARE @medidaId INT = (SELECT id FROM dbo_cotifly.PoliticaSeguroMedidas WHERE codigo = @medida AND isActive = 1);
   IF @medidaId IS NULL THROW 50011, N'No existe la medida activa indicada.', 1;
   IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos WHERE medidaId = @medidaId AND codigoServicio = @codigoServicio)
       INSERT INTO dbo_cotifly.PoliticaSeguroMedidaConexos (medidaId, codigoServicio) VALUES (@medidaId, @codigoServicio);
   -- Para QUITAR un conexo de la medida:
   -- DELETE FROM dbo_cotifly.PoliticaSeguroMedidaConexos WHERE medidaId = @medidaId AND codigoServicio = @codigoServicio;

   ---------------------------------------------------------------------------
   EJEMPLO 3 — Mover un límite entre dos tramos
   (p. ej., que el límite de 350 M pase a 400 M). Se mueven a la vez el valorHasta del tramo de abajo y el valorDesde
   del de arriba, y se ajustan los nombres. Si solo se mueve uno, la validación lo detecta (hueco o solape).
   ---------------------------------------------------------------------------
   DECLARE @limiteActual DECIMAL(18,2) = 350000000, @limiteNuevo DECIMAL(18,2) = 400000000;
   UPDATE dbo_cotifly.PoliticaSeguroTramos SET valorHasta = @limiteNuevo, nombre = N'> 250 – 400 M', updatedAt = SYSDATETIMEOFFSET()
   WHERE isActive = 1 AND valorHasta = @limiteActual;
   UPDATE dbo_cotifly.PoliticaSeguroTramos SET valorDesde = @limiteNuevo, nombre = N'> 400 – 500 M', updatedAt = SYSDATETIMEOFFSET()
   WHERE isActive = 1 AND valorDesde = @limiteActual;
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroTramos', N'U') IS NULL
    THROW 50001, N'No existe dbo_cotifly.PoliticaSeguroTramos: aplique primero scripts/21_politica_seguros.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- =========================================================================
    -- CAMBIO: pegue aquí UNO de los ejemplos de la cabecera, ajustado.
    -- =========================================================================


    -- =========================================================================
    -- Validación (la misma del script 21). No la quite.
    -- =========================================================================
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
        DECLARE @mensajeTramos NVARCHAR(2048) = N'Los tramos activos no son válidos: ' + @errorTramos + N'. No se cambió nada.';
        THROW 50003, @mensajeTramos, 1;
    END

    DECLARE @sinConexo NVARCHAR(400) = (
        SELECT TOP (1) m.codigo + N' (tramo ' + t.nombre + N')'
        FROM dbo_cotifly.PoliticaSeguroTramoMedidas AS tm
        JOIN dbo_cotifly.PoliticaSeguroTramos AS t ON t.id = tm.tramoId AND t.isActive = 1
        JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.id = tm.medidaId AND m.isActive = 1
        WHERE tm.nivel = 'obligatoria'
          AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos AS c WHERE c.medidaId = m.id));
    IF @sinConexo IS NOT NULL
    BEGIN
        DECLARE @mensajeConexo NVARCHAR(2048) = N'La medida obligatoria ' + @sinConexo + N' no tiene ningún conexo que la cumpla. No se cambió nada.';
        THROW 50004, @mensajeConexo, 1;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'La política NO cambió: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- Resumen de la política vigente
SELECT t.nombre AS tramo, t.valorDesde, t.valorHasta, t.proyectoEspecial,
       m.codigo AS medida, tm.nivel,
       (SELECT STRING_AGG(c.codigoServicio, N', ') FROM dbo_cotifly.PoliticaSeguroMedidaConexos AS c WHERE c.medidaId = m.id) AS conexos
FROM dbo_cotifly.PoliticaSeguroTramos AS t
LEFT JOIN dbo_cotifly.PoliticaSeguroTramoMedidas AS tm ON tm.tramoId = t.id
LEFT JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.id = tm.medidaId AND m.isActive = 1
WHERE t.isActive = 1
ORDER BY t.valorDesde, tm.nivel DESC, m.codigo;
GO
