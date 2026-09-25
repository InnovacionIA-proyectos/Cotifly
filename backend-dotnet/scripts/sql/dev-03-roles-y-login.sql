/* ============================================================================
   Cotifly — SOLO DESARROLLO LOCAL (scripts/levantar-local.*, #14). Nunca en QA ni producción.
   1. Fija en dbo_cotifly.Roles los IDROLES de desarrollo (34 comercial, 35 gerente_comercial). En el entorno
      real lo hace el DBA con los IDROLES de ClientePro (ADR-009); sin eso nadie entra (fail-closed).
   2. Crea el SQL login de servicio cotifly_app con los permisos MÍNIMOS que se piden al DBA
      (docs/03-infraestructura.md): lectura y escritura en dbTarifario_Nexo_Pruebas y SELECT por columna
      en dbsolucionesti, sin la columna CLAVE. Así el entorno local prueba también esa receta de permisos.
   Variable de sqlcmd: $(ClaveApp) = contraseña de cotifly_app (llega como variable de entorno del contenedor de sqlcmd).
   Idempotente. Se ejecuta en master con sqlcmd -I (QUOTED_IDENTIFIER ON: Roles tiene un índice filtrado).
   ============================================================================ */
SET NOCOUNT ON;
GO
UPDATE [dbTarifario_Nexo_Pruebas].dbo_cotifly.Roles SET idRolExterno = 34 WHERE codigo = 'comercial';
UPDATE [dbTarifario_Nexo_Pruebas].dbo_cotifly.Roles SET idRolExterno = 35 WHERE codigo = 'gerente_comercial';
GO
IF SUSER_ID(N'cotifly_app') IS NULL
    CREATE LOGIN [cotifly_app] WITH PASSWORD = N'$(ClaveApp)', CHECK_POLICY = OFF, DEFAULT_DATABASE = [dbTarifario_Nexo_Pruebas];
ELSE
    ALTER LOGIN [cotifly_app] WITH PASSWORD = N'$(ClaveApp)';
GO
USE [dbTarifario_Nexo_Pruebas];
GO
IF USER_ID(N'cotifly_app') IS NULL
    CREATE USER [cotifly_app] FOR LOGIN [cotifly_app];
ALTER ROLE db_datareader ADD MEMBER [cotifly_app];
ALTER ROLE db_datawriter ADD MEMBER [cotifly_app];
GO
USE [dbsolucionesti];
GO
IF USER_ID(N'cotifly_app') IS NULL
    CREATE USER [cotifly_app] FOR LOGIN [cotifly_app];
GRANT SELECT ON dbo.tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL) TO [cotifly_app];
GRANT SELECT ON dbo.tblSAPUsuarioRoles (ID, IDUSUARIO, IDROLES) TO [cotifly_app];
GRANT SELECT ON dbo.tblSAPRoles (IDROLES, DESCRIPCION) TO [cotifly_app];
GO
