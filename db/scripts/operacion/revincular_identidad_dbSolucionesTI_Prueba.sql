/* ============================================================================
   Cotifly — OPERACIÓN (#172): soltar los vínculos de UsuariosWeb con la base de identidad anterior
   ============================================================================
   *** REQUIERE APROBACIÓN. Ejecutarlo en la MISMA VENTANA del cambio de ConnectionStrings__Identidad
       a Database=dbSolucionesTI_Prueba, con el servicio detenido, o justo después del cambio de la
       variable y antes de que entren usuarios. ***
   Si corre mientras la API sigue con la configuración vieja (dbsolucionesti), el siguiente login vuelve
   a vincular a cada persona con el ID viejo (dguerra: 430) y el cambio queda a medias.

   Está en db/scripts/operacion/ a propósito: el ejecutador (ejecutar-bd.ps1 / .sh) solo corre los .sql de
   db/scripts (sin subcarpetas). Es un script de datos, no de esquema, y lo corre el DBA con un usuario
   administrador (crea la tabla de respaldo con SELECT ... INTO; cotifly_app no puede).

   Por qué: Cotifly guarda en UsuariosWeb.idUsuarioExterno el ID de tblSAPUsuariosApp de la base de identidad
   y lo manda a Nexo en X-Portal-clsUsuario-Id. Al pasar de dbsolucionesti a dbSolucionesTI_Prueba (la de Nexo
   QA, ADR-009) los ID cambian, y el login rechaza a quien quedó vinculado al ID viejo (401
   identidad_inconsistente). Con el vínculo en NULL, el siguiente login o refresh de cada persona la vincula
   al ID del portal (clsVerificadorIdentidad).

   Qué hace:
     1. Respalda los pares id / nombreUsuario / idUsuarioExterno con vínculo en
        dbo_cotifly.RespaldoVinculosIdentidad_172 (con la fecha del respaldo, respaldadoAt).
     2. Pone UsuariosWeb.idUsuarioExterno = NULL en esas filas.
   Las dos cosas van en una sola transacción.

   Idempotente: la tabla de respaldo es la marca de que ya corrió. Si existe, no toca nada (así no borra
   los vínculos nuevos que ya hicieron los usuarios al entrar).
   Ejecutar con: sqlcmd -I -b -d dbTarifario_Nexo_Pruebas -i revincular_identidad_dbSolucionesTI_Prueba.sql
   (-I: QUOTED_IDENTIFIER ON, que exige el índice filtrado uq_usuariosWeb_idUsuarioExterno).
   NUNCA en producción sin confirmar antes cuál es su base de identidad (la de Nexo producción).
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.UsuariosWeb', N'U') IS NULL
    THROW 50001, N'No existe dbo_cotifly.UsuariosWeb: ¿base equivocada?', 1;
GO

SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo_cotifly.RespaldoVinculosIdentidad_172', N'U') IS NOT NULL
BEGIN
    PRINT N'Ya se ejecutó: existe dbo_cotifly.RespaldoVinculosIdentidad_172. No se cambia nada.';
END
ELSE
BEGIN
    BEGIN TRANSACTION;

    SELECT u.id AS usuarioId,
           u.nombreUsuario,
           u.idUsuarioExterno,
           CAST(N'dbsolucionesti' AS NVARCHAR(128)) AS baseIdentidadAnterior,
           SYSDATETIMEOFFSET() AS respaldadoAt
    INTO dbo_cotifly.RespaldoVinculosIdentidad_172
    FROM dbo_cotifly.UsuariosWeb AS u WITH (UPDLOCK, HOLDLOCK)
    WHERE u.idUsuarioExterno IS NOT NULL;

    UPDATE dbo_cotifly.UsuariosWeb
    SET idUsuarioExterno = NULL
    WHERE idUsuarioExterno IS NOT NULL;

    COMMIT TRANSACTION;
    PRINT N'Vínculos respaldados en dbo_cotifly.RespaldoVinculosIdentidad_172 y puestos en NULL.';
END
GO

/* Verificación (solo lectura). Esperado tras la primera ejecución: vinculadosHoy = 0 (o solo quienes ya
   volvieron a entrar con la base nueva) y respaldados = los que tenían vínculo. */
SELECT
    (SELECT COUNT(*) FROM dbo_cotifly.UsuariosWeb WHERE idUsuarioExterno IS NOT NULL) AS vinculadosHoy,
    (SELECT COUNT(*) FROM dbo_cotifly.RespaldoVinculosIdentidad_172) AS respaldados,
    (SELECT MIN(respaldadoAt) FROM dbo_cotifly.RespaldoVinculosIdentidad_172) AS respaldadoAt;
GO

/* Reversa manual (solo si se vuelve a la base de identidad anterior, con aprobación):
   UPDATE u SET idUsuarioExterno = r.idUsuarioExterno
   FROM dbo_cotifly.UsuariosWeb AS u
   JOIN dbo_cotifly.RespaldoVinculosIdentidad_172 AS r ON r.usuarioId = u.id
   WHERE u.idUsuarioExterno IS NULL
     AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.UsuariosWeb AS o WHERE o.idUsuarioExterno = r.idUsuarioExterno);
*/
