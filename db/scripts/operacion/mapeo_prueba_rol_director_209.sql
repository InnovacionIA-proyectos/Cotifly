/* ============================================================================
   Cotifly — OPERACIÓN (#209, parte B): mapeo PROVISIONAL de prueba de gerente_comercial y director
   ============================================================================
   *** SOLO PARA EL AMBIENTE DE PRUEBA (dbTarifario_Nexo_Pruebas). LO EJECUTA DATOS / EL DBA. ***
   *** NO lo corre el ejecutador ni el backend. NUNCA en producción: allá va el script de #212.     ***

   Propuesta del backend (HU-037.2, #210) para Sara (dueña de #209). Si ella ya aplicó la suya, esta debe dejar lo mismo.

   Por qué: en dbSolucionesTI_Prueba (identidad de QA, ADR-009) no existe ClienteProRegional. Mientras tanto:
     - gerente_comercial -> 25 (ClienteProAdmin, de prestado, provisional);
     - director          -> 31 (ClienteProGerencial, el que hoy usa gerente_comercial).
   comercial NO se toca (sigue en el valor que tenga; HU-025.1, #190).

   Orden: dbo_cotifly.Roles tiene el índice único filtrado uq_roles_idRolExterno (idRolExterno WHERE NOT NULL): dos roles no
   pueden tener el mismo IDROLES. Por eso primero se saca a gerente_comercial del 31 y recién después se le pone el 31 a
   director. Al revés, el segundo UPDATE falla.

   Qué hace (en una sola transacción):
     1. Respalda TODAS las filas de dbo_cotifly.Roles en dbo_cotifly.RespaldoRoles_209 (con respaldadoAt), solo la primera vez.
     2. gerente_comercial: 31 -> 25 SOLO si hoy tiene 31.
     3. director: NULL -> 31 SOLO si hoy está en NULL.
   Falla sin cambiar nada si:
     - no existe la fila director (falta db/scripts/15_rol_director.sql);
     - otro rol distinto de gerente_comercial ya tiene el 25, o uno distinto de gerente_comercial y director tiene el 31.
   Idempotente: si ya está 25/31 no cambia nada; un valor distinto (puesto a propósito) no se pisa, solo se avisa.

   Antes de correrlo (criterio de #209): confirmar en dbSolucionesTI_Prueba quién tiene hoy el rol 25 (ClienteProAdmin). Mientras
   dure el préstamo, esa persona entra como gerente_comercial. Y quien tenga hoy el 31 pasa a entrar como DIRECTOR (ve todas las
   regionales): si tenía asignado gerente_comercial en UsuariosRoles, su siguiente login queda en revisión (rol_externo_cambio)
   hasta que se le reasigne el rol.

   Ejecutar con: sqlcmd -I -b -d dbTarifario_Nexo_Pruebas -i mapeo_prueba_rol_director_209.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_roles_idRolExterno).
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

SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @gerente INT = (SELECT idRolExterno FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'gerente_comercial');
DECLARE @director INT = (SELECT idRolExterno FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'director');

IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = N'director')
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50002, N'No existe el rol director: aplique primero db/scripts/15_rol_director.sql. No se cambió nada.', 1;
END

IF EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE idRolExterno = 25 AND codigo <> N'gerente_comercial')
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50003, N'Otro rol interno ya tiene idRolExterno = 25. Revisar a mano; no se cambió nada.', 1;
END

IF EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE idRolExterno = 31 AND codigo NOT IN (N'gerente_comercial', N'director'))
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50004, N'Otro rol interno (ni gerente_comercial ni director) ya tiene idRolExterno = 31. Revisar a mano; no se cambió nada.', 1;
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

-- 1) Primero sacar a gerente_comercial del 31 (uq_roles_idRolExterno).
IF @gerente = 31
BEGIN
    UPDATE dbo_cotifly.Roles SET idRolExterno = 25, updatedAt = SYSDATETIMEOFFSET()
    WHERE codigo = N'gerente_comercial' AND idRolExterno = 31;
    PRINT N'gerente_comercial: idRolExterno 31 -> 25 (provisional, ClienteProAdmin).';
END
ELSE IF @gerente = 25
    PRINT N'gerente_comercial ya estaba en 25: no se cambia.';
ELSE
    PRINT CONCAT(N'gerente_comercial tiene idRolExterno = ', COALESCE(CAST(@gerente AS NVARCHAR(20)), N'NULL'),
                 N' (no es 31): no se cambia. Revisar con Datos.');

-- 2) Después, director al 31 (solo si quedó libre).
IF @director IS NULL
BEGIN
    IF EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE idRolExterno = 31)
        PRINT N'El 31 sigue ocupado por gerente_comercial: director NO se mapea. Revisar con Datos.';
    ELSE
    BEGIN
        UPDATE dbo_cotifly.Roles SET idRolExterno = 31, updatedAt = SYSDATETIMEOFFSET()
        WHERE codigo = N'director' AND idRolExterno IS NULL;
        PRINT N'director: idRolExterno NULL -> 31 (ClienteProGerencial).';
    END
END
ELSE IF @director = 31
    PRINT N'director ya estaba en 31: no se cambia.';
ELSE
    PRINT CONCAT(N'director tiene idRolExterno = ', CAST(@director AS NVARCHAR(20)), N' (no es NULL): no se cambia. Revisar con Datos.');

COMMIT TRANSACTION;
GO

/* Verificación (solo lectura). Esperado en prueba: gerente_comercial = 25 y director = 31 (comercial sin cambio). */
SELECT codigo, idRolExterno, activo, updatedAt,
       CASE codigo WHEN N'gerente_comercial' THEN 25 WHEN N'director' THEN 31 END AS esperado,
       CASE WHEN codigo = N'comercial' THEN N'SIN CAMBIO'
            WHEN idRolExterno = CASE codigo WHEN N'gerente_comercial' THEN 25 WHEN N'director' THEN 31 END THEN N'OK'
            ELSE N'REVISAR' END AS estado
FROM dbo_cotifly.Roles
ORDER BY codigo;
SELECT * FROM dbo_cotifly.RespaldoRoles_209 ORDER BY codigo;
GO

/* Reversa manual (solo con aprobación; en este orden por uq_roles_idRolExterno):
   UPDATE dbo_cotifly.Roles SET idRolExterno = NULL, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'director';
   UPDATE r SET idRolExterno = b.idRolExterno, updatedAt = SYSDATETIMEOFFSET()
   FROM dbo_cotifly.Roles AS r
   JOIN dbo_cotifly.RespaldoRoles_209 AS b ON b.id = r.id
   WHERE r.codigo IN (N'gerente_comercial', N'director');

   En dbTarifario_Nexo_Pruebas (compartida) RespaldoRoles_209 se borró el 2026-11-02 (@scalderon-bs). Tenía, del
   2026-10-26: comercial 34, gerente_comercial 31, director NULL. Ahí la reversa es a mano, en este orden:
   UPDATE dbo_cotifly.Roles SET idRolExterno = NULL, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'director';
   UPDATE dbo_cotifly.Roles SET idRolExterno = 31, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'gerente_comercial';
   Ojo: para pasar a los valores definitivos no se revierte; se usan los de #203 (34 / 29 / 31).
*/
