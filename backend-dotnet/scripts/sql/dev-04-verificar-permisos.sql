/* ============================================================================
   Cotifly — SOLO DESARROLLO LOCAL (scripts/levantar-local.*, #14).
   Verifica, CONECTADO COMO cotifly_app, que la receta de permisos mínimos de dev-03 alcanza para la API y no
   da más: lo que debe funcionar funciona y lo que no, falla con permiso denegado. Cualquier desvío termina con
   error (sqlcmd -b) y el script de arranque se detiene.
   Se ejecuta en dbTarifario_Nexo_Pruebas.
   ============================================================================ */
SET NOCOUNT ON;
SET XACT_ABORT OFF;
GO
-- Debe funcionar: tablas y vistas de dbo_cotifly, escritura y candados.
DECLARE @n INT;
SELECT @n = COUNT(*) FROM dbo_cotifly.Roles;
SELECT @n = COUNT(*) FROM dbo_cotifly.vActividadPorComercial;
SELECT @n = COUNT(*) FROM dbo_cotifly.vAjustesMargenPorComercial;
BEGIN TRANSACTION;
    UPDATE dbo_cotifly.Roles SET codigo = codigo WHERE 1 = 0;
    DECLARE @candado INT;
    EXEC @candado = sp_getapplock @Resource = N'cotifly:verificacion-permisos', @LockMode = N'Exclusive',
                                  @LockOwner = N'Transaction', @LockTimeout = 5000;
    IF @candado < 0 THROW 50001, 'cotifly_app no pudo tomar un candado con sp_getapplock.', 1;
ROLLBACK TRANSACTION;
PRINT 'OK: lectura y escritura en dbo_cotifly, vistas y sp_getapplock';
GO
-- No debe funcionar: otros esquemas de la base compartida.
DECLARE @permitido BIT = 0;
BEGIN TRY
    DECLARE @m INT;
    SELECT @m = COUNT(*) FROM dbo.tblOtraAplicacionDev;
    SET @permitido = 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 229 THROW;
END CATCH;
IF @permitido = 1 THROW 50002, 'cotifly_app puede LEER otro esquema de dbTarifario_Nexo_Pruebas (dbo).', 1;

BEGIN TRY
    INSERT INTO dbo.tblOtraAplicacionDev (id, dato) VALUES (-1, N'no debe entrar');
    SET @permitido = 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 229 THROW;
END CATCH;
IF @permitido = 1 THROW 50003, 'cotifly_app puede ESCRIBIR en otro esquema de dbTarifario_Nexo_Pruebas (dbo).', 1;
PRINT 'OK: sin lectura ni escritura fuera de dbo_cotifly';
GO
-- dbsolucionesti: columnas del login sí, CLAVE no.
DECLARE @n INT;
SELECT @n = COUNT(*) FROM dbsolucionesti.dbo.tblSAPUsuariosApp WHERE NOMBREUSUARIO IS NOT NULL AND EMAIL IS NOT NULL;
DECLARE @permitido BIT = 0;
BEGIN TRY
    DECLARE @c INT;
    SELECT @c = COUNT(CLAVE) FROM dbsolucionesti.dbo.tblSAPUsuariosApp;
    SET @permitido = 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() <> 230 THROW;
END CATCH;
IF @permitido = 1 THROW 50004, 'cotifly_app puede leer la columna CLAVE de dbsolucionesti.', 1;
PRINT 'OK: dbsolucionesti sin la columna CLAVE';
GO
