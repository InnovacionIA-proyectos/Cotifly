/* ============================================================================
   Seeder 05 — Política de seguros por valor de la mercancía (carga inicial y validaciones)
   ============================================================================
   Siembra la carga aprobada en HU-046.1 (#267) en las cuatro tablas de la política:
     PoliticaSeguroTramos         7 tramos de valor de la mercancía (valorDesde exclusivo, valorHasta
                                  incluido; el último sin tope). Pesos colombianos.
     PoliticaSeguroMedidas        7 medidas (esCobrable = 1 en las de acompañante, que se cumplen con
                                  un conexo de Nexo).
     PoliticaSeguroTramoMedidas   25 reglas: qué medida aplica en cada tramo y si es 'recomendada' u
                                  'obligatoria'.
     PoliticaSeguroMedidaConexos  8 códigos del catálogo de Nexo que cumplen las medidas de acompañante.

   Idempotente: solo siembra si las CUATRO tablas están vacías. Una política ya cambiada con
   scripts/operacion/cambiar_politica_seguros.sql no se pisa ni se duplica al volver a correrlo.

   Validaciones (en cada corrida, también cuando no siembra; si algo falla, THROW y no queda nada):
     - los tramos activos empiezan en 0, son contiguos (cada valorDesde = valorHasta del anterior), no
       se solapan ni dejan huecos, y solo el último no tiene tope;
     - toda medida 'obligatoria' de un tramo activo tiene al menos un conexo (si no, nunca se podría
       cumplir).

   Requiere las tablas de la política (esquema/cotifly_esquema.sql o scripts/21_politica_seguros.sql).
   Se corre con la base ya seleccionada (-d) y con -f 65001: los nombres de los tramos llevan "≤" y "–".
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.PoliticaSeguroTramos', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.PoliticaSeguroMedidas', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.PoliticaSeguroTramoMedidas', N'U') IS NULL
   OR OBJECT_ID(N'dbo_cotifly.PoliticaSeguroMedidaConexos', N'U') IS NULL
    THROW 50001, N'Faltan las tablas de la política de seguros: instale primero el esquema (esquema/cotifly_esquema.sql) o aplique scripts/21_politica_seguros.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- =========================================================================
    -- 1. Carga inicial (solo con las 4 tablas vacías)
    -- =========================================================================
    IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramos WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidas WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroTramoMedidas WITH (UPDLOCK, HOLDLOCK))
       AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos WITH (UPDLOCK, HOLDLOCK))
    BEGIN
        INSERT INTO dbo_cotifly.PoliticaSeguroTramos (nombre, valorDesde, valorHasta) VALUES
            (N'≤ 150 M',           0,          150000000),
            (N'> 150 – 250 M',     150000000,  250000000),
            (N'> 250 – 350 M',     250000000,  350000000),
            (N'> 350 – 500 M',     350000000,  500000000),
            (N'> 500 – 700 M',     500000000,  700000000),
            (N'> 700 – 1.000 M',   700000000,  1000000000),
            (N'> 1.000 M',         1000000000, NULL);

        INSERT INTO dbo_cotifly.PoliticaSeguroMedidas (codigo, nombre, esCobrable) VALUES
            (N'esquema_basico',        N'Esquema básico de seguridad',             0),
            (N'gps',                   N'GPS',                                     0),
            (N'candado_satelital',     N'Candado satelital',                       0),
            (N'restriccion_nocturna',  N'Restricción de tránsito nocturno',        0),
            (N'acompanante_vehicular', N'Acompañante vehicular (parcial o total)', 1),
            (N'acompanante_total',     N'Acompañante vehicular total',             1),
            (N'incluir_seguro',        N'Incluir el seguro de la mercancía',       0);

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
            THROW 50002, N'Seeder 05: la carga de PoliticaSeguroTramoMedidas no cruzó las 25 filas (nombre de tramo o código de medida mal escrito). No se aplicó nada.', 1;

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

        IF @@ROWCOUNT <> 8
            THROW 50005, N'Seeder 05: la carga de PoliticaSeguroMedidaConexos no cruzó las 8 filas (código de medida mal escrito). No se aplicó nada.', 1;

        PRINT N'Seeder 05: política de seguros sembrada (7 tramos, 7 medidas, 25 reglas, 8 conexos).';
    END
    ELSE
        PRINT N'Seeder 05: la política ya tiene datos, no se siembra (se respeta lo que haya cambiado operación).';

    -- =========================================================================
    -- 2. Validación 1: tramos activos contiguos, desde 0, sin solapes ni huecos, y solo el último sin tope
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
        DECLARE @mensajeTramos NVARCHAR(2048) = N'Seeder 05: los tramos activos no son válidos: ' + @errorTramos + N'. No se aplicó nada.';
        THROW 50003, @mensajeTramos, 1;
    END

    -- =========================================================================
    -- 3. Validación 2: una medida obligatoria tiene que poder cumplirse con algún conexo
    -- =========================================================================
    DECLARE @sinConexo NVARCHAR(400) = (
        SELECT TOP (1) m.codigo + N' (tramo ' + t.nombre + N')'
        FROM dbo_cotifly.PoliticaSeguroTramoMedidas AS tm
        JOIN dbo_cotifly.PoliticaSeguroTramos AS t ON t.id = tm.tramoId AND t.isActive = 1
        JOIN dbo_cotifly.PoliticaSeguroMedidas AS m ON m.id = tm.medidaId AND m.isActive = 1
        WHERE tm.nivel = 'obligatoria'
          AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.PoliticaSeguroMedidaConexos AS c WHERE c.medidaId = m.id));
    IF @sinConexo IS NOT NULL
    BEGIN
        DECLARE @mensajeConexo NVARCHAR(2048) = N'Seeder 05: la medida obligatoria ' + @sinConexo + N' no tiene ningún conexo que la cumpla. No se aplicó nada.';
        THROW 50004, @mensajeConexo, 1;
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'Seeder 05 NO se aplicó (carga inicial o validaciones): se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación (en una base nueva: 7 tramos contiguos, 7 medidas, 25 reglas, 8 conexos):

     SELECT (SELECT COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramos)        AS tramos,
            (SELECT COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidas)       AS medidas,
            (SELECT COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramoMedidas)  AS reglas,
            (SELECT COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidaConexos) AS conexos;

     SELECT t.nombre AS tramo, m.codigo AS medida, tm.nivel, STRING_AGG(c.codigoServicio, ', ') AS conexos
     FROM dbo_cotifly.PoliticaSeguroTramoMedidas tm
     JOIN dbo_cotifly.PoliticaSeguroTramos t ON t.id = tm.tramoId
     JOIN dbo_cotifly.PoliticaSeguroMedidas m ON m.id = tm.medidaId
     LEFT JOIN dbo_cotifly.PoliticaSeguroMedidaConexos c ON c.medidaId = m.id
     GROUP BY t.valorDesde, t.nombre, m.codigo, tm.nivel
     ORDER BY t.valorDesde, tm.nivel DESC, m.codigo;

   Tramo de una ruta (la regla que usa el backend):
     SELECT nombre FROM dbo_cotifly.PoliticaSeguroTramos
     WHERE isActive = 1 AND @valorMercancia > valorDesde AND (valorHasta IS NULL OR @valorMercancia <= valorHasta);
   ============================================================================ */
