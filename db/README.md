# Base de datos de Cotifly

SQL Server 2019, schema `dbo_cotifly`. Esta carpeta tiene todo lo necesario para **crear la base de datos desde cero** y
dejarla con la configuración inicial.

> **Estado:** vigente · **Dueño:** Duban Guerra (backend); revisión de scripts: scalderon-bs y el DBA · **Actualizado:** 2026-10-15

## Estado

- **Instalación desde cero probada.** `scripts/01_crear_esquema.sql` y `seeder/` se corrieron el 2026-10-08 sobre
  `dbTarifario_Nexo_Pruebas`: 20 tablas, 2 vistas, 6 líneas de negocio, 5 mapeos y 2 roles, sin errores.
- **Verificación continua.** Las pruebas de integración (Testcontainers,
  `backend-dotnet/tests/Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs`) comprueban en cada corrida que la
  instalación desde cero y las migraciones `0001` a `0010` (más el `02`) dejan el mismo esquema, que el `0010` es
  idempotente y que el `0009` migra los datos del esquema anterior sin perder ajustes, correos ni auditoría.
- **Scripts posteriores al `0010`** y lo que falta aprobar (aprobación, no prueba):

| Script | HU | Estado |
|---|---|---|
| `migrations_mssql/0010_nombres_municipio.sql` | HU-021 | Borrador pendiente de revisión de scalderon-bs y del DBA |
| `scripts/02_alter_cubicador.sql` | HU-023.3 (#158) | En el ejecutador. Columnas para guardar la respuesta del cubicador; va después del `01` (o del `0010` en una base migrada) |
| `scripts/pendientes/03_orden_vehiculo_y_conexo_catalogo.sql` | HU-023.3 (#158) | Propuesta. El backend no mapea sus columnas |
| `scripts/pendientes/04_cubicacion_segundo_plano.sql` | HU-023.5 (#160) | Propuesta, pendiente de scalderon-bs y del DBA. **El backend sí mapea sus columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 04". Se aplica antes de desplegar la imagen. Solo en local, `levantar-local` también lo aplica |
| `scripts/07_motivo_busqueda_agotada.sql` | HU-023 (#176) | En el ejecutador. Reemplaza el CHECK `ck_cotizacionLineasSolicitud_motivoSinSeleccion` por uno con los 5 motivos vigentes del cubicador (agrega `BUSQUEDA_AGOTADA`). Idempotente; no obligatorio para arrancar (sin él, el backend guarda el motivo como advertencia y `/health/ready` solo lo avisa). El `pendientes/03` deja el mismo CHECK |
| `scripts/pendientes/05_categoria_conexo.sql` | #173 | Propuesta. El backend no mapea la columna |
| `scripts/pendientes/06_idempotencia_captura.sql` | #173 | Propuesta (deuda). El backend no mapea la tabla |

**Orden completo** de una base nueva: `01` (o `0001` a `0010`), `02`, `07` y, solo en local, `pendientes/04`.

## Estructura

```
db/
├── README.md              este archivo
├── ejecutar-bd.ps1        ejecutador para Windows (PowerShell)
├── ejecutar-bd.sh         ejecutador para Linux / contenedores (bash)
├── scripts/
│   ├── 01_crear_esquema.sql   schema, tablas, restricciones, índices y vistas, en su forma final
│   ├── 02_alter_cubicador.sql columnas para la respuesta del cubicador (ALTER; se puede repetir)
│   ├── 07_motivo_busqueda_agotada.sql  quinto motivo de "sin selección" en el CHECK (se puede repetir)
│   ├── pendientes/            propuestas 03 a 06, pendientes de revisión (el ejecutador no las corre)
│   └── operacion/             scripts de datos de una sola vez que corre el DBA con aprobación
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
`revincular_identidad_dbSolucionesTI_Prueba.sql` (#172, ver [`DEPLOY.md`](../DEPLOY.md#identidad-compartida-con-nexo-172)) y
`corregir_rol_comercial_32.sql` (#173: `Roles.idRolExterno` de `comercial` pasa de 34, que en `dbSolucionesTI_Prueba` es
`AdministracionZonasGeocerca`, a 32 `ClienteProComercial`; respalda `Roles` en `RespaldoRoles_173`, es idempotente y **requiere
aprobación; lo ejecuta el DBA**). En `pendientes/` está también el `05_categoria_conexo.sql` (#173, categoría del catálogo de Nexo
en el conexo; el backend no la mapea). Y el `06_idempotencia_captura.sql` (#173, deuda): tabla `CapturasIdempotentes` con `UNIQUE (usuarioId, clave)` para que la
`Idempotency-Key` de la captura sobreviva a reinicios y a despliegues `start-first`; hoy vive en memoria y el backend no la mapea.

### Con Docker

El `docker-compose.yml` de la raíz trae un servicio `db-init` que levanta después de SQL Server y corre
`ejecutar-bd.sh -c -a -y` contra la base `bd_cotifly`: la crea, crea el esquema y siembra. Es seguro en cada
`docker compose up`: si el esquema ya existe, no lo vuelve a crear; solo corre los ajustes y repasa los seeders.

```bash
docker compose up sqlserver db-init     # SQL Server local en el puerto 1434 del host
docker compose logs db-init             # ver qué hizo
```

Solo toca ese SQL Server local de desarrollo. El compose solo trae la base (`sqlserver` y `db-init`); el entorno local del
backend .NET es `backend-dotnet/scripts/levantar-local.*`.

### Qué hay que cambiar para ejecutarlo

**Nada dentro de los archivos.** El ejecutador busca `scripts/` y `seeder/` relativo a donde él está (se puede llamar desde
cualquier carpeta), y los `.sql` de `scripts/` y `seeder/` no traen ningún servidor ni nombre de base escritos: no tienen `USE`.
La base la eliges al ejecutar, con `-Servidor` y `-Base` (`-s` y `-d` en bash). Sin `-CrearBase`, la base tiene que existir ya.

La excepción son las **migraciones** de `migrations_mssql/` (0001 a 0010): esas sí traen `USE dbTarifario_Nexo_Pruebas`. Si algún
día se corren contra otra base, hay que cambiar esa línea. No aplica a la instalación desde cero.

### En SSMS o Azure Data Studio (sin el ejecutador)

1. Selecciona la base de datos destino arriba a la izquierda: los scripts corren sobre la que tengas seleccionada.
2. Abre y ejecuta, en este orden: `scripts/01_crear_esquema.sql`, `scripts/02_alter_cubicador.sql`,
   `scripts/07_motivo_busqueda_agotada.sql`, `seeder/01_lineas_negocio.sql`, `seeder/02_sap_mapeo_trqtype.sql` y `seeder/03_roles.sql`.
3. Si `dbo_cotifly` ya tiene tablas, el `01` avisa y no crea nada. Si falla otro paso, mira el error antes de seguir con
   los siguientes.

### Sin el ejecutador, con sqlcmd

```
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/01_crear_esquema.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/02_alter_cubicador.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/07_motivo_busqueda_agotada.sql
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
| `07_motivo_busqueda_agotada.sql` | HU-023 (#176) | `ck_cotizacionLineasSolicitud_motivoSinSeleccion` admite los 5 motivos (`BUSQUEDA_AGOTADA`) y exige `ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA'`. Idempotente, DROP + ADD `WITH CHECK` en una transacción; se detiene sin tocar nada si alguna ruta tiene motivo sin `SIN_SELECCION_AUTOMATICA` |

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
