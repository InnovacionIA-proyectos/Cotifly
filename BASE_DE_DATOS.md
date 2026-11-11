# Base de datos — Cotifly

> **Estado:** vigente desde HU-021 · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-11-10 (HU-024.2, #203: esquema consolidado en `db/esquema/`, ajustes `02` a `22`, seeders `01` a `05`, ejecutador en `db/ejecutores/`)

Punto de entrada a la base de datos de Cotifly: motor, esquema, scripts y dónde está cada detalle.

## 1. Resumen

- **Motor:** SQL Server 2019. **Schema:** `dbo_cotifly`, dentro de la base compartida `dbTarifario_Nexo_Pruebas`
  (`tbsdatacenter`).
- **Acceso desde el código:** EF Core *Database First* en el backend .NET
  (`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/`). No hay migraciones de EF: el esquema solo cambia con
  scripts SQL (ADR-008). Una base nueva se instala con `db/esquema/cotifly_esquema.sql` (el esquema completo, solo estructura);
  una base existente se pone al día con los ajustes idempotentes `db/scripts/02` a `22` (y, si todavía tiene el esquema anterior
  a HU-021, antes con las migraciones `db/migrations_mssql/0001` a `0010`). La configuración inicial la siembran `db/seeder/01`
  a `05`. Todo lo corre el ejecutador `db/ejecutores/ejecutar-bd.ps1` / `.sh`.
- **Qué guarda:** la trazabilidad de cada cotización (rutas, camiones, costos de Nexo, margen, precio, PDF, correo y
  resultado), la identidad interna y la auditoría. No es el ERP: Nexo, SAP TM y el cubicador son fuentes externas.
- **28 tablas y 1 procedimiento, sin vistas:** las 20 del diseño de HU-021, `CapturasIdempotentes` (`06`), `SapMargenCliente` y
  `SapMargenSyncLog` con el procedimiento `sp_sincronizar_margen_sap` (`17`), `TarifasSeguro` (`20`) y las 4 tablas de la
  política de seguros (`21`). Las 2 vistas del borrador de métricas se retiraron en HU-014.3, #152, con `db/scripts/08`.

| Documento | Qué tiene |
|---|---|
| [`docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`](docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md) | Esquema vigente (Parte A): diagramas ER, columnas, tipos, restricciones, índices y scripts |
| [`docs/database/DICCIONARIO_BASE_DE_DATOS.md`](docs/database/DICCIONARIO_BASE_DE_DATOS.md) | Qué significa cada columna, quién la escribe y quién la lee |
| [`db/README.md`](db/README.md) | Qué va en `esquema/`, `scripts/`, `seeder/` y `ejecutores/`; cómo instalar una base nueva, poner al día una existente y verificar; estado de cada ajuste |
| [ADR-019](backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md) | Decisión de las tres capas solicitud → ruta → camión y las decisiones 1 a 12 de HU-021 |
| [`DEPLOY.md`](DEPLOY.md) | Precondición de esquema para desplegar y errores de `/health/ready` |

## 2. Las tres capas: solicitud → ruta → camión

Desde el `0009` (HU-021) una cotización es una **solicitud** (`CotizacionesSolicitud`) con una o varias **rutas**
(`CotizacionLineasSolicitud`). Cada ruta sale en uno o varios **camiones** (`CotizacionVehiculosLineaSolicitud`, uno por
fila). Las **mercancías** (`CotizacionMercancias`) y los **servicios conexos** (`CotizacionServiciosConexos`) son de la ruta.
Nexo costea cada camión; la ruta suma sus camiones y sus conexos, aplica su margen y saca su precio; la cabecera suma sus
rutas. Cubica el sistema donde nace la solicitud: si nace en Cotifly, Cotifly llama al cubicador.

```mermaid
erDiagram
    CotizacionesSolicitud ||--|{ CotizacionLineasSolicitud : "rutas"
    CotizacionLineasSolicitud ||--|{ CotizacionVehiculosLineaSolicitud : "camiones"
    CotizacionLineasSolicitud ||--o{ CotizacionMercancias : "mercancias"
    CotizacionLineasSolicitud ||--o{ CotizacionServiciosConexos : "conexos"
    MargenesPolitica |o--o{ CotizacionLineasSolicitud : "linea de negocio"
    CotizacionLineasSolicitud |o--o{ MargenAjustes : "ajustes de margen"
    CotizacionesSolicitud ||--o{ Correos : "correos"
    CotizacionesSolicitud ||--o{ CotizacionPdfsHistorial : "PDF anteriores"
    CotizacionesSolicitud ||--o| CotizacionArchivosLicitacion : "Excel del pliego"
    UsuariosWeb |o--o{ CotizacionesSolicitud : "comercial"

    CotizacionesSolicitud {
        uniqueidentifier id PK
        nvarchar50 folio UK
        nvarcharmax moneda
        decimal18_4 trmAplicada
        decimal18_2 precioFinal "suma de las rutas"
        nvarcharmax estadoCosteo "resumen de las rutas"
        nvarchar64 solicitudNexoId
        nvarchar64 cotizacionNexoId
        int versionNegocio
    }
    CotizacionLineasSolicitud {
        uniqueidentifier id PK
        uniqueidentifier cotizacionSolicitudId FK
        int orden
        int lineaNegocioId FK
        int origenMunicipioId
        nvarchar150 origenNombre
        int destinoMunicipioId
        nvarchar150 destinoNombre
        decimal18_2 valorLevanteContenedor
        decimal18_2 costoFinal
        decimal9_4 margenAplicado
        decimal18_2 precioFinalLinea
        nvarchar30 estadoCosteo "resumen de los camiones"
    }
    CotizacionVehiculosLineaSolicitud {
        uniqueidentifier id PK
        uniqueidentifier lineaSolicitudId FK
        nvarchar100 tipoVehiculo
        nvarchar100 carroceria
        decimal12_2 pesoKg
        decimal18_2 costoFlete
        decimal18_2 costoRetornoContenedor
        decimal18_2 costoLevanteContenedor
        nvarchar64 lineaSolicitudNexoId
        nvarchar30 estadoCosteo
    }
    CotizacionMercancias {
        uniqueidentifier id PK
        uniqueidentifier lineaSolicitudId FK
        nvarchar64 codigoMercancia
        nvarchar200 nombreMercancia
        int cantidad
    }
    CotizacionServiciosConexos {
        uniqueidentifier id PK
        uniqueidentifier lineaSolicitudId FK
        nvarchar64 idServicioConexo
        nvarchar200 nombreServicioConexo
        decimal18_2 precio "costo unitario de Nexo"
        decimal10_2 cantidad
    }
```

El diagrama completo, con todas las columnas y el resto de tablas, está en
[`ESQUEMA_OBJETIVO_SQLSERVER.md`](docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md) §A.2 y §A.3.

## 3. Tablas

| Grupo | Tablas |
|---|---|
| Cotizaciones | `CotizacionesSolicitud` (cabecera, ex `Cotizaciones`), `CotizacionLineasSolicitud` (ruta), `CotizacionVehiculosLineaSolicitud` (camión), `CotizacionMercancias` (ex `CotizacionPosiciones`), `CotizacionServiciosConexos`, `CotizacionArchivosLicitacion`, `CotizacionPdfsHistorial` |
| Márgenes y correo | `MargenAjustes` (con la ruta ajustada), `Correos`, `CorreoDestinatarios` |
| Configuración | `MargenesPolitica` (líneas de negocio), `SapMapeoTrqType`, `TarifasSeguro` (tasa del seguro), `PoliticaSeguroTramos`, `PoliticaSeguroMedidas`, `PoliticaSeguroTramoMedidas`, `PoliticaSeguroMedidaConexos` (política de seguros) |
| Identidad | `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` |
| Asistente y auditoría | `Sesiones`, `EventosConversacion`, `EventosHerramienta` |
| Idempotencia | `CapturasIdempotentes` (claves `Idempotency-Key` persistentes, `db/scripts/06`, #191) |
| Margen de SAP TM | `SapMargenCliente`, `SapMargenSyncLog` y el procedimiento `sp_sincronizar_margen_sap` (`db/scripts/17`) |

## 4. Scripts

| Script | Qué hace |
|---|---|
| `db/esquema/cotifly_esquema.sql` | **Instalación de una base nueva:** el esquema completo en un paso (solo estructura). Equivale a `0001` a `0010` más los ajustes `02` a `22` |
| `db/scripts/02` a `22` | **Ajustes de las bases existentes**, idempotentes; sobre una base instalada con `db/esquema/` no cambian nada. Todos aplicados en la base compartida. Lista, estado y si `/health/ready` los exige: [`db/README.md`](db/README.md#ajustes-al-esquema-scripts02-a-22) |
| `db/seeder/01` a `05` | **Configuración inicial:** líneas de negocio, mapeo SAP TM, roles, tasa del seguro y política de seguros. Idempotentes |
| `db/ejecutores/ejecutar-bd.ps1` / `.sh` | **Ejecutador:** base vacía → esquema; después, en toda base, los ajustes y los seeders |
| `db/migrations_mssql/0001` a `0010` | Migraciones históricas: llevan una base del esquema anterior al de HU-021 (`0009`: cabecera → rutas → camiones, con respaldo `Respaldo0009_*`; `0010`: nombres de municipio, `solicitudNexoId`, nombre del conexo y el CHECK de `estadoEnvio`). Las usan las pruebas de integración y `levantar-local.*` |
| `db/scripts/operacion/` | Scripts de datos de una sola vez (identidad compartida con Nexo, rol comercial, mapeo de prueba del rol director, cambio de la tasa del seguro y de la política de seguros). Los corre el DBA con aprobación; el ejecutador no los corre |

Detalle de cada script en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md) §A.5 y en la cabecera de
cada archivo.

**Precondición para desplegar el backend:** el esquema al día (una base nueva instalada con el ejecutador, o una existente con
`0001` a `0010` y los ajustes que el backend mapea). Si falta algo, `/health/ready` responde `503` y dice qué falta (`falta 0002`,
`falta 0009`, `falta 0010`, `falta NN` con el número del ajuste, o `falta Tabla.columna`; ver [`DEPLOY.md`](DEPLOY.md) §4.5).
Sobre otra base existente los ajustes van en orden numérico, con dos salvedades: el `08` después de la imagen que retiró el
borrador de métricas y el `15` con o después de la imagen de HU-037.

## 5. Cómo acceder

- **Local:** el `docker-compose.yml` de la raíz levanta SQL Server en el puerto **1434** del host y el servicio `db-init`
  instala el esquema y los seeders en la base `bd_cotifly` (ver [`db/README.md`](db/README.md)). El backend .NET tiene sus
  propios scripts de arranque local: [`backend-dotnet/docs/06-ejecucion-local.md`](backend-dotnet/docs/06-ejecucion-local.md).
- **Base compartida:** `tbsdatacenter`, base `dbTarifario_Nexo_Pruebas`, schema `dbo_cotifly`. Los scripts los aplica el
  DBA, con aprobación; nunca se corren a mano desde el equipo de desarrollo.

## 6. Respaldo

La política de respaldo de `dbo_cotifly`, la exportación lógica (`exportar-dbo-cotifly.sh`) y el restore en una instancia
de pruebas están en [`DEPLOY.md`](DEPLOY.md) §6.

## 7. Pendientes

| Pendiente | Quién lo resuelve |
|---|---|
| Confirmar en cada ambiente nuevo (fuera de la base compartida) que los ajustes que el backend mapea están aplicados antes de la imagen (consulta en [`DEPLOY.md`](DEPLOY.md) §4.5) | DBA |
| `TrqType` de SAP TM para `DevolucionContenedor` (pendiente externo en `db/seeder/02_sap_mapeo_trqtype.sql`; no se inventa) | Negocio o SAP |
| Fijar `Roles.idRolExterno` de los roles de ClientePro en cada ambiente (`db/seeder/03_roles.sql`) | DBA |
| Borrar el respaldo `dbo_cotifly_respaldo_20261008` de la base compartida cuando ya no se necesite | DBA |
| Borrar las tablas `Respaldo0009_*` cuando el backend esté validado, si en algún ambiente se aplicó el `0009` sobre datos existentes (solo existen en ese caso) | DBA |
