/* ============================================================================
   Cotifly — 16: varias asignaciones activas por usuario y rol de la sesión en el refresh (PROPUESTA, pendiente de revisión
   de @scalderon-bs)
   ============================================================================
   HU-040.1 (#229, Datos; la consume HU-040.2, #230). Multi-rol: una persona con varios roles (p. ej. comercial y gerente
   comercial) elige con cuál entra, en vez de que gane siempre el de mayor alcance (ADR-009 y HU-037).

   Qué hace (NO destructivo e idempotente; no borra ni cambia ninguna fila):
     1. UsuariosRoles: retira el índice único filtrado uq_usuariosRoles_usuarioId_activo (usuarioId) WHERE activo = 1, que
        permitía UNA sola asignación activa por usuario. Se conserva uq_usuariosRoles_usuarioId_rolId (usuarioId, rolId): el
        mismo rol no se puede asignar dos veces al mismo usuario (activo o no). Imprime la definición anterior como respaldo.
     2. TokensRefresco: agrega rolSesion NVARCHAR(30) NULL, el rol interno con el que se emitió la sesión, para que el refresh
        conserve el rol elegido (no vuelve a preguntar) y revalide que siga vigente. CHECK ck_tokensRefresco_rolSesion: NULL o
        uno de los códigos de Roles ('comercial', 'gerente_comercial', 'director'). NULL = refresh emitido antes del 16.

   Dónde corre: después del 15 (o del 01). Lo toman solos el ejecutador (ejecutar-bd.ps1 / .sh) y levantar-local, en orden de
   nombre. El backend de HU-040.1 SÍ mapea rolSesion: sin el 16, /health/ready responde 503 "esquema incompleto: falta 16", así
   que se aplica ANTES de desplegar la imagen.

   ¿Es seguro con la imagen que está desplegada hoy (anterior a HU-040)? Sí (a diferencia del 15, que agregó una fila al catálogo):
     - quitar el índice no cambia ninguna fila: mientras nadie agregue una segunda asignación activa, todo usuario sigue con una;
       el alta JIT de esa imagen crea una sola. Si Datos agrega una segunda asignación activa ANTES de la imagen de HU-040.1, esa
       imagen rechaza solo a ese usuario (rol_externo_cambio, 401 genérico): falla cerrado, nadie entra con un rol de más;
     - rolSesion es NULL y la imagen vieja no la conoce: sus INSERT nombran sus columnas y la dejan en NULL.
   Con la imagen de HU-040.1 un usuario con dos asignaciones activas entra como hoy, con la jerarquía (director > gerente_comercial
   > comercial), siempre que todas sus asignaciones activas sigan respaldadas por un rol de ClientePro. La elección del rol llega
   con HU-040.2 (#230).

   Respaldo: no hace falta (no cambia filas). Se imprime la definición del índice que se retira.
   Permisos: ninguno nuevo.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON por los índices filtrados).
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 16_multiples_roles_activos.sql
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 0. Precondiciones
-- =============================================================================
IF OBJECT_ID(N'dbo_cotifly.UsuariosRoles', N'U') IS NULL OR OBJECT_ID(N'dbo_cotifly.TokensRefresco', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene UsuariosRoles o TokensRefresco: aplique primero el 0002 (o instale con 01_crear_esquema.sql).', 1;
GO

-- Sin la unicidad (usuarioId, rolId) no se afloja nada: sería posible duplicar el mismo rol activo.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles') AND name = N'uq_usuariosRoles_usuarioId_rolId' AND is_unique = 1)
    THROW 50002, N'Falta uq_usuariosRoles_usuarioId_rolId en UsuariosRoles: no se retira la unicidad de la asignación activa sin ella.', 1;
GO

-- =============================================================================
-- 1. UsuariosRoles: varias asignaciones activas por usuario (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles') AND name = N'uq_usuariosRoles_usuarioId_activo')
    BEGIN
        DECLARE @filtro NVARCHAR(MAX) = (
            SELECT filter_definition FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles') AND name = N'uq_usuariosRoles_usuarioId_activo');
        PRINT CONCAT(N'Índice anterior (respaldo): CREATE UNIQUE INDEX uq_usuariosRoles_usuarioId_activo ON dbo_cotifly.UsuariosRoles (usuarioId) WHERE ',
                     COALESCE(@filtro, N'(sin filtro)'), N';');
        DROP INDEX uq_usuariosRoles_usuarioId_activo ON dbo_cotifly.UsuariosRoles;
        PRINT N'uq_usuariosRoles_usuarioId_activo retirado: un usuario puede tener varias asignaciones activas (una por rol).';
    END
    ELSE
        PRINT N'uq_usuariosRoles_usuarioId_activo ya no existe: no se cambia.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'16 NO se aplicó (índice de UsuariosRoles): se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- =============================================================================
-- 2. TokensRefresco.rolSesion (columna y CHECK)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.TokensRefresco', N'rolSesion') IS NULL
        ALTER TABLE dbo_cotifly.TokensRefresco ADD rolSesion NVARCHAR(30) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'16 NO se aplicó completo (TokensRefresco.rolSesion): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

-- El CHECK va en otro lote: dentro del mismo lote del ADD, SQL Server todavía no conoce la columna al compilar.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'ck_tokensRefresco_rolSesion'
                   AND parent_object_id = OBJECT_ID(N'dbo_cotifly.TokensRefresco'))
        EXEC (N'ALTER TABLE dbo_cotifly.TokensRefresco WITH CHECK ADD CONSTRAINT ck_tokensRefresco_rolSesion
                 CHECK (rolSesion IS NULL OR rolSesion IN (''comercial'', ''gerente_comercial'', ''director''));');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'16 NO se aplicó completo (CHECK de rolSesion): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'16 aplicado: varias asignaciones activas por usuario y TokensRefresco.rolSesion.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (0 filas del índice retirado; 1 de la unicidad usuario+rol; la columna y su CHECK):

     SELECT name, is_unique, filter_definition FROM sys.indexes
     WHERE object_id = OBJECT_ID(N'dbo_cotifly.UsuariosRoles') AND name LIKE N'uq_usuariosRoles%';
     SELECT c.name, TYPE_NAME(c.user_type_id) AS tipo, c.max_length, c.is_nullable FROM sys.columns c
     WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.TokensRefresco') AND c.name = N'rolSesion';
     SELECT name FROM sys.check_constraints WHERE name = N'ck_tokensRefresco_rolSesion';

   Usuarios con más de una asignación activa (los que tendrán selector de rol con HU-040.2):
     SELECT usuarioId, COUNT(*) AS activas FROM dbo_cotifly.UsuariosRoles WHERE activo = 1 GROUP BY usuarioId HAVING COUNT(*) > 1;

   Reversa (solo con una imagen anterior a HU-040.1; con ella, /health/ready vuelve a 503 "falta 16"). Volver a crear el índice
   exige antes dejar UNA asignación activa por usuario (la consulta anterior debe devolver 0 filas), si no el CREATE falla:
     ALTER TABLE dbo_cotifly.TokensRefresco DROP CONSTRAINT ck_tokensRefresco_rolSesion;
     ALTER TABLE dbo_cotifly.TokensRefresco DROP COLUMN rolSesion;
     CREATE UNIQUE INDEX uq_usuariosRoles_usuarioId_activo ON dbo_cotifly.UsuariosRoles (usuarioId) WHERE activo = 1;
   ============================================================================ */
