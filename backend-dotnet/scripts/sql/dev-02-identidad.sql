/* ============================================================================
   Cotifly — SOLO DESARROLLO LOCAL (scripts/levantar-local.*, #14). Nunca en QA ni producción.
   dbsolucionesti FALSA con las tres tablas del login SSO (ADR-009) y sus tipos reales, igual que la de
   las pruebas (tests/Comun/clsBaseDatosPruebas). Datos ficticios:

     NOMBREUSUARIO  ID    Rol externo (IDROLES)       Resultado del login
     APEREZ         101   34 COMERCIAL                comercial
     LGOMEZ         102   35 GERENTE COMERCIAL        gerente_comercial
     KCANO          103   34 COMERCIAL                comercial (usuario de QA para la regresión manual)
     JCONSULTA      104   40 CONSULTA (sin mapeo)     401: sin rol interno

   Los IDROLES 34/35/40 son de desarrollo: los reales de ClientePro están pendientes (ADR-009).
   Idempotente. Se ejecuta en dbsolucionesti con sqlcmd -I.
   ============================================================================ */
SET NOCOUNT ON;
GO
IF OBJECT_ID(N'dbo.tblSAPUsuariosApp', N'U') IS NULL
    CREATE TABLE dbo.tblSAPUsuariosApp (
        ID                  NUMERIC(18,0) NOT NULL PRIMARY KEY,
        NOMBREUSUARIO       VARCHAR(50)   NULL,
        DESCRIPCIONUSUARIO  VARCHAR(250)  NULL,
        EMAIL               VARCHAR(100)  NULL,
        CLAVE               VARCHAR(200)  NULL
    );
GO
IF OBJECT_ID(N'dbo.tblSAPRoles', N'U') IS NULL
    CREATE TABLE dbo.tblSAPRoles (
        IDROLES             INT           NOT NULL PRIMARY KEY,
        DESCRIPCION         VARCHAR(50)   NULL
    );
GO
IF OBJECT_ID(N'dbo.tblSAPUsuarioRoles', N'U') IS NULL
    CREATE TABLE dbo.tblSAPUsuarioRoles (
        ID                  INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        IDUSUARIO           INT           NOT NULL,
        IDROLES             INT           NOT NULL
    );
GO
MERGE dbo.tblSAPRoles AS destino
USING (VALUES (34, 'COMERCIAL'), (35, 'GERENTE COMERCIAL'), (40, 'CONSULTA')) AS origen (IDROLES, DESCRIPCION)
    ON destino.IDROLES = origen.IDROLES
WHEN NOT MATCHED THEN INSERT (IDROLES, DESCRIPCION) VALUES (origen.IDROLES, origen.DESCRIPCION);
GO
MERGE dbo.tblSAPUsuariosApp AS destino
USING (VALUES
    (101, 'APEREZ',    'Ana Pérez (dev)',        'aperez@example.com'),
    (102, 'LGOMEZ',    'Luis Gómez (dev)',       'lgomez@example.com'),
    (103, 'KCANO',     'Kevin Cano (QA, dev)',   'kcano@example.com'),
    (104, 'JCONSULTA', 'Julia Consulta (dev)',   'jconsulta@example.com')
) AS origen (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL)
    ON destino.ID = origen.ID
WHEN NOT MATCHED THEN
    INSERT (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL, CLAVE)
    VALUES (origen.ID, origen.NOMBREUSUARIO, origen.DESCRIPCIONUSUARIO, origen.EMAIL, 'no-debe-leerse');
GO
INSERT INTO dbo.tblSAPUsuarioRoles (IDUSUARIO, IDROLES)
SELECT origen.IDUSUARIO, origen.IDROLES
FROM (VALUES (101, 34), (102, 35), (103, 34), (104, 40)) AS origen (IDUSUARIO, IDROLES)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.tblSAPUsuarioRoles r WHERE r.IDUSUARIO = origen.IDUSUARIO AND r.IDROLES = origen.IDROLES);
GO
