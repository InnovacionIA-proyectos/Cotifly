/* ============================================================================
   Cotifly — OPERACIÓN (#209): mapeo PROVISIONAL de prueba para gerente_comercial y director
   ============================================================================
   *** SOLO AMBIENTE DE PRUEBA/COMPARTIDA (dbSolucionesTI_Prueba). NUNCA en producción. ***
   *** REQUIERE APROBACIÓN. LO EJECUTA EL DBA O SCALDERON-BS. No lo corre el ejecutador ni el backend. ***

   Por qué: Sara (Gerencia Comercial) decidió el 2026-10-27 que `director` usa el id de ClientePro
   `ClienteProGerencial` (el mismo que hoy tiene `gerente_comercial`) y que `gerente_comercial` pasa a usar
   `ClienteProRegional`. El problema: en la base de prueba `ClienteProRegional` todavía NO EXISTE. Mientras
   tanto, `gerente_comercial` toma prestado el id de `ClienteProAdmin` (25 en prueba) — provisional, hasta
   que se despliegue a producción y se corra el script real (HU-037.4, #212, con los valores de producción:
   comercial = 34, gerente_comercial = 29 (`ClienteProRegional`), director = 31 (`ClienteProGerencial`)).

   Valores de prueba (`dbSolucionesTI_Prueba`), confirmados por Sara el 2026-10-27:
     ClienteProAdmin = 25, ClienteProComercial = 32, ClienteProGerencial = 31, ClienteProRegional = no existe.

   Qué hace (en una sola transacción, en este orden — el orden importa por uq_roles_idRolExterno):
     1. Respalda TODAS las filas de dbo_cotifly.Roles en dbo_cotifly.RespaldoRoles_209 (con respaldadoAt),
        solo si el respaldo no existe todavía (la primera vez).
     2. UPDATE idRolExterno de gerente_comercial: 31 → 25, SOLO si hoy tiene 31 (lo saca del 31 primero).
     3. UPDATE idRolExterno de director: → 31, SOLO si está en NULL (recién creado por el 15) o ya en 31.
   Falla sin cambiar nada si otro rol ya tiene el 25 (uq_roles_idRolExterno lo impediría a medias) o si
   director ya tiene un valor distinto de NULL/31 (no se pisa un valor que alguien fijó a propósito).

   Idempotente: si ya quedó en el estado esperado (gerente_comercial = 25, director = 31), no cambia nada.

   Ejecutar con: sqlcmd -I -b -d dbTarifario_Nexo_Pruebas -i mapeo_prueba_rol_director_209.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_roles_idRolExterno).

   Efecto en los usuarios: fase de prueba, nadie real tiene `ClienteProAdmin` ni `ClienteProGerencial`
   asignado todavía (confirmado por Sara). Si llegara a haber alguien con `ClienteProAdmin` por otro motivo,
   ese usuario entraría como `gerente_comercial` mientras dure el préstamo — confirmar antes de correr este
   script si ese es el caso.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.Roles', N'U') IS NULL
    THROW 50001, N'No existe dbo_cotifly.Roles: ¿base equivocada?', 1;
GO

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = N'director')
    THROW 50002, N'No existe el rol ''director''. Corra primero scripts/15_rol_director.sql.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @gerenteActual INT = (SELECT idRolExterno FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'gerente_comercial');
DECLARE @directorActual INT = (SELECT idRolExterno FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'director');

IF EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE idRolExterno = 25 AND codigo NOT IN (N'gerente_comercial'))
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50003, N'Otro rol interno ya tiene idRolExterno = 25. Revisar a mano antes de seguir; no se cambió nada.', 1;
END

IF OBJECT_ID(N'dbo_cotifly.RespaldoRoles_209', N'U') IS NULL
BEGIN
    SELECT r.id, r.codigo, r.descripcion, r.idRolExterno, r.activo, r.updatedAt,
           SYSDATETIMEOFFSET() AS respaldadoAt
    INTO dbo_cotifly.RespaldoRoles_209
    FROM dbo_cotifly.Roles AS r;
    PRINT N'Roles respaldados en dbo_cotifly.RespaldoRoles_209.';
END
ELSE
    PRINT N'El respaldo dbo_cotifly.RespaldoRoles_209 ya existe: se conserva el de la primera ejecución.';

-- Paso 1: sacar a gerente_comercial del 31 ANTES de dárselo a director (uq_roles_idRolExterno).
IF @gerenteActual = 31
BEGIN
    UPDATE dbo_cotifly.Roles
    SET idRolExterno = 25, updatedAt = SYSDATETIMEOFFSET()
    WHERE codigo = N'gerente_comercial' AND idRolExterno = 31;
    PRINT N'gerente_comercial: idRolExterno 31 → 25 (ClienteProAdmin, provisional).';
END
ELSE IF @gerenteActual = 25
    PRINT N'gerente_comercial ya estaba en 25: no se cambia nada.';
ELSE
    PRINT CONCAT(N'gerente_comercial tiene idRolExterno = ', COALESCE(CAST(@gerenteActual AS NVARCHAR(20)), N'NULL'),
                 N' (no es 31): no se cambia. Revisar con el equipo de ClientePro.');

-- Paso 2: darle el 31 a director, ya con gerente_comercial fuera de ese valor.
IF @directorActual IS NULL OR @directorActual = 31
BEGIN
    UPDATE dbo_cotifly.Roles
    SET idRolExterno = 31, updatedAt = SYSDATETIMEOFFSET()
    WHERE codigo = N'director' AND (idRolExterno IS NULL OR idRolExterno = 31);
    PRINT N'director: idRolExterno → 31 (ClienteProGerencial).';
END
ELSE
    PRINT CONCAT(N'director tiene idRolExterno = ', CAST(@directorActual AS NVARCHAR(20)),
                 N' (no es NULL ni 31): no se cambia. Revisar con el equipo de ClientePro.');

COMMIT TRANSACTION;
GO

/* Verificación (solo lectura). Esperado en prueba: comercial, gerente_comercial = 25, director = 31. */
SELECT codigo, idRolExterno, activo, updatedAt,
       CASE codigo WHEN N'gerente_comercial' THEN 25 WHEN N'director' THEN 31 ELSE idRolExterno END AS esperado,
       CASE WHEN idRolExterno = CASE codigo WHEN N'gerente_comercial' THEN 25 WHEN N'director' THEN 31 ELSE idRolExterno END
            THEN N'OK' ELSE N'REVISAR' END AS estado
FROM dbo_cotifly.Roles
ORDER BY codigo;
SELECT * FROM dbo_cotifly.RespaldoRoles_209 ORDER BY codigo;
GO

/* Reversa manual (solo con aprobación):
   UPDATE r SET idRolExterno = b.idRolExterno, updatedAt = SYSDATETIMEOFFSET()
   FROM dbo_cotifly.Roles AS r
   JOIN dbo_cotifly.RespaldoRoles_209 AS b ON b.id = r.id
   WHERE r.codigo IN (N'gerente_comercial', N'director');
*/
