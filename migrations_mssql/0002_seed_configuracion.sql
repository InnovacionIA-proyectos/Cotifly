/* ============================================================================
   Cotifly — Seed de configuración: MargenesPolitica + SapMapeoTrqType (HU-002.4)
   ============================================================================
   Hueco encontrado en el checklist de HU-002.4: "ejecutar el esquema objetivo
   (tablas, FKs, constraints, vistas)" no incluía datos de configuración. Sin
   esto, la app fallaría con "línea de negocio desconocida" en la primera
   cotización — no es dato histórico transaccional, es configuración que la
   app necesita desde el día uno (equivalente a `migrations/versions/
   0002_seed_config.py` del lado Postgres).

   Valores verificados en vivo contra el Postgres de dev (2026-09-22, docker
   compose, botcotizador) — no se copiaron de la documentación a ciegas:
   `BASE_DE_DATOS.md` afirma que ITR está al 50% ("una al 50%, ITR,
   intencional") pero el dato real en BD (y en la migración
   `0002_seed_config.py` que lo sembró) es 0.15 igual que las otras 7 líneas —
   ese dato del documento está desactualizado/incorrecto, no se usó.

   Corrección de nombres: el pedido original traía `tblMargenesPolitica`/
   `tblSapMapeoTrqType` — esos nombres ya no existen, se les quitó el prefijo
   `tbl` (ver docs/ESQUEMA_OBJETIVO_SQLSERVER.md §9). Este script usa los
   nombres reales: `MargenesPolitica`, `SapMapeoTrqType`.

   Idempotencia: cada INSERT está guardado con un chequeo de "¿ya tiene filas
   esta tabla?" — si el script se corre dos veces, la segunda vez no duplica
   nada (igual de criterio que 0001, pero acá por tabla completa en vez de por
   fila, ya que es una siembra de una sola vez, no algo que se vaya actualizando
   fila por fila).

   Precondición: correr después de `0001_esquema_objetivo.sql` (las tablas
   deben existir).

   Ejecutar igual que 0001:
     sqlcmd -S tbsdatacenter -d dbTarifario_Nexo_Pruebas -E -C -i migrations_mssql\0002_seed_configuracion.sql
   ============================================================================ */

SET NOCOUNT ON;
GO

USE dbTarifario_Nexo_Pruebas;
GO

-- =============================================================================
-- 1. dbo_cotifly.MargenesPolitica — 8 líneas de negocio, valores reales de Postgres
-- =============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.MargenesPolitica)
BEGIN
    INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive) VALUES
    ('Nacional', 0.15, 'margen', 1),
    ('Distribucion', 0.15, 'margen', 1),
    ('Importacion', 0.15, 'margen', 1),
    ('Exportacion', 0.15, 'margen', 1),
    ('Internacional', 0.15, 'margen', 1),
    ('Almacenamiento', 0.15, 'margen', 1),
    ('Almacenamiento3PL', 0.15, 'margen', 1),
    ('ITR', 0.15, 'margen', 1); -- verificado en vivo: 0.15, no 0.50 (ver nota arriba)
END
GO

-- =============================================================================
-- 2. dbo_cotifly.SapMapeoTrqType — mapeo TrqType SAP -> línea de negocio
-- =============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo_cotifly.SapMapeoTrqType)
BEGIN
    INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, activo) VALUES
    ('OPTN', '', 'Nacional', 1),
    ('OPDS', '', 'Distribucion', 1),
    ('OPIM', '', 'Importacion', 1),
    ('OPEX', '', 'Exportacion', 1),
    ('OPTI', '', 'Internacional', 1),
    ('OHUB', '', 'Almacenamiento', 1),
    ('OH3P', '', 'Almacenamiento3PL', 1),
    ('OPSC', '', 'ITR', 1);
END
GO

/* ============================================================================
   Verificación sugerida tras ejecutar:

     SELECT lineaNegocio, margen, metodoCalculo, isActive
     FROM dbo_cotifly.MargenesPolitica ORDER BY lineaNegocio;
     -- Debe devolver 8 filas, todas margen = 0.15.

     SELECT trqType, salesOrg, lineaNegocio, activo
     FROM dbo_cotifly.SapMapeoTrqType ORDER BY trqType;
     -- Debe devolver 8 filas.
   ============================================================================ */
