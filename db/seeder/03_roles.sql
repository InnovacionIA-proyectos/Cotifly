/* ============================================================================
   Seeder 03 — Roles internos de Cotifly (dbo_cotifly.Roles)
   ============================================================================
   Los dos roles nacen SIN rol externo (`idRolExterno` NULL): mientras sea NULL nadie puede
   iniciar sesión con ese rol (falla cerrado). El DBA lo fija con los IDROLES de ClientePro
   (dbsolucionesti.dbo.tblSAPRoles.IDROLES):

       UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'comercial';
       UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'gerente_comercial';

   Idempotente: inserta solo el rol que falta; volver a correrlo no pisa los valores que el DBA
   ya haya fijado.
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID(N'dbo_cotifly.Roles', N'U') IS NULL
    THROW 50001, N'Falta dbo_cotifly.Roles: corra primero scripts/01_crear_esquema.sql.', 1;
GO

INSERT INTO dbo_cotifly.Roles (codigo, descripcion)
SELECT v.codigo, v.descripcion
FROM (VALUES
    (N'comercial',         N'Comercial'),
    (N'gerente_comercial', N'Gerente comercial')
) AS v (codigo, descripcion)
WHERE NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles AS r WHERE r.codigo = v.codigo);
GO

/* Verificación: SELECT codigo, idRolExterno FROM dbo_cotifly.Roles ORDER BY codigo;
   -- 2 filas; idRolExterno NULL hasta que el DBA lo fije. */
