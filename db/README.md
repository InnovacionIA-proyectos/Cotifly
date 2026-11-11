# Base de datos de Cotifly

SQL Server 2019 o posterior, schema `dbo_cotifly`. Esta carpeta tiene todo lo necesario para **instalar la base desde cero**,
**poner al día una base que ya existe** y dejarla con la configuración inicial.

> **Estado:** vigente · **Dueño:** Duban Guerra (backend); revisión de scripts: scalderon-bs y el DBA · **Actualizado:** 2026-11-10 (HU-024.2, #203: esquema consolidado en `esquema/`, carga de datos en `seeder/`, ejecutador en `ejecutores/`)

## Qué hay en cada carpeta

```
db/
├── README.md                este archivo
├── esquema/
│   └── cotifly_esquema.sql  el esquema COMPLETO de dbo_cotifly, solo estructura: instala una base nueva en un paso
├── scripts/                 ajustes de las bases que ya existen (02 a 22), idempotentes, en orden de nombre
│   └── operacion/           scripts de datos de una sola vez que corre el DBA con aprobación (el ejecutador no los corre)
├── seeder/                  configuración inicial (datos), idempotente, en orden de nombre
│   ├── 01_lineas_negocio.sql      líneas de negocio y su margen de política
│   ├── 02_sap_mapeo_trqtype.sql   mapeo del TrqType de SAP TM a la línea de negocio
│   ├── 03_roles.sql               roles internos (comercial, gerente_comercial)
│   ├── 04_tarifa_seguro.sql       tasa inicial del seguro de la mercancía (0.003450)
│   └── 05_politica_seguros.sql    carga inicial de la política de seguros y sus validaciones
├── ejecutores/
│   ├── ejecutar-bd.ps1      ejecutador para Windows (PowerShell)
│   └── ejecutar-bd.sh       ejecutador para Linux / contenedores (bash)
├── jobs/
│   └── sincronizar_margen_sap.ps1   paso de PowerShell del SQL Agent Job de HU-042.1 (no se instala, ver abajo)
└── migrations_mssql/        migraciones 0001 a 0010: llevan una base del esquema viejo al de HU-021
    └── rollback/            reversas
```

| Carpeta | Para qué | Cuándo se usa |
|---|---|---|
| `esquema/` | El esquema vigente completo (tablas, columnas, restricciones, índices y el procedimiento `sp_sincronizar_margen_sap`) en un solo script. **Sin datos.** Equivale a `migrations_mssql/0001` a `0010` más todos los ajustes `scripts/02` a `22` | Solo en una base donde `dbo_cotifly` no tiene tablas. Si ya tiene, avisa y no ejecuta nada |
| `scripts/` | Ajustes al esquema (`ALTER`, tablas nuevas): cada uno revisa si su cambio ya está, corre en una transacción y se puede repetir | En las bases que ya existen. Sobre una base recién instalada con `esquema/` no cambian nada (el ejecutador igual los corre) |
| `seeder/` | La configuración que la aplicación necesita desde el primer día. Insertan solo lo que falta y no pisan lo que ya se haya ajustado | Después de los ajustes, en toda base (nueva o existente) |
| `ejecutores/` | Los scripts que corren todo lo anterior en orden con `sqlcmd` | Para instalar o poner al día una base sin correr nada a mano |
| `migrations_mssql/` | Migraciones históricas `0001` a `0010` (traen `USE dbTarifario_Nexo_Pruebas`). Las usan las pruebas de integración, `levantar-local.*` y los respaldos | Solo para una base que todavía tenga el esquema anterior a HU-021 (tabla `Cotizaciones`) |

**Regla para cambiar el esquema:** un ajuste nuevo en `scripts/` (siguiente número, idempotente) **y** el mismo cambio en
`esquema/cotifly_esquema.sql`, en el mismo PR. Los datos de configuración van en un seeder, no en el ajuste. Ver "Mantener esto
en sincronía".

Los ajustes `02` a `19` y el `22` se escribieron cuando el instalador desde cero era `scripts/01_crear_esquema.sql`: sus cabeceras
y sus mensajes de precondición todavía lo nombran. Hoy ese papel lo cumple `esquema/cotifly_esquema.sql` (el `01` se retiró en
HU-024.2, #203).

## Instalar o poner al día una base (ejecutador)

Necesitas `sqlcmd` y permiso para crear schemas, tablas, índices y procedimientos en la base.

**Windows (PowerShell):**

```powershell
cd db
# Crea la base si no existe, instala el esquema, corre los ajustes y los seeders:
.\ejecutores\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -CrearBase

# Con usuario de SQL Server en vez de autenticación de Windows:
.\ejecutores\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly -Usuario sa -CrearBase
```

**Linux / contenedores:**

```bash
cd db
./ejecutores/ejecutar-bd.sh -s localhost,1433 -d dbCotifly -u sa -p '<clave>' -c
```

El ejecutador **no tiene servidor ni base por defecto, a propósito**: así no se corre por accidente contra el servidor
compartido (`tbsdatacenter`). Antes de ejecutar muestra a dónde va y pide escribir `SI` (con `-Si` o `-y` no pregunta). Busca
`esquema/`, `scripts/` y `seeder/` en la carpeta padre de donde él está, así que se puede llamar desde cualquier carpeta.

Pasos, en orden, deteniéndose en el primer error:

1. (con `-CrearBase` / `-c`) crea la base si no existe, con la collation `Latin1_General_100_CI_AS_SC_UTF8`;
2. si `dbo_cotifly` **no tiene tablas**, instala `esquema/cotifly_esquema.sql`; si ya tiene, se salta este paso;
3. corre los ajustes `scripts/*.sql` (02 en adelante) en orden de nombre: en una base existente aplican lo que falte, en una
   base recién instalada no cambian nada (solo el `15` agrega la fila del rol `director`, ver la tabla de ajustes);
4. corre los seeders `seeder/*.sql` en orden de nombre.

| Opción (PowerShell / bash) | Qué hace |
|---|---|
| `-CrearBase` / `-c` | Crea la base de datos si no existe |
| `-SoloEsquema` / `-e` | Solo los pasos 2 y 3 (esquema si la base está vacía, y ajustes), sin seeders |
| `-SoloSeeder` / `-r` | Solo los seeders; se puede repetir sin problema |
| `-Auto` / `-a` | Se acepta por compatibilidad (`docker-compose.yml`); el ejecutador ya decide solo si instala el esquema |
| `-Usuario` y `-Clave` / `-u` y `-p` | Autenticación de SQL Server (sin esto usa la de Windows) |
| `-Si` / `-y` | No pide confirmación |

Al final muestra un resumen: tablas, líneas de negocio, mapeos SAP TM, roles, tasas del seguro y filas de la política de seguros.

En una base anterior al `0009` (sin `CotizacionMercancias`) el `02` se detiene en su precondición y el ejecutador también:
primero hay que migrarla con `migrations_mssql/0002` a `0010`. Los `.sql` de `esquema/`, `scripts/` y `seeder/` no traen
servidor ni base escritos (no tienen `USE`): la base se elige al ejecutar.

### Con Docker

El `docker-compose.yml` de la raíz trae un servicio `db-init` que levanta después de SQL Server y corre
`ejecutores/ejecutar-bd.sh -c -a -y` contra la base `bd_cotifly`: la crea, instala el esquema y siembra. Es seguro en cada
`docker compose up`: si el esquema ya existe, no lo vuelve a crear; solo corre los ajustes y repasa los seeders.

```bash
docker compose up sqlserver db-init     # SQL Server local en el puerto 1434 del host
docker compose logs db-init             # ver qué hizo
```

Solo toca ese SQL Server local de desarrollo. El entorno local del backend .NET es `backend-dotnet/scripts/levantar-local.*`,
que hace lo mismo (esquema si la base está vacía, ajustes y seeders) sobre su propio contenedor.

### Sin el ejecutador (sqlcmd, SSMS o Azure Data Studio)

En SSMS o Azure Data Studio, selecciona la base destino y abre y ejecuta los mismos archivos en el mismo orden. Con sqlcmd:

```
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i esquema/cotifly_esquema.sql      # solo en una base nueva
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/02_alter_cubicador.sql   # ... y así cada ajuste, 02 a 22, en orden
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/01_lineas_negocio.sql     # ... y así cada seeder, 01 a 05, en orden
```

`-E` autenticación de Windows, `-C` confiar en el certificado, `-I` activa `QUOTED_IDENTIFIER` (lo exigen los índices
filtrados), `-b` detiene el script en el primer error, `-f 65001` lee los archivos como UTF-8 (los nombres de los tramos de la
política llevan `≤` y `–`). Sin `-b`, SSMS o sqlcmd siguen ejecutando después de un error. `esquema/cotifly_esquema.sql` corre en
una sola transacción: con `-b`, si algo falla no queda nada creado.

## Qué crea

**Esquema** (`esquema/cotifly_esquema.sql`, o las migraciones más los ajustes): 28 tablas y 1 procedimiento en el schema
`dbo_cotifly`, sin vistas.

| Grupo | Tablas |
|---|---|
| Configuración | `MargenesPolitica`, `SapMapeoTrqType`, `TarifasSeguro`, `PoliticaSeguroTramos`, `PoliticaSeguroMedidas`, `PoliticaSeguroTramoMedidas`, `PoliticaSeguroMedidaConexos` |
| Identidad | `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` |
| Idempotencia | `CapturasIdempotentes` |
| Conversación del agente | `Sesiones`, `EventosConversacion`, `EventosHerramienta` |
| Cotizaciones | `CotizacionesSolicitud` (cabecera), `CotizacionLineasSolicitud` (ruta), `CotizacionVehiculosLineaSolicitud` (camiones), `CotizacionMercancias`, `CotizacionServiciosConexos`, `CotizacionArchivosLicitacion`, `CotizacionPdfsHistorial` |
| Márgenes y correo | `MargenAjustes`, `Correos`, `CorreoDestinatarios` |
| Margen de SAP TM | `SapMargenCliente`, `SapMargenSyncLog` y el procedimiento `sp_sincronizar_margen_sap` |

El esquema con su diagrama ER está en [`docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`](../docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md)
y el significado de cada columna en [`docs/database/DICCIONARIO_BASE_DE_DATOS.md`](../docs/database/DICCIONARIO_BASE_DE_DATOS.md).

**Seeders** (`seeder/`): la configuración que la aplicación necesita desde el primer día. Todos son idempotentes.

| Archivo | Siembra | Cuándo inserta |
|---|---|---|
| `01_lineas_negocio.sql` | 6 líneas de negocio con margen 15 %: Nacional, Distribucion, Importacion, Exportacion, Internacional, DevolucionContenedor | Las líneas que falten |
| `02_sap_mapeo_trqtype.sql` | 5 mapeos de TrqType (OPTN, OPDS, OPIM, OPEX, OPTI), con `lineaNegocioId`. **Pendiente externo:** el TrqType de `DevolucionContenedor` lo tiene que dar negocio/SAP; no se inventa | Los pares (trqType, salesOrg) que falten |
| `03_roles.sql` | Los roles `comercial` y `gerente_comercial`, sin rol externo (`idRolExterno` NULL). El tercero, `director`, lo agrega el ajuste `15` | Los roles que falten |
| `04_tarifa_seguro.sql` | La tasa del seguro de la mercancía `0.003450` (0,345 %), activa | Solo si `TarifasSeguro` está vacía |
| `05_politica_seguros.sql` | La política de seguros aprobada en HU-046.1 (#267): 7 tramos, 7 medidas, 25 reglas y 8 conexos. Valida en cada corrida que los tramos activos sean contiguos desde 0, sin huecos ni solapes, y que toda medida obligatoria tenga al menos un conexo | Solo si las 4 tablas de la política están vacías |

**Después de sembrar**, el DBA tiene que fijar los roles externos de ClientePro; mientras sean NULL nadie puede iniciar
sesión con ese rol (falla cerrado). Las sentencias están en `seeder/03_roles.sql`; los valores de prueba y de producción, en
#209 y #212. La tasa y la política se cambian con `scripts/operacion/cambiar_tasa_seguro.sql` y
`scripts/operacion/cambiar_politica_seguros.sql`, nunca editando el seeder.

**No crea** datos de ejemplo ni de prueba, ni datos históricos.

## Ajustes al esquema (`scripts/02` a `22`)

Todos están **aplicados en la base compartida** (`tbsdatacenter`, `dbTarifario_Nexo_Pruebas`), del `02` al `22`, y todos están
en `esquema/cotifly_esquema.sql`. Cada uno es idempotente y trae en su cabecera qué hace, por qué, la verificación y la reversa.
"`/health/ready`" indica si el backend lo exige para arrancar (responde 503 "esquema incompleto: falta NN" sin él).

| Script | HU | Qué cambia | `/health/ready` |
|---|---|---|---|
| `02_alter_cubicador.sql` | HU-023.3 (#158) | `CotizacionMercancias`: `idTipoMercancia` (uuid) se reemplaza por `codigoMercancia`. Camión: `volumenTotalM3`, `porcentajeLlenadoVolumen` y `porcentajeLlenadoPeso` (0 a 100). Ruta: `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion` y `condicionada`. Se detiene sin tocar nada si `idTipoMercancia` tiene valores | Sí |
| `03_orden_vehiculo_y_conexo_catalogo.sql` | HU-023.3 (#158); HU-014 (#188) | Camión: `ordenVehiculo` (CHECK ≥ 1, índice único filtrado por ruta; numera los camiones existentes). Conexo: `idServicioConexoCatalogo`. Corrige el CHECK del motivo (el mismo del `07`) | Sí |
| `04_cubicacion_segundo_plano.sql` | HU-023.5 (#160) | Ruta: la cola de la cubicación de la licitación en segundo plano (`estadoCubicacion`, `cubicacionIntentos`, `cubicacionDisponibleAt`, `errorCubicacion`, `avisosCubicacion`) y su índice filtrado. Respalda la tabla la primera vez si tiene datos | Sí |
| `05_categoria_conexo.sql` | #173; HU-014 (#188) | `CotizacionServiciosConexos.categoriaServicioConexo` | Sí |
| `06_idempotencia_captura.sql` | #191 (HU-026.1) | Tabla `CapturasIdempotentes` (la `Idempotency-Key` persistente), con su único, CHECKs e índices | Sí |
| `07_motivo_busqueda_agotada.sql` | HU-023 (#176) | `ck_cotizacionLineasSolicitud_motivoSinSeleccion` con los 5 motivos (`BUSQUEDA_AGOTADA`) y `ISNULL(resultadoCubicacion, '')` | No (solo avisa) |
| `08_retirar_vistas_actividad.sql` | HU-014.3 (#152) | `DROP VIEW` de `vActividadPorComercial` y `vAjustesMargenPorComercial`, cada una solo si existe | No |
| `09_score_prioridad.sql` | HU-018.1 (#155) | `CotizacionesSolicitud.scorePrioridad DECIMAL(9,4) NULL` | Sí |
| `10_rechazo_linea.sql` | HU-028.2 (#182) / HU-028.1 (#183) | Camión: `motivoRechazo` y `rechazadaAt` con el CHECK `ck_cotizacionVehiculosLineaSolicitud_rechazo`; `'rechazada'` en los tres CHECK de `estadoCosteo` | Sí |
| `11_peso_con_tara.sql` | HU-030.1 (#184) | Ruta: `pesoConTaraKg DECIMAL(12,2) NULL` con su CHECK (> 0) | Sí |
| `12_vehiculo_elegido.sql` | HU-032 (#197) | Ruta: `tipoVehiculoElegido` y `carroceriaElegida`. Mercancía: `largoCm`/`anchoCm`/`altoCm` NULL con el CHECK "las tres NULL o las tres > 0" | Sí |
| `13_correo_mailsender.sql` | HU-033 (#199) | `Correos`: seguimiento de MailSender (`idMailSender`, `redirigidoA`, `intentosEntrega`, `enviadoAt`, `estadoActualizadoAt`, `siguienteConsultaAt`, `encoladoAt`, dos índices filtrados y el CHECK del estado). `CotizacionesSolicitud`: resultado diferido a Nexo (`sincronizacionResultadoNexo`, `comentarioResultadoPendiente`, `resultadoSincronizadoAt`, `errorSincronizacionResultado`, `intentosSincronizacionResultado`) | Sí |
| `14_cotizacion_finalizada.sql` | HU-036 (#205, #206) | `CotizacionesSolicitud.finalizadaAt` y `finalizadaPor` (FK `fk_cotizaciones_finalizadaPor` a `UsuariosWeb`), con el CHECK `ck_cotizaciones_finalizada` (las dos o ninguna). Sin backfill | Sí |
| `15_rol_director.sql` | HU-037.1 (#209) | `ck_roles_codigo` admite `director` y agrega la fila `director` (activa, `idRolExterno` NULL; no pisa uno ya fijado). Es el único ajuste que escribe una fila: también la agrega en una base nueva, antes de los seeders. Va con o después de la imagen de HU-037.2 (ver `DEPLOY.md`, "Rol director") | No |
| `16_multiples_roles_activos.sql` | HU-040.1 (#229) | `UsuariosRoles`: retira `uq_usuariosRoles_usuarioId_activo` (varias asignaciones activas por usuario). `TokensRefresco.rolSesion` con su CHECK | Sí |
| `17_sap_margen_sincronizacion.sql` | HU-042.1 (#242) | Tablas `SapMargenCliente` y `SapMargenSyncLog` y el procedimiento `sp_sincronizar_margen_sap` (copia local del margen de SAP TM) | Sí (desde HU-042.2, #243) |
| `18_sap_mapeo_fk_margenes_politica.sql` | HU-042.4 (#247) | `SapMapeoTrqType.lineaNegocioId INT NOT NULL` con FK a `MargenesPolitica`, con backfill por texto verificado. No borra `lineaNegocio` todavía | Sí |
| `19_precio_venta_conexos.sql` | HU-043.1 (#245) | `CotizacionServiciosConexos.precioVenta DECIMAL(18,2) NOT NULL` (backfill = `precio`) y el CHECK de montos ampliado | Sí |
| `20_seguro_mercancia.sql` | HU-045.1 (#263) | Tabla `TarifasSeguro` (una sola tasa activa) y, en la ruta, `incluyeSeguro`, `tasaSeguroAplicada` y `costoSeguro` con dos CHECK. Solo estructura: la tasa inicial la siembra `seeder/04_tarifa_seguro.sql` | Sí (desde HU-045.2, #264) |
| `21_politica_seguros.sql` | HU-046.2 (#268) | Las 4 tablas de la política de seguros y `CotizacionServiciosConexos.codigoServicio`. Solo estructura: la carga y sus validaciones están en `seeder/05_politica_seguros.sql` | No (hasta HU-046.3, #269) |
| `22_origen_lectura_licitacion.sql` | HU-044.7 (#290) | `CotizacionArchivosLicitacion.origenLectura VARCHAR(20) NULL` (`analizar_excel` \| `leer_estructura`) con su CHECK | Lo exigirá el backend de HU-044.7 |

**Orden en otra base existente:** el numérico (el ejecutador los corre por nombre), con dos salvedades: el `08` va **después** de
desplegar el backend sin el borrador de métricas (la versión anterior respondería 500 en esos endpoints) y el `15` **con o
después** de la imagen de HU-037. El `10` toma un bloqueo de esquema al reemplazar los CHECK de `estadoCosteo`: mejor en una
ventana de poco tráfico. Detalle para el DBA: [`DEPLOY.md`](../DEPLOY.md) §4.

### Notas de algunos ajustes

**`scripts/03_orden_vehiculo_y_conexo_catalogo.sql` y `scripts/05_categoria_conexo.sql`** (HU-014, #188). Eran propuestas en
`pendientes/` con "NO APLICAR"; con #188 el backend mapea sus columnas y pasan a `scripts/`, como el `09`. **Conservan su número**:
es el nombre con que ya los conocen el DBA y @scalderon-bs; por nombre el `03` corre antes del `07` y deja el mismo CHECK del motivo
que el `07` (en cualquier orden terminan iguales), y el `05` no depende de ningún otro. Renumerarlos (a `10` y `11`) no cambiaría el
resultado y confundiría a quien ya los aplicó. El `03` agrega `CotizacionVehiculosLineaSolicitud.ordenVehiculo` (numera los
camiones existentes por `createdAt`, con un índice único filtrado por ruta), `CotizacionServiciosConexos.idServicioConexoCatalogo`
(NULL en las filas anteriores: el backend las sigue resolviendo por id o por nombre) y corrige el CHECK del motivo; el `05`, la
columna `categoriaServicioConexo` (NULL en las filas anteriores: el costeo la lee del catálogo). Los dos son idempotentes y **van
antes de la imagen de #188**: sin ellos `/health/ready` responde 503 "falta 03" o "falta 05". El DBA confirma que están aplicados con
la consulta de solo lectura de [`DEPLOY.md`](../DEPLOY.md) §4.5 (paso 0).

**`scripts/07_motivo_busqueda_agotada.sql`** (HU-023, #176) reemplaza el CHECK
`ck_cotizacionLineasSolicitud_motivoSinSeleccion` por uno con los **5 motivos** del contrato vigente del cubicador (agrega
`BUSQUEDA_AGOTADA`) y con `ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA'` (la regla del 02 sin el hueco del NULL, la
misma que deja el `03`). Es idempotente (si el CHECK ya tiene `BUSQUEDA_AGOTADA` **y** el `ISNULL` no hace nada; una
versión sin `ISNULL` la corrige), hace DROP y ADD `WITH CHECK` en una transacción, imprime la definición anterior como respaldo
y trae la consulta de verificación. **No es obligatorio para arrancar**: el backend lee `sys.check_constraints` y, sin el 07, guarda
`BUSQUEDA_AGOTADA` en `NULL` con el motivo real como advertencia de la ruta (`MOTIVO_SIN_SELECCION`); `/health/ready` sigue en 200 y
solo lo avisa en la descripción (la autodetección necesita `GRANT VIEW DEFINITION ON SCHEMA::dbo_cotifly TO cotifly_app`; sin él la
sonda dice que no se puede verificar, o se fija con `BaseDatos__MotivoBusquedaAgotada`, ver DEPLOY.md). El `03` deja
exactamente el mismo CHECK y solo lo reemplaza si le falta `BUSQUEDA_AGOTADA` o el `ISNULL`: en cualquier orden terminan iguales.

**`scripts/08_retirar_vistas_actividad.sql`** (HU-014.3, #152) borra las vistas `vActividadPorComercial` y
`vAjustesMargenPorComercial`, que solo leían `GET /web/metricas/actividad` y `/ajustes-margen` (el borrador del tablero, retirado
del backend y del contrato; el tablero de HU-010 lee las tablas y no usa vistas). Cada `DROP VIEW` va con
`IF OBJECT_ID(...) IS NOT NULL`: se puede repetir y sobre una instalación desde cero (`esquema/` no las crea) no hace nada. No toca
tablas ni datos. **Orden:** aplicarlo **después** de desplegar el backend de la #152; la versión anterior respondería 500 en esos
dos endpoints sin las vistas. Respaldo: su definición está en `migrations_mssql/0009` (sección de vistas).

**`scripts/09_score_prioridad.sql`** (HU-018.1, #155) agrega a la cabecera (`dbo_cotifly.CotizacionesSolicitud`, la fila que guarda
`solicitudNexoId`) la columna `scorePrioridad DECIMAL(9,4) NULL`: el `scorePrioridad` que Nexo devuelve al crear la solicitud
(`201 CapturaResultDto` de `POST /api/captura-solicitudes`, `backend-dotnet/docs/COTIFLY-CONTRATO.md` §5), que antes se descartaba. Va en
la cabecera porque es un dato de la **solicitud** de Nexo, no de la ruta. `DECIMAL(9,4)` porque el contrato no fija la escala (el ejemplo
de Nexo trae `62.5` y sus pruebas `0.73`): guarda 4 decimales y hasta `99999.9999`; el backend redondea a 4 decimales y deja `NULL` un
valor fuera de rango sin fallar el envío. `NULL` = Nexo no lo mandó, la solicitud no se ha enviado o se envió antes del 09. Es un `ADD`
de una columna `NULL` (sin respaldo: no toca filas), idempotente y en una transacción. **Sí es obligatorio para arrancar**, igual que el
`04`: el modelo EF lee la columna en cada consulta de la cabecera, así que sin ella `/health/ready` responde 503 "esquema incompleto:
falta 09" y Traefik no manda tráfico a la versión nueva. A diferencia del `07` no hay degradación (el `07` cambia un `CHECK` que el
backend puede esquivar guardando `NULL`; una columna mapeada no se puede esquivar sin dejar de leerla). Como está en `scripts/`, el
ejecutador y `db-init` lo aplican solos.

**`scripts/10_rechazo_linea.sql`** (HU-028.2, #182, para HU-028.1, #183; **lo escribió el equipo de Cotifly y lo revisa @scalderon-bs
antes de aplicarlo en la base compartida**) guarda el rechazo de una línea por la Mesa de Costeo de Nexo (Nexo #320, columnas de Nexo
#314). En Cotifly cada línea de Nexo es un **camión** (`CotizacionVehiculosLineaSolicitud`), así que el rechazo va ahí:
`motivoRechazo NVARCHAR(500) NULL` (el texto de Nexo, mismo largo que `tbl_lineas_solicitud.MotivoRechazo`) y `rechazadaAt
DATETIMEOFFSET NULL` (cuándo lo **vio** Cotifly: el `GET /api/solicitudes/{id}` de Nexo no expone su fecha ni quién rechazó, y no hay
catálogo de motivos; por eso no hay columna `rechazadaPor` ni de "comentario"). El estado es `estadoCosteo = 'rechazada'`, que el 10
agrega a los **tres** CHECK de `estadoCosteo` (camión, ruta y cabecera): la ruta queda `rechazada` cuando lo están todos sus camiones, y
la cabecera, que el backend nunca deja en `rechazada` (con todas las rutas rechazadas queda `entregada` o `pendiente_costeo`), lo admite
para que las tres listas sigan iguales al enum del backend. Agrega además `ck_cotizacionVehiculosLineaSolicitud_rechazo`: motivo y fecha
solo con el camión en `'rechazada'` (con `ISNULL(estadoCosteo, N'')`, para que un `estadoCosteo` NULL no lo burle). Cada CHECK se
reemplaza solo si le falta `'rechazada'`; todo en una transacción, con verificación al final, idempotente y sin respaldo (columnas NULL y
CHECK que las filas actuales ya cumplen). **Obligatorio para arrancar** la imagen de #183: sin las columnas `/health/ready` responde 503
"esquema incompleto: falta 10". Aplicarlo con la imagen anterior no cambia nada (no lee las columnas ni escribe `'rechazada'`). La
reversa está al final del script (solo antes de que haya filas `'rechazada'`).

**`scripts/11_peso_con_tara.sql`** (HU-030.1, #184) agrega a la **ruta** (`dbo_cotifly.CotizacionLineasSolicitud`) la columna
`pesoConTaraKg DECIMAL(12,2) NULL` y el CHECK `ck_cotizacionLineasSolicitud_pesoConTaraKg` (`NULL` o > 0): el peso total CON la tara de
los contenedores que escribe el comercial en una ruta de clase de viaje `Contenedor` (solo esa clase, aclaración del líder técnico del
2026-10-20). Va en la ruta y no en el camión porque el comercial lo escribe en la captura, antes de que existan los camiones (salen del
cubicador en la misma petición): el backend reparte la tara (peso con tara − peso de carga) por partes iguales entre los camiones al
armar las líneas de Nexo, así que el valor sobrevive a una recubicación, a la cubicación en segundo plano de la licitación y a la edición
sin una columna por camión. `DECIMAL(12,2)` es el tipo de `pesoKg` de la ruta y del camión (Nexo admite hasta `999999.99` kg con 2
decimales). La base solo exige > 0; que no sea menor que el peso de carga lo valida el backend (el peso de carga puede ser la suma de
las mercancías, que no es una columna). `NULL` = sin tara (a Nexo va el peso de carga, como antes) o una ruta anterior al 11. Es un
`ADD` de una columna `NULL` (sin respaldo: no toca filas), idempotente y en una transacción. **Obligatorio para arrancar**, igual que el
`09`: sin la columna `/health/ready` responde 503 "esquema incompleto: falta 11". Lleva el número `11` porque el `10` lo usa #183, que se
desarrolla en paralelo.

**`scripts/06_idempotencia_captura.sql`** (#191, HU-026.1; propuesto en #173) crea `dbo_cotifly.CapturasIdempotentes`: una fila por
`Idempotency-Key`, comercial y operación (`capturar_costeo`, `conexos`, `re_solicitar_costeo`, `editar_solicitud`), con `UNIQUE
(usuarioId, operacion, clave)`. Guarda la huella del cuerpo (SHA-256; los números se comparan por valor), el estado (`en_curso` con la
toma de la petición y su vencimiento de 10 min, `terminada` con la respuesta en JSON, o `libre`), y en la captura el folio, el
`solicitudNexoId` y el `scorePrioridad`. Vence en 24 h (`expiraAt`, con índice): la API ignora las vencidas y las borra cada hora
en lotes de 1000; no hace falta un job del DBA. Hasta #191 todo esto vivía en la memoria del proceso y se perdía al reiniciar o con dos
réplicas (despliegue `start-first`): un reintento podía crear otra solicitud en Nexo. **Conserva su número** (es como lo conocen el DBA
y los documentos, y el 06 estaba libre en `scripts/`); solo depende del esquema base. Idempotente; si encuentra la tabla de la propuesta de #173
(sin `operacion`) vacía la recrea, y con filas se detiene sin tocarla. Sin FK (la fila se reserva antes de que exista el folio).
**Obligatorio para arrancar**: sin la tabla `/health/ready` responde 503 "esquema incompleto: falta 06"; la API no degrada a memoria
(con varias réplicas volvería el duplicado en Nexo sin que nadie lo note). No necesita permisos nuevos (`cotifly_app` ya tiene
`SELECT/INSERT/UPDATE/DELETE` sobre el esquema).

## Bases que ya existen (migraciones)

La base compartida (`tbsdatacenter`, `dbTarifario_Nexo_Pruebas`) se **reinstaló desde cero el 2026-10-08** con el instalador de
ese día (el antiguo `scripts/01_crear_esquema.sql`, que equivalía a `0001`–`0009`) y desde ahí recibió el `0010` y los ajustes
`02` a `22`. Para una base que todavía tenga el esquema anterior (`Cotizaciones`), el `0009_reestructuracion_cotizaciones.sql` la
lleva al esquema de HU-021 conservando sus datos: hace un respaldo de las tablas que toca (`Respaldo0009_*`) y corre en una sola
transacción. Está probado con Testcontainers sobre datos del esquema anterior; aplicarlo en un ambiente real sigue necesitando
el visto bueno del DBA. El `0010_nombres_municipio.sql` va **después del `0009`** (nombres de municipio de la ruta,
`solicitudNexoId`, `nombreServicioConexo` y el `CHECK` de `estadoEnvio` si falta); es idempotente y no destructivo. Después, el
ejecutador corre los ajustes y los seeders.

## Mantener esto en sincronía

`esquema/cotifly_esquema.sql` tiene que dejar **exactamente** el mismo esquema que `migrations_mssql/0001` a `0010` más todos los
ajustes de `scripts/`. Por eso:

- **Todo cambio de esquema** es un ajuste nuevo en `scripts/` (siguiente número, idempotente, en transacción, sin `USE`) **y** el
  mismo cambio en `esquema/cotifly_esquema.sql`, en el mismo PR. En el esquema, las columnas nuevas van al final de su tabla
  (el orden de una base ajustada) y las restricciones llevan el mismo nombre que crea el ajuste.
- **Los datos de configuración** van en un seeder (`seeder/NN_*.sql`, idempotente: solo inserta lo que falta), no en el ajuste.
  Así llegan igual a una base nueva y a una existente.
- Las restricciones de las tablas renombradas conservan los nombres que dejan las migraciones (`pk_cotizaciones`,
  `pk_cotizacionPosiciones`…).
- Las pruebas de integración (`backend-dotnet/tests/Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs`,
  `LaInstalacionDesdeCeroDejaElMismoEsquemaQueLasMigraciones`) comparan en cada corrida la base instalada con `esquema/` contra
  la cadena de migraciones y ajustes.

## Verificar

Contra bases vacías **locales o de pruebas, nunca la compartida**:

1. Base A: `esquema/cotifly_esquema.sql`. Base B: `migrations_mssql/0001` a `0010` (cambiando su `USE`) y después
   `scripts/02` a `22`.
2. Comparar los catálogos de las dos (`sys.tables`, `sys.columns` con tipo, largo, precisión, escala, nulabilidad, identidad y
   collation, `sys.default_constraints`, `sys.check_constraints`, `sys.indexes` con columnas, filtros e incluidas,
   `sys.foreign_keys` y `sys.sql_modules`). Deben ser iguales salvo los respaldos `Respaldo0009_*` que deja el `0009` en B.
3. Correr el ejecutador dos veces sobre una base nueva (`-CrearBase`): la segunda vez no cambia el esquema ni duplica datos.
   El resumen debe mostrar 28 tablas, 6 líneas de negocio, 5 mapeos SAP TM, 3 roles (2 del seeder y `director` del `15`),
   1 tasa del seguro y la política con 7 tramos, 7 medidas, 25 reglas y 8 conexos.

## Job de sincronización del margen de SAP (HU-042.1, #242)

> **⚠️ Este SQL Agent Job NO se instala (decisión del 2026-11-03, #243, ADR-027).** La sincronización del margen de SAP la hace ahora
> el backend (`clsWorkerSincronizacionMargenSap` en `backend-dotnet`), que replica en C# lo que hace `jobs/sincronizar_margen_sap.ps1` y
> llama al mismo `sp_sincronizar_margen_sap` del `17`. Esta sección queda como referencia del script de Datos; no correr
> `scripts/operacion/instalar_sql_agent_job_margen_sap.sql`. Permisos que sí hacen falta: `EXECUTE` sobre el stored procedure e `INSERT`
> sobre `SapMargenSyncLog` para `cotifly_app` (ver `DEPLOY.md`).

Copia local del margen recomendado de SAP TM (`RentabClienteSet`), para no depender de una llamada en vivo a SAP. Es
trabajo de **Datos (@scalderon-bs)**, separado de `backend-dotnet` — **no se despliega con el backend, no depende de los
releases de Duban en Dokploy**. Lo que la app lee son las tablas del `17` (desde HU-042.2, #243, ADR-027: `/health/ready` exige el
`17` y el `18`); el job y el stored procedure siguen siendo solo de Datos.

**Objetos** (`scripts/17_sap_margen_sincronizacion.sql`):
- `SapMargenCliente` — estado actual: `id` propio (`UNIQUEIDENTIFIER`, igual que `SapMargenSyncLog`) como PK, con
  `(salesOrgId, trqType, bpSap)` como restricción única (la clave de negocio). `trqType` es el código crudo de SAP; el
  nombre de la línea de negocio se obtiene cruzando con `SapMapeoTrqType`, no se duplica aquí. Más `nit` (resuelto por BP,
  para cruzar directo con `CotizacionesSolicitud` sin que el backend tenga que resolverlo aparte), `margenRecomendado` (el
  número completo que manda SAP) y `activo` (soft delete: si SAP deja de traer un combo, se desactiva, no se borra).
- `#SapMargenStage` — **no es una tabla fija del esquema**: el job de PowerShell la crea como tabla temporal local en su
  propia conexión, la llena y la llama desde ahí; desaparece sola al cerrar la conexión (sin `TRUNCATE` manual y sin que
  dos corridas puedan pisarse mientras cargan datos, porque cada una tiene la suya, privada).
- `SapMargenSyncLog` — log de corridas (append-only, una fila por ejecución, no por registro de SAP), inspirado en
  `fo_snapshot_sync_log` de EmbarQA/`etl-sap`. `id` es `UNIQUEIDENTIFIER` (no serial), igual que la mayoría de las tablas
  de evento/log del esquema (`EventosConversacion`, `TokensRefresco`...).
- `sp_sincronizar_margen_sap` — hace el `MERGE` (alta, actualización solo si cambió el valor, soft delete de lo que ya no
  viene) y escribe el log, todo en una transacción con `sp_getapplock` para que no se solapen dos corridas.

**El job** (`jobs/sincronizar_margen_sap.ps1`) trae el set completo de SAP (~1500 registros, sin paginar), resuelve el
`nit` de cada BP contra `dbo.DimBPSolicitante` en el DW `DWCubosOperacionesEBS` (10.10.40.24,1433, usuario de solo lectura
`consultascubos` — **base distinta** de `DWCubosEBSSAP`, la de `ConnectionStrings:Regional` que ya usa el backend para
`DimEjecutivo`: no se puede reusar esa conexión), vuelca todo al staging y llama al stored procedure.

**Cómo corre: un SQL Server Agent Job, instalado una sola vez por servidor.** No es un worker ni un servicio que haya que
mantener prendido — es un job nativo de SQL Server Agent (como un cron del motor de base de datos). Una vez creado, sigue
corriendo solo mientras el servidor de SQL Server esté encendido, sin relación ninguna con los despliegues del backend. Si
en el futuro hay un SQL Server de producción aparte del de pruebas, el job se instala ahí también (una vez, igual que
aplicar un script de BD).

**El intervalo real es dinámico, por variable de entorno, no por el horario del job.** El SQL Agent Job se programa para
*intentar* correr seguido (cada hora); el propio script revisa en `SapMargenSyncLog` cuándo fue la última corrida exitosa
y, si fue hace menos de `SYNC_INTERVAL_MINUTOS` (1440 = 24h por defecto), sale sin hacer nada. Para cambiar la
frecuencia (por ejemplo, a cada 60 min) solo hay que cambiar esa variable de entorno — no hay que tocar ni recrear el
horario del job.

**Credenciales: archivo `.env`, mismo patrón que `backend-dotnet/.env`.** `jobs/.env.example` se versiona con placeholders;
copiarlo a `jobs/.env` en el servidor de SQL Server y llenarlo con los valores reales (`SAP_TM_URL`, `SAP_TM_USER`,
`SAP_TM_PASSWORD`, `NEXO_DW_SQL_PASSWORD`, `COTIFLY_SQL_DATABASE`, `COTIFLY_SQL_USER`, `COTIFLY_SQL_PASSWORD`). La
conexión al destino también es login SQL, no autenticación de Windows — mismo criterio que usa el propio backend para
`ConnectionStrings:Cotifly` (`clsOpcionesConexiones.cs`). `jobs/.env` queda ignorado por git (lo cubre el
`.gitignore` de la raíz) y el script lo carga solo al arrancar — no requiere variables de entorno de Windows ni reiniciar
ningún servicio.

**El esquema (tablas, SP y la FK de `SapMapeoTrqType`, scripts `17` y `18`) ya está aplicado en la base compartida de
pruebas (2026-10-28), y el job ya se probó de punta a punta a mano** (1276 filas sincronizadas, 100% con `nit` resuelto).
En esa prueba aparecieron y se corrigieron 2 bugs: la tabla del DW es `dbo.DimBPSolicitante` (BP en mayúsculas, no
`DimBpSolicitante`), y había un choque de collation entre `#SapMargenStage` (nace con el collation del servidor/tempdb) y
`SapMargenCliente` (el de esta base) — se corrigió con `COLLATE DATABASE_DEFAULT` en las columnas de texto de la temporal.

### Cómo correrlo a mano (probado varias veces, 2026-10-28)
Sirve para confirmar que el script funciona (llama a SAP, al DW, hace el upsert) sin esperar a tenerlo instalado.
Se puede correr así desde cualquier máquina con red hacia SAP, el DW y la base destino — no hace falta que sea el
servidor de SQL Server para esta parte (solo para dejarlo *instalado*, ver abajo):

1. Copiar `jobs/.env.example` a `jobs/.env` en la misma carpeta y llenar los valores reales (nunca subir `.env` al repo,
   ya queda ignorado por git).
2. Abrir PowerShell en `db/jobs/` y correr `.\sincronizar_margen_sap.ps1`.
3. Debe imprimir `Sincronización de margen SAP: N filas procesadas.`
4. Verificar en la base: `SELECT COUNT(*) FROM dbo_cotifly.SapMargenCliente;` y
   `SELECT TOP 1 * FROM dbo_cotifly.SapMargenSyncLog ORDER BY iniciadoAt DESC;` — debe haber filas y la última corrida
   debe decir `status = 'ok'`.

Dos bugs reales que aparecieron la primera vez y ya están corregidos en el script (quedan acá por si alguien los
reencuentra en otro ambiente): (1) la tabla del DW es `dbo.DimBPSolicitante`, con "BP" en mayúsculas; (2) choque de
collation entre `#SapMargenStage` (nace con el collation del servidor/tempdb) y `SapMargenCliente` (el de esta base) —
se corrigió con `COLLATE DATABASE_DEFAULT` en las columnas de texto de la temporal.

### Cómo instalarlo (que corra solo, automático)
No basta con correrlo a mano una vez: para que quede como SQL Agent Job hace falta que el **archivo** viva físicamente
en el servidor de SQL Server, y que alguien con permiso lo **registre** en SQL Agent. Son dos requisitos separados:

1. **El archivo tiene que estar en el servidor.** Copiar `jobs/sincronizar_margen_sap.ps1` y el `jobs/.env` real (con
   las claves ya llenas, no `.env.example`) a una carpeta del servidor de SQL Server (ej. `C:\Jobs\SapMargenSap\`).
   Esto requiere acceso por Escritorio Remoto (RDP) a esa máquina — no se puede hacer por red, el job solo puede
   ejecutar un archivo que esté en su propio disco.
2. **Alguien con permiso tiene que registrar el job.** Correr `scripts/operacion/instalar_sql_agent_job_margen_sap.sql`
   contra ese servidor (ajustando la ruta del paso 1 si se usó otra carpeta). Esto requiere ser `sysadmin` o pertenecer
   al rol `SQLAgentUserRole` de `msdb` — verificado que @scalderon-bs hoy no tiene ninguno de los dos.

**Bloqueado hoy por esos dos permisos** (ni RDP al servidor ni rol de SQL Agent) — ver
`docs/backlog/2026-10-28_PENDIENTES_SARA.md`. Hace falta un DBA/admin que haga los dos pasos, o que le dé esos accesos
a Datos para hacerlo directamente.
