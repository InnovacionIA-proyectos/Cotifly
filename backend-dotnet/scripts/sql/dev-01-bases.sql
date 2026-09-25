/* ============================================================================
   Cotifly — SOLO DESARROLLO LOCAL (scripts/levantar-local.*, #14). Nunca en QA ni producción.
   Crea en el SQL Server local las dos bases que en el entorno real ya existen:
     - dbTarifario_Nexo_Pruebas: la base donde vive el esquema dbo_cotifly (migrations_mssql/0001..0003).
     - dbsolucionesti: almacén corporativo de usuarios y roles de ClientePro (solo lectura para Cotifly).
   Idempotente. Se ejecuta en master con sqlcmd -I (QUOTED_IDENTIFIER ON).
   ============================================================================ */
IF DB_ID(N'dbTarifario_Nexo_Pruebas') IS NULL
    CREATE DATABASE [dbTarifario_Nexo_Pruebas];
GO
IF DB_ID(N'dbsolucionesti') IS NULL
    CREATE DATABASE [dbsolucionesti];
GO
