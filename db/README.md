# Base de datos de Cotifly

SQL Server 2019, schema `dbo_cotifly`. Esta carpeta tiene todo lo necesario para **crear la base de datos desde cero** y
dejarla con la configuración inicial.

> **Estado (2026-10-13, HU-021.14):** probado. La instalación desde cero (`scripts/01_crear_esquema.sql` y `seeder/`) se
> corrió el 2026-10-08 sobre `dbTarifario_Nexo_Pruebas` (después de borrar el schema `dbo_cotifly` anterior): creó las
> 20 tablas y las 2 vistas y sembró 6 líneas de negocio, 5 mapeos y 2 roles, sin errores. Las pruebas de integración
> (Testcontainers, `backend-dotnet/tests/Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs`) lo verifican en
> cada corrida: la instalación desde cero y las migraciones `0001` a `0010` dejan el mismo esquema, el `0010` es idempotente
> y el `0009` migra datos del esquema anterior sin perder ajustes, correos ni auditoría. El backend corre sobre este esquema
> (ADR-019). Lo único pendiente es de aprobación, no de prueba: el `0010_nombres_municipio.sql` es un **borrador pendiente
> de revisión de scalderon-bs y del DBA**.
>
> **`scripts/02_alter_cubicador.sql`** (HU-023.3, #158) agrega las columnas para guardar la respuesta del cubicador. Va
> después del 01 (o del `0010` en una base migrada) y el ejecutador lo corre solo. Las pruebas de integración lo aplican después
> de `0001` a `0010` y después del 01, y exigen el mismo esquema (HU-023.3).

## Estructura

```
db/
├── README.md              este archivo
├── ejecutar-bd.ps1        ejecutador para Windows (PowerShell)
├── ejecutar-bd.sh         ejecutador para Linux / contenedores (bash)
├── scripts/
│   ├── 01_crear_esquema.sql   schema, tablas, restricciones, índices y vistas, en su forma final
│   └── 02_alter_cubicador.sql columnas para la respuesta del cubicador (ALTER; se puede repetir)
├── seeder/
│   ├── 01_lineas_negocio.sql      líneas de negocio y su margen de política
│   ├── 02_sap_mapeo_trqtype.sql   mapeo del TrqType de SAP TM a la línea de negocio
│   └── 03_roles.sql               roles internos (comercial, gerente_comercial)
├── migrations_mssql/      migraciones 0001 a 0010 (SQL Server): llevan una base que ya existe de un estado al siguiente
│   └── rollback/          reversas
```

`scripts/` y `seeder/` son para **instalar desde cero**; `migrations_mssql/` es para **bases que ya existen**. El backend,
las pruebas y el CI leen `migrations_mssql/`, y por eso esa carpeta no se movió.

## Instalar desde cero

Necesitas `sqlcmd` y permiso para crear schemas, tablas y vistas en la base.

**Windows (PowerShell):**

```powershell
cd db
# Crea la base si no existe, el esquema (01 y 02) y los seeders:
.\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -CrearBase

# Con usuario de SQL Server en vez de autenticación de Windows:
.\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -Usuario sa -CrearBase
```

**Linux / contenedores:**

```bash
cd db
./ejecutar-bd.sh -s localhost,1433 -d dbCotifly -u sa -p '<clave>' -c
```

El ejecutador **no tiene servidor ni base por defecto, a propósito**: así no se corre por accidente contra el servidor
compartido (`tbsdatacenter`). Antes de ejecutar muestra a dónde va y pide escribir `SI` (con `-Si` o `-y` no pregunta).

| Opción (PowerShell / bash) | Qué hace |
|---|---|
| `-CrearBase` / `-c` | Crea la base de datos si no existe, con la collation `Latin1_General_100_CI_AS_SC_UTF8` |
| `-SoloEsquema` / `-e` | Corre solo el esquema: `scripts/01_crear_esquema.sql` y los ajustes `scripts/02_*` en adelante |
| `-SoloSeeder` / `-r` | Corre solo los seeders; se puede repetir sin problema |
| `-Auto` / `-a` | Si `dbo_cotifly` ya tiene tablas, se salta la creación del esquema (01) y corre los ajustes (02 en adelante) y los seeders (sirve para correrlo en cada arranque) |
| `-Usuario` y `-Clave` / `-u` y `-p` | Autenticación de SQL Server (sin esto usa la de Windows) |

El orden es: crear la base (opcional), `scripts/01_crear_esquema.sql`, los ajustes `scripts/02_*.sql` en adelante (en
orden de nombre) y los seeders. Se detiene en el primer error y no ejecuta los pasos siguientes.

**Los ajustes (`02` en adelante) se pueden repetir**: cada cambio revisa si ya está. Por eso corren también en una base que
ya tiene el esquema; así una base instalada antes queda igual que una nueva.

Con `-Auto` / `-a` el `02` corre en **cada** arranque (la segunda vez no hace nada). En una base anterior al `0009` (sin
`CotizacionMercancias`) el `02` se detiene con un error en su precondición y el ejecutador también: primero hay que migrar con
`migrations_mssql/0009` y `0010`. `scripts/pendientes/` (propuestas pendientes de revisión, como el `03`) **no** la corre el
ejecutador. Tampoco `scripts/operacion/`: scripts de datos de una sola vez que corre el DBA con aprobación, como
`revincular_identidad_dbSolucionesTI_Prueba.sql` (#172, ver [`DEPLOY.md`](../DEPLOY.md#identidad-compartida-con-nexo-172)).

### Con Docker

El `docker-compose.yml` de la raíz trae un servicio `db-init` que levanta después de SQL Server y corre
`ejecutar-bd.sh -c -a -y` contra la base `bd_cotifly`: la crea, crea el esquema y siembra. Es seguro en cada
`docker compose up`: si el esquema ya existe, no lo vuelve a crear; solo corre los ajustes y repasa los seeders.

```bash
docker compose up sqlserver db-init     # SQL Server local en el puerto 1434 del host
docker compose logs db-init             # ver qué hizo
```

Solo toca ese SQL Server local de desarrollo. El `deploy/docker-compose.swarm.yml` no se modificó. Ojo: el bot viejo en
Python (`mcp` y `orquestador`) usa modelos de Postgres y no puede usar este esquema.

### Qué hay que cambiar para ejecutarlo

**Nada dentro de los archivos.** El ejecutador busca `scripts/` y `seeder/` relativo a donde él está (se puede llamar desde
cualquier carpeta), y los `.sql` de `scripts/` y `seeder/` no traen ningún servidor ni nombre de base escritos: no tienen `USE`.
La base la eliges al ejecutar, con `-Servidor` y `-Base` (`-s` y `-d` en bash). Sin `-CrearBase`, la base tiene que existir ya.

La excepción son las **migraciones** de `migrations_mssql/` (0001 a 0010): esas sí traen `USE dbTarifario_Nexo_Pruebas`. Si algún
día se corren contra otra base, hay que cambiar esa línea. No aplica a la instalación desde cero.

### En SSMS o Azure Data Studio (sin el ejecutador)

1. Selecciona la base de datos destino arriba a la izquierda: los scripts corren sobre la que tengas seleccionada.
2. Abre y ejecuta, en este orden: `scripts/01_crear_esquema.sql`, `scripts/02_alter_cubicador.sql`,
   `seeder/01_lineas_negocio.sql`, `seeder/02_sap_mapeo_trqtype.sql` y `seeder/03_roles.sql`.
3. Si `dbo_cotifly` ya tiene tablas, el `01` avisa y no crea nada. Si falla otro paso, mira el error antes de seguir con
   los siguientes.

### Sin el ejecutador, con sqlcmd

```
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/01_crear_esquema.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/02_alter_cubicador.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/01_lineas_negocio.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/02_sap_mapeo_trqtype.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/03_roles.sql
```

`-E` autenticación de Windows, `-C` confiar en el certificado, `-I` activa `QUOTED_IDENTIFIER` (lo exigen los índices
filtrados), `-b` detiene el script en el primer error, `-f 65001` lee los archivos como UTF-8. Sin `-b`, SSMS o sqlcmd
siguen ejecutando después de un error.

## Qué crea

**Esquema** (`scripts/01_crear_esquema.sql`): 20 tablas y 2 vistas en el schema `dbo_cotifly`.

| Grupo | Tablas |
|---|---|
| Configuración | `MargenesPolitica`, `SapMapeoTrqType` |
| Identidad | `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` |
| Conversación del agente | `Sesiones`, `EventosConversacion`, `EventosHerramienta` |
| Cotizaciones | `CotizacionesSolicitud` (cabecera), `CotizacionLineasSolicitud` (ruta), `CotizacionVehiculosLineaSolicitud` (camiones), `CotizacionMercancias`, `CotizacionServiciosConexos`, `CotizacionArchivosLicitacion`, `CotizacionPdfsHistorial` |
| Márgenes y correo | `MargenAjustes`, `Correos`, `CorreoDestinatarios` |
| Vistas | `vAjustesMargenPorComercial`, `vActividadPorComercial` |

El esquema vigente con su diagrama ER está en [`docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`](../docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md)
y el significado de cada columna en [`docs/database/DICCIONARIO_BASE_DE_DATOS.md`](../docs/database/DICCIONARIO_BASE_DE_DATOS.md).
El diseño original de las tablas de cotizaciones está en `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`.

**Seeders** (`seeder/`): la configuración que la aplicación necesita desde el primer día.

| Archivo | Siembra |
|---|---|
| `01_lineas_negocio.sql` | 6 líneas de negocio con margen 15 %: Nacional, Distribucion, Importacion, Exportacion, Internacional, DevolucionContenedor |
| `02_sap_mapeo_trqtype.sql` | 5 mapeos de TrqType (OPTN, OPDS, OPIM, OPEX, OPTI). **Falta el de `DevolucionContenedor`**: necesita el código real de SAP y no se inventa |
| `03_roles.sql` | Los roles `comercial` y `gerente_comercial`, sin rol externo (`idRolExterno` NULL) |

Todos son idempotentes: insertan solo lo que falta y no pisan lo que ya se haya ajustado.

**Después de sembrar**, el DBA tiene que fijar los roles externos de ClientePro; mientras sean NULL nadie puede iniciar
sesión con ese rol (falla cerrado). Las dos sentencias están en `seeder/03_roles.sql`.

## Qué NO hace

- **No sirve para una base que ya tiene el esquema.** Si `dbo_cotifly` ya tiene tablas, `01_crear_esquema.sql` se detiene
  sin tocar nada: no suelta ni recrea nada. Para una base existente se usan las migraciones.
- **No crea datos de ejemplo ni de prueba.** Solo la configuración inicial.
- **No lleva datos históricos.** Nada se migra desde el Postgres viejo.

## Bases que ya existen (migraciones)

La base compartida (`tbsdatacenter`, `dbTarifario_Nexo_Pruebas`) se **reinstaló desde cero el 2026-10-08** con
`scripts/01_crear_esquema.sql` en la versión de ese día, que equivalía a `0001`–`0009`. Para una base que todavía tenga el
esquema anterior (`Cotizaciones`), el `0009_reestructuracion_cotizaciones.sql` la lleva al esquema de este directorio
conservando sus datos: hace un respaldo de las tablas que toca (`Respaldo0009_*`) y corre en una sola transacción. Está
probado con Testcontainers sobre datos del esquema anterior; aplicarlo en un ambiente real sigue necesitando el visto bueno
del DBA.

El `0010_nombres_municipio.sql` (HU-021, ADR-019) se aplica **después del `0009`**: agrega los nombres de municipio de la
ruta, `CotizacionesSolicitud.solicitudNexoId`, `CotizacionServiciosConexos.nombreServicioConexo` y el `CHECK` de
`estadoEnvio` si falta. Es idempotente y no destructivo, y llena los nombres desde `Respaldo0009_*` si existen.
`scripts/01_crear_esquema.sql` ya trae esas columnas.

## Ajustes al esquema (`scripts/02_*`)

Cambios pequeños al esquema que todavía no se consolidan en el `01`. Van como `ALTER TABLE`, en un script numerado de
`scripts/` que **se puede repetir** (cada cambio revisa si ya está) y corre en **una sola transacción**.

| Script | HU | Qué cambia |
|---|---|---|
| `02_alter_cubicador.sql` | HU-023.3 (#158) | `CotizacionMercancias`: `idTipoMercancia` (uuid) se reemplaza por `codigoMercancia`. Camión (`CotizacionVehiculosLineaSolicitud`): `volumenTotalM3`, `porcentajeLlenadoVolumen` y `porcentajeLlenadoPeso` (0 a 100). Ruta (`CotizacionLineasSolicitud`): `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion` y `condicionada` |

El `02` se detiene sin tocar nada si `idTipoMercancia` todavía tiene valores (un uuid no se puede convertir a código).
Cuando el diseño se estabilice, estos cambios se pasan al `01` y el script se retira.

## Mantener esto en sincronía

`scripts/01_crear_esquema.sql` es el resultado de aplicar todas las migraciones. **Cada migración nueva (`000N`) tiene que
reflejarse también ahí**, para que una instalación desde cero y una base migrada queden idénticas. Por eso las restricciones
de las tablas renombradas conservan los nombres que dejan las migraciones (`pk_cotizaciones`, `pk_cotizacionPosiciones`…).

## Verificar

Contra una base vacía **local o de pruebas, nunca la compartida**:

```powershell
.\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly_prueba -CrearBase
```

El resumen final debe mostrar 20 tablas, 6 líneas de negocio, 5 mapeos SAP TM y 2 roles. Al final del
`02_alter_cubicador.sql` hay una consulta para comprobar sus 8 columnas. Para comprobar que quedó igual que
una base migrada, aplicar en otra base vacía `db/migrations_mssql/0001` a `0010` más el `02` y comparar columnas (por ejemplo con
`INFORMATION_SCHEMA.COLUMNS`) entre las dos.
