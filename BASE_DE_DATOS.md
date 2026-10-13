# Base de datos — Cotifly

**Vigente desde HU-021. Actualizado en HU-021.14 (2026-10-13).**

Punto de entrada a la base de datos de Cotifly: motor, esquema, scripts y dónde está cada detalle.

## 1. Resumen

- **Motor:** SQL Server 2019. **Schema:** `dbo_cotifly`, dentro de la base compartida `dbTarifario_Nexo_Pruebas`
  (`tbsdatacenter`).
- **Acceso desde el código:** EF Core *Database First* en el backend .NET
  (`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/`). No hay migraciones de EF: el esquema solo cambia con
  scripts `db/migrations_mssql/000N_*.sql` (ADR-008).
- **Qué guarda:** la trazabilidad de cada cotización (rutas, camiones, costos de Nexo, margen, precio, PDF, correo y
  resultado), la identidad interna y la auditoría. No es el ERP: Nexo, SAP TM y el cubicador son fuentes externas.
- **20 tablas y 2 vistas.**

| Documento | Qué tiene |
|---|---|
| [`docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`](docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md) | Esquema vigente (Parte A): diagramas ER, columnas, tipos, restricciones, índices y scripts |
| [`docs/database/DICCIONARIO_BASE_DE_DATOS.md`](docs/database/DICCIONARIO_BASE_DE_DATOS.md) | Qué significa cada columna, quién la escribe y quién la lee |
| [`db/README.md`](db/README.md) | Cómo instalar desde cero, migrar una base existente y verificar |
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
        uniqueidentifier idTipoMercancia
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
| Configuración | `MargenesPolitica` (líneas de negocio), `SapMapeoTrqType` |
| Identidad | `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` |
| Asistente y auditoría | `Sesiones`, `EventosConversacion`, `EventosHerramienta` |
| Vistas | `vAjustesMargenPorComercial`, `vActividadPorComercial` |

## 4. Scripts

| Script | Qué hace |
|---|---|
| `db/scripts/01_crear_esquema.sql` + `db/seeder/*` | Instalación desde cero; equivale a `0001` a `0010` aplicados. Se corre con `db/ejecutar-bd.ps1` o `db/ejecutar-bd.sh` |
| `db/migrations_mssql/0001` a `0008` | Esquema inicial, identidad, costeo en Nexo, líneas de la solicitud, concurrencia y textos del PDF, campos de captura y horas por línea |
| `db/migrations_mssql/0009_reestructuracion_cotizaciones.sql` | HU-021: cabecera → rutas → camiones, mercancías y conexos en filas, con respaldo `Respaldo0009_*` |
| `db/migrations_mssql/0010_nombres_municipio.sql` | Nombres de municipio de la ruta, `solicitudNexoId`, nombre del conexo y el CHECK de `estadoEnvio`. **Borrador pendiente de revisión de scalderon-bs** |

Detalle de cada script en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md) §A.5.

**Precondición para desplegar el backend:** `0001` a `0010` aplicados (o la instalación desde cero). Si falta algo,
`/health/ready` responde `503` y dice qué falta (`falta 0009`, `falta 0010`, `falta Tabla.columna`; ver
[`DEPLOY.md`](DEPLOY.md) §4.5).

**Pendiente — HU-023 (cubicador):** `db/scripts/02_alter_cubicador.sql` agregará las columnas de resultado de la cubicación
y cambiará `idTipoMercancia` por `codigoMercancia`. Todavía no está en esta rama; cuando entre, pasa a ser parte de la
precondición.

## 5. Cómo acceder

- **Local:** el `docker-compose.yml` de la raíz levanta SQL Server en el puerto **1434** del host y el servicio `db-init`
  instala el esquema y los seeders en la base `bd_cotifly` (ver [`db/README.md`](db/README.md)). El backend .NET tiene sus
  propios scripts de arranque local: [`backend-dotnet/docs/06-ejecucion-local.md`](backend-dotnet/docs/06-ejecucion-local.md).
- **Base compartida:** `tbsdatacenter`, base `dbTarifario_Nexo_Pruebas`, schema `dbo_cotifly`. Los scripts los aplica el
  DBA, con aprobación; nunca se corren a mano desde el equipo de desarrollo.

## 6. Respaldo

La política de respaldo de `dbo_cotifly`, la exportación lógica (`exportar-dbo-cotifly.sh`) y el restore en una instancia
de pruebas están en [`DEPLOY.md`](DEPLOY.md) §6.
