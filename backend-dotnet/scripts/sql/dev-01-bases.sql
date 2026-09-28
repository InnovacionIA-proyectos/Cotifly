/* ============================================================================
   Cotifly — SOLO DESARROLLO LOCAL (scripts/levantar-local.*, #14). Nunca en QA ni producción.
   Crea en el SQL Server local las dos bases que en el entorno real ya existen:
     - dbTarifario_Nexo_Pruebas: la base donde vive el esquema dbo_cotifly (db/migrations_mssql/0001..0003).
     - dbsolucionesti: almacén corporativo de usuarios y roles de ClientePro (solo lectura para Cotifly).
   Idempotente. Se ejecuta en master con sqlcmd -I (QUOTED_IDENTIFIER ON).
   ============================================================================ */
IF DB_ID(N'dbTarifario_Nexo_Pruebas') IS NULL
    CREATE DATABASE [dbTarifario_Nexo_Pruebas];
GO
IF DB_ID(N'dbsolucionesti') IS NULL
    CREATE DATABASE [dbsolucionesti];
GO
-- dbTarifario_Nexo_Pruebas es compartida: en el entorno real tiene objetos de otras aplicaciones fuera de
-- dbo_cotifly. Esta tabla ficticia los representa para comprobar que cotifly_app NO puede leerlos ni escribirlos
-- (verificación de permisos de scripts/levantar-local.*).
USE [dbTarifario_Nexo_Pruebas];
GO
IF OBJECT_ID(N'dbo.tblOtraAplicacionDev', N'U') IS NULL
    CREATE TABLE dbo.tblOtraAplicacionDev (id INT NOT NULL PRIMARY KEY, dato NVARCHAR(50) NULL);
GO
