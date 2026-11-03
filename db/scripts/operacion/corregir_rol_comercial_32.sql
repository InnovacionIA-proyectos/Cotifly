/* ============================================================================
   Cotifly — OPERACIÓN (#173): el rol interno `comercial` debe mapear al IDROLES 32 (ClienteProComercial)
   ============================================================================
   *** REQUIERE APROBACIÓN. LO EJECUTA EL DBA. No lo corre el ejecutador ni el backend. ***

   Por qué: en dbSolucionesTI_Prueba (la base de identidad de QA que Cotifly comparte con Nexo, ADR-009) el IDROLES
   34 es `AdministracionZonasGeocerca`; el del comercial es el 32 (`ClienteProComercial`). La base compartida quedó con
   dbo_cotifly.Roles.idRolExterno = 34 para `comercial` (venía del seed de desarrollo dev-03-roles-y-login.sql). Con eso
   ningún comercial real entra como comercial y quien tenga el rol de geocerca entraría como comercial.
   El `gerente_comercial` = 31 (`ClienteProGerencial`) está bien: este script NO lo toca, solo lo muestra en la
   verificación.

   Qué hace (en una sola transacción):
     1. Respalda TODAS las filas de dbo_cotifly.Roles en dbo_cotifly.RespaldoRoles_173 (con respaldadoAt), solo si
        el respaldo no existe todavía (la primera vez).
     2. UPDATE idRolExterno 34 → 32 SOLO en la fila codigo = 'comercial' que hoy tenga 34.
   Falla sin cambiar nada si otro rol ya tiene el 32 (uq_roles_idRolExterno lo impediría a medias).

   Idempotente: si `comercial` ya está en 32, no cambia nada; si está en otro valor distinto de 34, tampoco (lo avisa:
   no se pisa un valor que alguien fijó a propósito).
   Ejecutar con: sqlcmd -I -b -d dbTarifario_Nexo_Pruebas -i corregir_rol_comercial_32.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_roles_idRolExterno).
   NUNCA en producción sin confirmar antes los IDROLES de la base de identidad de producción.

   Efecto en los usuarios: quien ya tenga una asignación `comercial` en UsuariosRoles conserva la asignación. Si en el
   portal tiene el 32, el siguiente login coincide y entra. Si solo tenía el 34 (geocerca) y ningún otro rol mapeado, el
   siguiente login (y el refresh) responde 401 `sin_rol_autorizado`: ningún rol externo suyo mapea a un rol interno, así
   que ni siquiera se llega a comparar con la asignación guardada (no es `rol_externo_cambio`). Si además tiene el 31,
   entra como `gerente_comercial` y, por la asignación `comercial` anterior, queda en revisión (`rol_externo_cambio`).
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

DECLARE @actual INT = (SELECT idRolExterno FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'comercial');

IF EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE idRolExterno = 32 AND codigo <> N'comercial')
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50002, N'Otro rol interno ya tiene idRolExterno = 32. Revisar a mano antes de seguir; no se cambió nada.', 1;
END

IF OBJECT_ID(N'dbo_cotifly.RespaldoRoles_173', N'U') IS NULL
BEGIN
    SELECT r.id, r.codigo, r.descripcion, r.idRolExterno, r.activo, r.updatedAt,
           SYSDATETIMEOFFSET() AS respaldadoAt
    INTO dbo_cotifly.RespaldoRoles_173
    FROM dbo_cotifly.Roles AS r;
    PRINT N'Roles respaldados en dbo_cotifly.RespaldoRoles_173.';
END
ELSE
    PRINT N'El respaldo dbo_cotifly.RespaldoRoles_173 ya existe: se conserva el de la primera ejecución.';

IF @actual = 34
BEGIN
    UPDATE dbo_cotifly.Roles
    SET idRolExterno = 32, updatedAt = SYSDATETIMEOFFSET()
    WHERE codigo = N'comercial' AND idRolExterno = 34;
    PRINT N'comercial: idRolExterno 34 → 32.';
END
ELSE IF @actual = 32
    PRINT N'comercial ya estaba en 32: no se cambia nada.';
ELSE
    PRINT CONCAT(N'comercial tiene idRolExterno = ', COALESCE(CAST(@actual AS NVARCHAR(20)), N'NULL'),
                 N' (no es 34): no se cambia. Revisar con el equipo de ClientePro.');

COMMIT TRANSACTION;
GO

/* Verificación (solo lectura). Esperado: comercial = 32 y gerente_comercial = 31. */
SELECT codigo, idRolExterno, activo, updatedAt,
       CASE codigo WHEN N'comercial' THEN 32 WHEN N'gerente_comercial' THEN 31 END AS esperado,
       CASE WHEN idRolExterno = CASE codigo WHEN N'comercial' THEN 32 WHEN N'gerente_comercial' THEN 31 END
            THEN N'OK' ELSE N'REVISAR' END AS estado
FROM dbo_cotifly.Roles
ORDER BY codigo;
SELECT * FROM dbo_cotifly.RespaldoRoles_173 ORDER BY codigo;
GO

/* Reversa manual (solo con aprobación):
   UPDATE r SET idRolExterno = b.idRolExterno, updatedAt = SYSDATETIMEOFFSET()
   FROM dbo_cotifly.Roles AS r
   JOIN dbo_cotifly.RespaldoRoles_173 AS b ON b.id = r.id
   WHERE r.codigo = N'comercial';

   En dbTarifario_Nexo_Pruebas (compartida) RespaldoRoles_173 se borró el 2026-11-02 (@scalderon-bs). Tenía, del
   2026-10-26: comercial 34, gerente_comercial 31, director NULL. Ahí la reversa es a mano:
   UPDATE dbo_cotifly.Roles SET idRolExterno = 34, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'comercial';
*/
