/* ============================================================================
   Cotifly — 15: rol Director (esquema)
   ============================================================================
   HU-037.1 parte A (#209), habilita HU-037.2 (#210). Decisión de Sara del 2026-10-26: un rol nuevo
   `director`, que ve TODAS las regionales (a diferencia de `gerente_comercial`, que solo ve la suya). Por
   jerarquía, no multi-rol: un solo rol interno por usuario, igual que hoy.

   Qué hace (NO destructivo e idempotente: solo agrega; no cambia datos):
     1. Reemplaza el CHECK ck_roles_codigo (hoy solo 'comercial' y 'gerente_comercial', 01_crear_esquema.sql)
        por uno que también admita 'director'.
     2. Agrega la fila 'director' en dbo_cotifly.Roles, activa, con idRolExterno = NULL (nadie entra con
        ese rol hasta que se fije, igual que nacieron 'comercial' y 'gerente_comercial' en el seeder 03).
        El valor real lo pone el script de operación de la parte B (db/scripts/operacion/), porque cambia
        por ambiente — hoy en prueba no existe todavía el rol de ClientePro que le correspondería a
        'gerente_comercial' (ClienteProRegional), así que el mapeo de prueba es provisional.

   Dónde corre: después del 0009 (o del 01). No depende de ningún otro ajuste de este listado. El ejecutador
   (ejecutar-bd.ps1 / .sh) y levantar-local lo toman solos, en orden de nombre. El backend SÍ mapea el rol
   nuevo (HU-037.2, #210): sin este script, el enum RolInterno.Director no tiene fila que lo respalde en
   dbo_cotifly.Roles y ResolverRol nunca lo asignaría.

   Nota: `01_crear_esquema.sql` y `seeder/03_roles.sql` ya admiten e insertan `director` directamente (no se
   esperó a la consolidación del 01, HU-024.2). Este script 15 sigue siendo necesario para las bases que ya
   existen y fueron instaladas antes de ese cambio (como la compartida): idempotente, no hace nada si ya
   está aplicado.

   Respaldo: no hace falta. El CHECK se reemplaza por uno que admite todo lo que admitía antes (solo agrega
   'director') y la fila nueva no toca las dos que ya existen.

   Permisos: ninguno nuevo (cotifly_app ya tiene SELECT/INSERT/UPDATE/DELETE sobre el esquema).

   Cómo ejecutarlo (-b detiene el script en el primer error). Sin USE: corre sobre la base que se indique.
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 15_rol_director.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_roles_idRolExterno).
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
IF OBJECT_ID(N'dbo_cotifly.Roles', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene Roles: aplique primero el 0009 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. CHECK y fila nueva (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_roles_codigo'
               AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Roles') AND definition NOT LIKE N'%''director''%')
        ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT ck_roles_codigo;

    IF OBJECT_ID(N'dbo_cotifly.ck_roles_codigo', N'C') IS NULL
        EXEC (N'ALTER TABLE dbo_cotifly.Roles WITH CHECK ADD CONSTRAINT ck_roles_codigo
                 CHECK (codigo IN (''comercial'', ''gerente_comercial'', ''director''));');

    INSERT INTO dbo_cotifly.Roles (codigo, descripcion)
    SELECT N'director', N'Director'
    WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WHERE codigo = N'director');

    COMMIT TRANSACTION;
    PRINT N'15 aplicado: CHECK de Roles admite ''director'' y la fila ya existe (idRolExterno NULL hasta que lo fije la parte B).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'15 NO se aplicó: se deshizo todo. Error:';
    THROW;
END CATCH;
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (debe devolver 3 filas: comercial, gerente_comercial y director):

     SELECT codigo, idRolExterno, activo FROM dbo_cotifly.Roles ORDER BY codigo;
     SELECT definition FROM sys.check_constraints WHERE name = N'ck_roles_codigo';

   Reversa (solo antes de desplegar un backend que use RolInterno.Director; con él, el backend no tendría
   la fila que respalda el rol). Falla si ya hay alguien asignado a 'director' en UsuariosRoles.
     DELETE FROM dbo_cotifly.Roles WHERE codigo = N'director';
     ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT ck_roles_codigo;
     ALTER TABLE dbo_cotifly.Roles WITH CHECK ADD CONSTRAINT ck_roles_codigo CHECK (codigo IN ('comercial', 'gerente_comercial'));
   ============================================================================ */
