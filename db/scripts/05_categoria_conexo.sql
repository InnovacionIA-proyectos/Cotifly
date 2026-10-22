/* ============================================================================
   Cotifly — 05 (#173): categoría del catálogo de Nexo en el conexo de la ruta
   ============================================================================
   Aprobado y aplicado en la base compartida el 2026-10-16 (HU-024.1, organización de scripts). Movido de
   db/scripts/pendientes/ a db/scripts/. El backend todavía NO mapea esta columna (mapearla sin la columna
   rompería cada lectura de la cotización).

   Por qué: el costeo mostraba todos los conexos con categoría `logistico` (constante del backend) aunque en el
   catálogo de Nexo sean `adicional` (E2E real del 2026-10-14). Desde #173 el backend lee la categoría del
   catálogo de Nexo al armar el costeo (índice en memoria de 30 min, clsCategoriasConexosService) y usa
   `logistico` solo si el catálogo no responde o no tiene el conexo. Con esta columna la categoría se guardaría
   al capturar (la captura ya lee el catálogo) y al importar los conexos de la cotización de Nexo, y el costeo
   dejaría de depender del catálogo.

   Qué hace: agrega CotizacionServiciosConexos.categoriaServicioConexo NVARCHAR(20) NULL (copia de la categoría
   del catálogo: logistico | adicional | ...). NULL en las filas existentes: el backend la sigue leyendo del
   catálogo para ellas. Sin CHECK de valores: el catálogo es de Nexo y puede crecer.

   Idempotente y no destructivo. Va después del 02 (y del 03 y 04 si se aplican; no depende de ellos).
   ============================================================================ */

SET NOCOUNT ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos', N'U') IS NULL
    THROW 50001, N'dbo_cotifly no tiene el esquema nuevo: corra primero 01_crear_esquema.sql y 02_alter_cubicador.sql.', 1;
GO

IF COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'categoriaServicioConexo') IS NULL
    ALTER TABLE dbo_cotifly.CotizacionServiciosConexos
        ADD categoriaServicioConexo NVARCHAR(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL;  -- categoría del catálogo de Nexo
GO

/* Verificación (solo lectura): la columna existe y es NULL en las filas anteriores. */
SELECT COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'categoriaServicioConexo') AS largoBytes,
       (SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos WHERE categoriaServicioConexo IS NULL) AS filasSinCategoria;
GO

/* Reversa (solo con aprobación, y antes de que el backend la mapee):
   ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP COLUMN categoriaServicioConexo;
*/
