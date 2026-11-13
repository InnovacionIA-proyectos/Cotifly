/* ============================================================================
   Cotifly — 23: rol sensible con alta manual (requiereAltaManual)
   ============================================================================
   HU-040.3 (#258, resuelve el punto 2 de #261; ADR-009 OPCIÓN B+). El login pasa a RECONCILIAR los roles internos con los roles
   externos de ClientePro en vez de rechazar ante un desajuste: auto-otorga los roles ORDINARIOS que falten y auto-revoca TODOS los
   que perdieron su respaldo (fail-safe). Los roles SENSIBLES NO se auto-otorgan: siguen con alta manual y auditoría. Este flag marca
   cuáles son sensibles, como DATO del catálogo (no una lista fija en el código).

   Qué hace (NO destructivo e idempotente; no borra ni cambia filas de otros objetos):
     1. Roles: agrega la columna requiereAltaManual BIT NOT NULL DEFAULT 0 (df_roles_requiereAltaManual). Solo si falta.
     2. Marca requiereAltaManual = 1 en el rol `director` (HU-037, #209): es el rol sensible de hoy. Si mañana se agrega un rol de
        administración, el DBA lo marca igual (UPDATE ... SET requiereAltaManual = 1 WHERE codigo = '<admin>'), sin tocar el código.
        Idempotente: vuelve a correrlo y deja el mismo resultado.

   Dónde corre: después del 15 (crea la fila director) y del 20 (o del 01). Lo toman solos el ejecutador (ejecutar-bd.ps1 / .sh) y
   levantar-local, en orden de nombre. El backend de HU-040.3 SÍ mapea requiereAltaManual: sin el 23, /health/ready responde 503
   "esquema incompleto: falta 23", así que se aplica ANTES de desplegar la imagen.

   ¿Es seguro con la imagen desplegada hoy (anterior a HU-040.3)? Sí: la columna nace en 0 (ningún rol sensible) y esa imagen no la
   lee; sus INSERT nombran sus columnas y la dejan en el DEFAULT. Mientras no se despliegue la imagen de HU-040.3 el login sigue
   validando/rechazando como hoy (no reconcilia).

   Respaldo: no hace falta (no cambia filas existentes salvo marcar director). Permisos: ninguno nuevo.

   Cómo ejecutarlo (-b detiene en el primer error; -I: QUOTED_IDENTIFIER ON por los índices filtrados de Roles).
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 23_rol_requiere_alta_manual.sql
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
-- 1. Columna requiereAltaManual (una sola transacción)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    IF COL_LENGTH(N'dbo_cotifly.Roles', N'requiereAltaManual') IS NULL
    BEGIN
        ALTER TABLE dbo_cotifly.Roles
            ADD requiereAltaManual BIT NOT NULL CONSTRAINT df_roles_requiereAltaManual DEFAULT (0);
        PRINT N'Roles.requiereAltaManual agregada (BIT NOT NULL DEFAULT 0).';
    END
    ELSE
        PRINT N'Roles.requiereAltaManual ya existe: no se cambia.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'23 NO se aplicó (columna requiereAltaManual): se deshizo todo. Error:';
    THROW;
END CATCH;
GO

-- =============================================================================
-- 2. Marcar el rol sensible `director` (otro lote: la columna recién agregada no se ve en el lote del ADD)
-- =============================================================================
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;

    UPDATE dbo_cotifly.Roles
    SET requiereAltaManual = 1
    WHERE codigo = N'director' AND requiereAltaManual = 0;
    IF @@ROWCOUNT > 0
        PRINT N'Rol director marcado como sensible (requiereAltaManual = 1).';
    ELSE
        PRINT N'El rol director ya estaba marcado (o no existe todavía): no se cambia.';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT N'23 NO se aplicó completo (marca de director): se deshizo este paso. Error:';
    THROW;
END CATCH;
GO

PRINT N'23 aplicado: Roles.requiereAltaManual y director marcado como sensible.';
GO

/* ============================================================================
   Verificación sugerida tras ejecutar (director con requiereAltaManual = 1; los demás en 0):

     SELECT codigo, idRolExterno, activo, requiereAltaManual FROM dbo_cotifly.Roles ORDER BY id;

   Reversa (solo con una imagen anterior a HU-040.3; la de HU-040.3 queda en 503 "falta 23" sin la columna):
     ALTER TABLE dbo_cotifly.Roles DROP CONSTRAINT df_roles_requiereAltaManual;
     ALTER TABLE dbo_cotifly.Roles DROP COLUMN requiereAltaManual;
   ============================================================================ */
