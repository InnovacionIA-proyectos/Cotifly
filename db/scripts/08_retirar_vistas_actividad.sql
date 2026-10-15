/* ============================================================================
   Cotifly — 08: retiro de las vistas del borrador de métricas
   ============================================================================
   HU-014.3 (#152). Las vistas `vActividadPorComercial` y `vAjustesMargenPorComercial` solo
   las leían `GET /web/metricas/actividad` y `GET /web/metricas/ajustes-margen`, el borrador
   del tablero (obsoleto desde HU-010.1). Esos dos endpoints se retiraron del backend y del
   contrato (specs/openapi.yaml); el tablero vigente (`/web/metricas/comercial` y
   `/web/metricas/regional`, HU-010) lee las tablas con SQL propio y no usa vistas.

   Qué hace: DROP VIEW de las dos vistas de `dbo_cotifly`, cada una solo si existe. No toca
   tablas ni datos (las vistas no guardan nada: todo sigue en MargenAjustes y
   CotizacionesSolicitud).

   Dónde corre: después del 07 (el ejecutador corre los .sql de db/scripts en orden de nombre).
   En una base instalada con el 01 actual no hace nada: el 01 ya no crea las vistas. En una
   base que viene de las migraciones 0001 a 0010 (o de un 01 anterior) las borra.

   Orden con el despliegue: aplicarlo DESPUÉS de desplegar la versión del backend sin los
   endpoints del borrador. La versión anterior del backend respondería 500 en esos dos
   endpoints si las vistas ya no existen (el resto de la API no las usa).

   Se puede correr varias veces: `IF OBJECT_ID(...) IS NOT NULL DROP VIEW`.

   Respaldo: la definición de las dos vistas está en db/migrations_mssql/0009 (sección de
   vistas). Para volver atrás se crean de nuevo con ese CREATE VIEW.

   Cómo ejecutarlo a mano:
     sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i 08_retirar_vistas_actividad.sql
   ============================================================================ */

SET NOCOUNT ON;
GO

IF OBJECT_ID(N'dbo_cotifly.vActividadPorComercial', N'V') IS NOT NULL
BEGIN
    DROP VIEW dbo_cotifly.vActividadPorComercial;
    PRINT N'08: vista dbo_cotifly.vActividadPorComercial retirada.';
END
ELSE
    PRINT N'08: dbo_cotifly.vActividadPorComercial no existe. No se cambió nada.';
GO

IF OBJECT_ID(N'dbo_cotifly.vAjustesMargenPorComercial', N'V') IS NOT NULL
BEGIN
    DROP VIEW dbo_cotifly.vAjustesMargenPorComercial;
    PRINT N'08: vista dbo_cotifly.vAjustesMargenPorComercial retirada.';
END
ELSE
    PRINT N'08: dbo_cotifly.vAjustesMargenPorComercial no existe. No se cambió nada.';
GO
