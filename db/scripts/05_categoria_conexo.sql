/* ============================================================================
   Cotifly — 05 (#173): categoría del catálogo de Nexo en el conexo de la ruta
   ============================================================================
   El backend mapea la columna desde HU-014 (#188). Antes estaba en db/scripts/pendientes/ con "NO APLICAR". Desde #188 está en
   db/scripts/ y lo corren el ejecutador (ejecutar-bd.ps1 / .sh), db-init y levantar-local.*, por orden de nombre (después del 03 y
   del pendientes/04).

   *** OBLIGATORIO ANTES DE DESPLEGAR LA IMAGEN DE #188 ***
   Sin la columna, /health/ready responde 503 "esquema incompleto: falta 05" (cada lectura de la cotización la incluye).
   Verificación de solo lectura (debe dar un largo, no NULL):
     SELECT COL_LENGTH('dbo_cotifly.CotizacionServiciosConexos', 'categoriaServicioConexo') AS categoriaServicioConexo;

   Numeración (decisión de #188): conserva el número 05, por la misma razón que el 03. No depende del 03 ni del 04.

   Por qué: el costeo mostraba todos los conexos con categoría `logistico` (constante del backend) aunque en el catálogo de Nexo
   sean `adicional` (E2E real del 2026-10-14). #173 empezó a leer la categoría del catálogo de Nexo al armar el costeo (índice en
   memoria de 30 min, clsCategoriasConexosService). Desde #188 la categoría se guarda al capturar la ruta (la captura ya lee el
   catálogo), al importar los conexos de la cotización de Nexo y al copiarlos a una -vN. El costeo usa la guardada; solo las filas
   en NULL (anteriores a #188) siguen saliendo del catálogo, con `logistico` si el catálogo no responde o no tiene el conexo.

   Qué hace: agrega CotizacionServiciosConexos.categoriaServicioConexo NVARCHAR(20) NULL (copia de la categoría del catálogo:
   logistico | adicional | ...). Queda en NULL en las filas existentes. Sin CHECK de valores: el catálogo es de Nexo y puede crecer.

   Idempotente y no destructivo. Va después del 02.
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

/* Reversa (solo con aprobación): primero desplegar una imagen anterior a #188, que no mapea la columna; con la de #188 la API
   queda en 503 "falta 05".
   ALTER TABLE dbo_cotifly.CotizacionServiciosConexos DROP COLUMN categoriaServicioConexo;
*/
