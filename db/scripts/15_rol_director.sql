/* ============================================================================
   Cotifly — 15: rol interno `director`
   ============================================================================
   HU-037.1 (#209, parte A; la escribe Datos, @scalderon-bs). PROPUESTA del backend (HU-037.2, #210) para poder probar: si Sara
   ya aplicó la suya en la base compartida, esta debe dejar exactamente el mismo resultado (CHECK con los tres códigos y la fila
   `director` activa con idRolExterno NULL).

   Por qué: el Director comercial ve todas las regionales (HU-037, ADR-016 §11). Es un tercer rol interno, por encima del gerente
   comercial: con varios roles externos mapeados gana director > gerente_comercial > comercial (regla auditada
   `precedencia_director`).

   Qué hace (NO destructivo e idempotente; en una sola transacción):
     1. Reemplaza el CHECK ck_roles_codigo (hoy codigo IN ('comercial', 'gerente_comercial'), del 0002 y del 01) por uno que
        además admite 'director'. Solo si a la definición actual le falta 'director'; si ya lo tiene, no lo toca. Imprime la
        definición anterior como respaldo.
     2. Inserta la fila `director` en dbo_cotifly.Roles, activa y con idRolExterno = NULL (nadie entra con ese rol hasta que
        se fije su IDROLES de ClientePro). Si ya existe, no la toca (no pisa el idRolExterno que haya fijado el DBA).

   El idRolExterno real NO va aquí: cambia por ambiente. Lo fija un script de operación (db/scripts/operacion/):
     - prueba:      mapeo_prueba_rol_director_209.sql (gerente_comercial 31 -> 25 y luego director -> 31), #209 parte B;
     - producción:  el de #212 (comercial 34, gerente_comercial 29, director 31), cuando haya fecha de despliegue.
   uq_roles_idRolExterno (índice único filtrado) impide que dos roles tengan el mismo IDROLES: por eso el orden de esos scripts.

   Dónde corre: después del 13 (o del 01). No depende de ningún otro ajuste. El ejecutador (ejecutar-bd.ps1 / .sh) y
   levantar-local lo toman solos, en orden de nombre. El 14 es de HU-036 (en paralelo); son independientes.

   ¿Lo exige /health/ready? No. No agrega columnas: el backend de HU-037 arranca igual sin él. Sin el 15 no hay fila `director`,
   así que nadie entra como director (falla cerrado) y comercial y gerente siguen como hoy.
   OJO al revés: un backend ANTERIOR a HU-037 lee el catálogo de roles con el enum de dos valores. Con la fila `director`
   presente, ese backend falla al leer el catálogo en el login. Aplicar el 15 junto con (o después de) la imagen de HU-037.2;
   desde HU-037.2 el backend ignora los códigos de rol que no conoce.

   Respaldo: no hace falta (no cambia filas existentes). La definición anterior del CHECK se imprime.
   Permisos: ninguno nuevo.

   Cómo ejecutarlo (-b detiene el script en el primer error; -I: QUOTED_IDENTIFIER ON por el índice filtrado de Roles).
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 15_rol_director.sql
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
    THROW 50001, N'dbo_cotifly no tiene Roles: aplique primero el 0002 (o instale con 01_crear_esquema.sql).', 1;
GO

-- =============================================================================
-- 1. CHECK y fila (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @definicion NVARCHAR(MAX) = (
        SELECT definition FROM sys.check_constraints
        WHERE name = N'ck_roles_codigo' AND parent_object_id = OBJECT_ID(N'dbo_cotifly.Roles'));

    IF @definicion IS NULL OR @definicion NOT LIKE N'%''director''%'
    BEGIN
        PRINT CONCAT(N'CHECK anterior de ck_roles_codigo (respaldo): ', COALESCE(@definicion, N'(no existía)'));
        IF @definicion IS NOT NULL
            ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT ck_roles_codigo;
        ALTER TABLE dbo_cotifly.Roles WITH CHECK ADD CONSTRAINT ck_roles_codigo
            CHECK (codigo IN ('comercial', 'gerente_comercial', 'director'));
        PRINT N'ck_roles_codigo admite comercial, gerente_comercial y director.';
    END
    ELSE
        PRINT N'ck_roles_codigo ya admite director: no se cambia.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'15 NO se aplicó (CHECK): se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- La fila va en otro lote: dentro del mismo lote del ADD CONSTRAINT, SQL Server compila el INSERT con el CHECK anterior.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.Roles WITH (UPDLOCK, HOLDLOCK) WHERE codigo = N'director')
    BEGIN
        INSERT INTO dbo_cotifly.Roles (codigo, descripcion, idRolExterno, activo)
        VALUES (N'director', N'Director comercial', NULL, 1);
        PRINT N'Rol director agregado (activo, idRolExterno NULL).';
    END
    ELSE
        PRINT N'El rol director ya existe: no se cambia (se conserva su idRolExterno).';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'15 NO se aplicó completo (fila director): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

-- HU-040.3 (#258): si la columna requiereAltaManual ya existe (el 23 corrió antes, p. ej. en el orden del arreglo de pruebas), este 15 deja
-- marcado `director` como sensible, para que el resultado no dependa del orden entre el 15 y el 23. En el orden normal (15 antes del 23) la
-- columna todavía no existe aquí y es el 23 el que marca `director`. Idempotente en ambos casos.
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    -- La columna puede no existir todavía (orden normal: 15 antes del 23). El UPDATE va en SQL dinámico porque SQL Server resuelve los
    -- nombres de columna al COMPILAR el lote: referirse directo a requiereAltaManual rompería el lote aunque el IF no lo ejecute.
    IF COL_LENGTH(N'dbo_cotifly.Roles', N'requiereAltaManual') IS NOT NULL
    BEGIN
        EXEC (N'UPDATE dbo_cotifly.Roles SET requiereAltaManual = 1 WHERE codigo = N''director'' AND requiereAltaManual = 0;');
        IF @@ROWCOUNT > 0
            PRINT N'Rol director marcado como sensible (requiereAltaManual = 1), porque la columna del 23 ya existe.';
    END

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'15 NO se aplicó completo (marca de director sensible): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'15 aplicado: rol interno director.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (3 filas; director activo; el CHECK con los tres códigos):

     SELECT codigo, descripcion, idRolExterno, activo FROM dbo_cotifly.Roles ORDER BY id;
     SELECT definition FROM sys.check_constraints WHERE name = N'ck_roles_codigo';

   Reversa (solo si nadie tiene asignado el rol director en UsuariosRoles; con asignaciones, primero desactivarlas):
     DELETE FROM dbo_cotifly.Roles WHERE codigo = N'director';
     ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT ck_roles_codigo;
     ALTER TABLE dbo_cotifly.Roles WITH CHECK ADD CONSTRAINT ck_roles_codigo CHECK (codigo IN ('comercial', 'gerente_comercial'));
   ============================================================================ */
