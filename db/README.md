# Base de datos de Cotifly

SQL Server 2019, schema `dbo_cotifly`. Esta carpeta tiene todo lo necesario para **crear la base de datos desde cero** y
dejarla con la configuración inicial.

> **Estado:** vigente · **Dueño:** Duban Guerra (backend); revisión de scripts: scalderon-bs y el DBA · **Actualizado:** 2026-10-22

## Estado

- **Instalación desde cero probada.** `scripts/01_crear_esquema.sql` y `seeder/` se corrieron el 2026-10-08 sobre
  `dbTarifario_Nexo_Pruebas`: 20 tablas, 2 vistas, 6 líneas de negocio, 5 mapeos y 2 roles, sin errores. Desde HU-014.3 (#152)
  el `01` ya no crea las 2 vistas del borrador de métricas y el `08` las retira de las bases que ya las tienen.
- **Verificación continua.** Las pruebas de integración (Testcontainers,
  `backend-dotnet/tests/Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs`) comprueban en cada corrida que la
  instalación desde cero y las migraciones `0001` a `0010` (más el `02`, el `03`, el `pendientes/04`, el `05`, el `06`, el `07`, el `08`, el `09`, el `10`, el `11`, el `12`, el `13` y el `14`) dejan el mismo esquema, que el `0010` es
  idempotente y que el `0009` migra los datos del esquema anterior sin perder ajustes, correos ni auditoría.
- **Scripts posteriores al `0010`** y lo que falta aprobar (aprobación, no prueba):

| Script | HU | Estado |
|---|---|---|
| `migrations_mssql/0010_nombres_municipio.sql` | HU-021 | Borrador pendiente de revisión de scalderon-bs y del DBA |
| `scripts/02_alter_cubicador.sql` | HU-023.3 (#158) | En el ejecutador. Columnas para guardar la respuesta del cubicador; va después del `01` (o del `0010` en una base migrada) |
| `scripts/03_orden_vehiculo_y_conexo_catalogo.sql` | HU-023.3 (#158); HU-014 (#188) | En el ejecutador (desde #188; antes en `pendientes/`). **El backend mapea sus columnas** (`ordenVehiculo`, `idServicioConexoCatalogo`): sin él, `/health/ready` responde "esquema incompleto: falta 03". Se aplica antes de desplegar la imagen (ver abajo) |
| `scripts/pendientes/04_cubicacion_segundo_plano.sql` | HU-023.5 (#160) | Propuesta, pendiente de scalderon-bs y del DBA. **El backend sí mapea sus columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 04". Se aplica antes de desplegar la imagen. El ejecutador no lo corre; el entorno local (`levantar-local.*`) sí |
| `scripts/05_categoria_conexo.sql` | #173; HU-014 (#188) | En el ejecutador (desde #188; antes en `pendientes/`). **El backend mapea la columna** (`categoriaServicioConexo`): sin él, `/health/ready` responde "esquema incompleto: falta 05". Se aplica antes de desplegar la imagen |
| `scripts/06_idempotencia_captura.sql` | #173; #191 (HU-026.1) | En el ejecutador (desde #191; antes en `pendientes/`). Tabla `CapturasIdempotentes`: la `Idempotency-Key` persistente. **El backend la usa y la mapea**: sin él, `/health/ready` responde "esquema incompleto: falta 06". **Lo aprueba y aplica el DBA antes de desplegar la imagen** (ver abajo) |
| `scripts/07_motivo_busqueda_agotada.sql` | HU-023 (#176) | En el ejecutador (orden de nombre, después del `02`); lo aplica @scalderon-bs en la base compartida. Quinto motivo `BUSQUEDA_AGOTADA` en el CHECK del motivo. No es obligatorio para arrancar (ver abajo) |
| `scripts/08_retirar_vistas_actividad.sql` | HU-014.3 (#152) | En el ejecutador (después del `07`); **lo aplica el DBA** en la base compartida, después de desplegar el backend sin el borrador de métricas. `DROP VIEW` de `vActividadPorComercial` y `vAjustesMargenPorComercial` (ver abajo) |
| `scripts/09_score_prioridad.sql` | HU-018.1 (#155) | En el ejecutador (orden de nombre, después del `08`). `CotizacionesSolicitud.scorePrioridad DECIMAL(9,4) NULL`. **El backend mapea la columna**: sin él, `/health/ready` responde "esquema incompleto: falta 09". Se aplica antes de desplegar la imagen (ver abajo) |
| `scripts/10_rechazo_linea.sql` | HU-028.2 (#182) / HU-028.1 (#183). **Lo revisa @scalderon-bs antes de la base compartida** | En el ejecutador (orden de nombre, después del `09`). Camión: `motivoRechazo NVARCHAR(500) NULL` y `rechazadaAt DATETIMEOFFSET NULL`; `'rechazada'` en los tres CHECK de `estadoCosteo`; CHECK `ck_cotizacionVehiculosLineaSolicitud_rechazo`. **El backend mapea las columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 10". Se aplica antes de desplegar la imagen (ver abajo) |
| `scripts/11_peso_con_tara.sql` | HU-030.1 (#184) | En el ejecutador (orden de nombre, después del `09`; el `10` es de #183). `CotizacionLineasSolicitud.pesoConTaraKg DECIMAL(12,2) NULL` con su CHECK (> 0). **El backend mapea la columna**: sin él, `/health/ready` responde "esquema incompleto: falta 11". Se aplica antes de desplegar la imagen (ver abajo) |
| `scripts/12_vehiculo_elegido.sql` | HU-032 (#197) | En el ejecutador (orden de nombre, después del `11`). Ruta: `tipoVehiculoElegido NVARCHAR(100) NULL` y `carroceriaElegida NVARCHAR(100) NULL` (el vehículo elegido, respaldo del cubicador); mercancía: `largoCm`/`anchoCm`/`altoCm` pasan a NULL con el CHECK "las tres NULL o las tres > 0" (mercancías no cubicables). Idempotente y en una transacción; imprime el CHECK anterior. **El backend mapea las columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 12". Se aplica antes de desplegar la imagen |
| `scripts/13_correo_mailsender.sql` | HU-033 (#199) | En el ejecutador (orden de nombre, después del `12`). `Correos`: el seguimiento de MailSender (`idMailSender`, `redirigidoA`, `intentosEntrega`, `enviadoAt`, `estadoActualizadoAt`, `siguienteConsultaAt`, `encoladoAt`, dos índices filtrados y el CHECK del estado con `encolado` y `enviando`); `CotizacionesSolicitud`: el resultado diferido a Nexo (`sincronizacionResultadoNexo`, `comentarioResultadoPendiente`, `resultadoSincronizadoAt`, `errorSincronizacionResultado`, `intentosSincronizacionResultado`). Idempotente, no destructivo. **El backend mapea las columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 13". Se aplica antes de desplegar la imagen |
| `scripts/14_cotizacion_finalizada.sql` | HU-036 (#205, #206) | **Propuesta pendiente de revisión de @scalderon-bs.** En el ejecutador (orden de nombre, después del `13`). `CotizacionesSolicitud`: `finalizadaAt DATETIMEOFFSET NULL` y `finalizadaPor UNIQUEIDENTIFIER NULL` (FK `fk_cotizaciones_finalizadaPor` a `UsuariosWeb(id)`, CHECK `ck_cotizaciones_finalizada`: las dos o ninguna). Sin backfill. Idempotente, no destructivo, en una transacción. **El backend mapea las columnas**: sin él, `/health/ready` responde "esquema incompleto: falta 14". Se aplica antes de desplegar la imagen |
| `scripts/19_precio_venta_conexos.sql` | HU-043.1 (#245) | Ya aplicado en la base compartida de pruebas (2026-10-28), corrido dos veces sin error; dueña @scalderon-bs (Datos). `CotizacionServiciosConexos.precioVenta DECIMAL(18,2) NOT NULL`, junto a `precio` (costo unitario). Backfill `precioVenta = precio` en las filas existentes; amplía `ck_cotizacionServiciosConexos_montos` con `precioVenta >= 0`. Idempotente, en una transacción. No depende de otro ajuste; HU-043.2 (backend) la consume |
| `scripts/15_rol_director.sql` | HU-037.1 (#209) | En el ejecutador (orden de nombre, después del `13`; el `14` es de HU-036). **Propuesta del backend; la dueña es @scalderon-bs (Datos).** `ck_roles_codigo` admite `director` (el `01` ya lo trae) y agrega la fila `director` activa con `idRolExterno = NULL`. Idempotente, en una transacción, no pisa un `idRolExterno` ya fijado. **No lo exige `/health/ready`** (no agrega columnas; sin él nadie entra como director). Va **con o después** de la imagen de HU-037.2 (#210): una imagen anterior falla al leer el catálogo de roles con la fila `director` (ver `DEPLOY.md`, "Rol director"). El IDROLES lo fija un script de operación por ambiente: `operacion/mapeo_prueba_rol_director_209.sql` (prueba) y el de #212 (producción) |
| `scripts/16_multiples_roles_activos.sql` | HU-040.1 (#229) | **Aprobado por @scalderon-bs (2026-11-02).** En el ejecutador (orden de nombre, después del `15`). `UsuariosRoles`: retira el índice único filtrado `uq_usuariosRoles_usuarioId_activo` (varias asignaciones activas por usuario, una por rol: multi-rol, HU-040) y conserva `uq_usuariosRoles_usuarioId_rolId`; imprime la definición anterior. `TokensRefresco`: `rolSesion NVARCHAR(30) NULL` (el rol de la sesión, que el refresh conserva) con el CHECK `ck_tokensRefresco_rolSesion`. Idempotente, no destructivo (no cambia filas); se detiene sin tocar nada si falta la unicidad usuario + rol. **El backend mapea `rolSesion`**: sin él, `/health/ready` responde "esquema incompleto: falta 16". Se aplica antes de desplegar la imagen; es seguro con la imagen anterior (ver la cabecera del script) |
| `scripts/17_sap_margen_sincronizacion.sql` | HU-042.1 (#242) | En el ejecutador (orden de nombre, después del `16`). **Lo lee backend-dotnet desde HU-042.2 (#243, ADR-027): `/health/ready` lo exige ("falta 17").** Ya aplicado en la base compartida de pruebas (2026-10-28); dueña @scalderon-bs (Datos). Crea `SapMargenCliente`, `SapMargenSyncLog` y el stored procedure `sp_sincronizar_margen_sap` (copia local del margen recomendado de SAP TM; el staging es una tabla temporal `#SapMargenStage` que arma el propio job, no un objeto fijo del esquema). Idempotente. Lo alimenta la sincronización del backend (#243, ADR-027: el SQL Agent Job de `jobs/sincronizar_margen_sap.ps1` no se instala) — ver sección "Job de sincronización del margen de SAP" abajo |
| `scripts/18_sap_mapeo_fk_margenes_politica.sql` | HU-042.4 (#247) | En el ejecutador (después del `17`; el seeder `02` llena `lineaNegocioId` cuando el 18 ya está). **El backend resuelve por `lineaNegocioId` desde HU-042.4: `/health/ready` lo exige ("falta 18").** Ya aplicado en la base compartida de pruebas (2026-10-28), backfill verificado; dueña @scalderon-bs (Datos). `SapMapeoTrqType.lineaNegocio` (texto libre) pasa a tener también `lineaNegocioId INT NOT NULL` con FK a `MargenesPolitica(id)` — igual patrón que `CotizacionLineasSolicitud.lineaNegocioId`. Backfill por texto con verificación (se detiene si algo no cruza). **No borra `lineaNegocio` todavía**: transición en 2 pasos a propósito, hasta que el backend (HU-042.4, #247, @dguerraBSSL) deje de leerla como texto. Idempotente, en una transacción |
| `scripts/20_seguro_mercancia.sql` | HU-045.1 (#263) | En el ejecutador (orden de nombre, después del `19`); dueña @scalderon-bs (Datos). Probado en Docker (2026-11-02): instalación completa, segunda corrida sin cambios, sobre rutas existentes y con la reversa. Aplicado en la base compartida de pruebas el 2026-11-02 y vuelto a aplicar desde cero el 2026-11-03, con `id` UNIQUEIDENTIFIER (ninguna ruta tenía datos del seguro). Tabla nueva `TarifasSeguro` (la tasa del seguro con su historia; una sola activa por índice único filtrado; fila inicial `0.003450` = 0,345 %). Ruta: `incluyeSeguro BIT NOT NULL DEFAULT 0`, `tasaSeguroAplicada DECIMAL(9,6) NULL` y `costoSeguro DECIMAL(18,2) NULL` con dos CHECK. Las rutas existentes quedan sin seguro: ninguna cotización cambia de valor. Idempotente, no destructivo. **No lo exige `/health/ready`** hasta que HU-045.2 mapee las columnas. La tasa se cambia con `operacion/cambiar_tasa_seguro.sql` |
| `scripts/21_politica_seguros.sql` | HU-046.2 (#268) | En el ejecutador (orden de nombre, después del `20`); dueña @scalderon-bs (Datos). Probado en Docker (2026-11-02): instalación completa, segunda corrida sin cambios, base sin collation (como el CI), reset desde la primera versión (id INT) y los tres ejemplos del script de operación. Aplicado en la base compartida de pruebas (2026-11-03), corrido dos veces sin error; ese mismo día se le quitó a mano `PoliticaSeguroTramos.nota` (`ALTER TABLE ... DROP COLUMN nota`), que el `21` ya no crea. Cuatro tablas nuevas de la política de seguros por valor de la mercancía (`PoliticaSeguroTramos`, `PoliticaSeguroMedidas`, `PoliticaSeguroTramoMedidas`, `PoliticaSeguroMedidaConexos`) con la carga aprobada en HU-046.1 (#267): 7 tramos, 7 medidas, 25 reglas, 8 conexos. `CotizacionServiciosConexos.codigoServicio NVARCHAR(40) NULL`. Valida que los tramos activos sean contiguos y que toda medida obligatoria tenga conexos. Idempotente; solo siembra con las 4 tablas vacías. **No lo exige `/health/ready`** hasta HU-046.3. La política se cambia con `operacion/cambiar_politica_seguros.sql` |

**`scripts/pendientes/04_cubicacion_segundo_plano.sql`** (HU-023.5, #160) agrega a la ruta la cola de la cubicación de la
licitación en segundo plano (`estadoCubicacion`, `cubicacionIntentos`, `cubicacionDisponibleAt`, `errorCubicacion`,
`avisosCubicacion` y el índice filtrado `(cubicacionDisponibleAt, createdAt)`), con un respaldo previo de la tabla si tiene
datos. Es idempotente y no destructivo. **No** está en el 01 ni lo corre el ejecutador: queda en un solo lugar hasta su
aprobación. Pero el backend de la #160 SÍ mapea sus columnas: **no desplegar sin aplicar antes el 0010 y el 04**.

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
`IF OBJECT_ID(...) IS NOT NULL`: se puede repetir y sobre una instalación desde cero (el `01` ya no las crea) no hace nada. No toca
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
y los documentos, y el 06 estaba libre en `scripts/`); solo depende del `01`. Idempotente; si encuentra la tabla de la propuesta de #173
(sin `operacion`) vacía la recrea, y con filas se detiene sin tocarla. Sin FK (la fila se reserva antes de que exista el folio).
**Obligatorio para arrancar**: sin la tabla `/health/ready` responde 503 "esquema incompleto: falta 06"; la API no degrada a memoria
(con varias réplicas volvería el duplicado en Nexo sin que nadie lo note). No necesita permisos nuevos (`cotifly_app` ya tiene
`SELECT/INSERT/UPDATE/DELETE` sobre el esquema).

**Orden completo** de una base nueva: `01` (o `0001` a `0010`), `02`, `03`, `pendientes/04`, `05`, `06`, `07`, `08`, `09`, `10`, `11`,
`12`, `13`, `14` y `15`. El entorno local (`backend-dotnet/scripts/levantar-local.ps1` / `.sh`) aplica los ajustes `02` en adelante (hoy `02`, `03`,
`05`, `06`, `07`, `08`, `09`, `10`, `11`, `12`, `13`, `14` y `15`) y el `pendientes/04` en ese orden en cada arranque (son idempotentes). Con el `03` aplicado el CHECK del motivo ya es el del
`07` (sin el `07`, la sonda no da el aviso del `07`).

**Orden del release** sobre una base ya migrada: `03` → `pendientes/04` → `05` → `06` → `07` → `09` → `10` → `11` → `12` → `13` → `14` → `15` →
imagen → `08` (el `15` en la misma ventana que la imagen de HU-037, nunca antes con una imagen vieja; en la compartida ya está
aplicado y el `14` está pendiente de @scalderon-bs). El `10` (rechazo de la línea) toma un bloqueo de esquema al reemplazar los CHECK de `estadoCosteo`: mejor en una ventana
de poco tráfico. Detalle para el DBA: `DEPLOY.md` §4.

## Estructura

```
db/
├── README.md              este archivo
├── ejecutar-bd.ps1        ejecutador para Windows (PowerShell)
├── ejecutar-bd.sh         ejecutador para Linux / contenedores (bash)
├── scripts/
│   ├── 01_crear_esquema.sql   schema, tablas, restricciones e índices, en su forma final (sin vistas desde HU-014.3)
│   ├── 02_alter_cubicador.sql columnas para la respuesta del cubicador (ALTER; se puede repetir)
│   ├── 03_orden_vehiculo_y_conexo_catalogo.sql  orden del camión e id del catálogo del conexo (se puede repetir)
│   ├── 05_categoria_conexo.sql         categoría del catálogo de Nexo en el conexo (se puede repetir)
│   ├── 06_idempotencia_captura.sql     Idempotency-Key persistente: tabla CapturasIdempotentes (se puede repetir)
│   ├── 07_motivo_busqueda_agotada.sql  quinto motivo de "sin selección" en el CHECK (se puede repetir)
│   ├── 08_retirar_vistas_actividad.sql retira las vistas del borrador de métricas (se puede repetir)
│   ├── 09_score_prioridad.sql          scorePrioridad de Nexo en la cabecera (se puede repetir)
│   ├── 10_rechazo_linea.sql            rechazo de la línea por la Mesa de Costeo en el camión (se puede repetir)
│   ├── 11_peso_con_tara.sql            peso con tara de la ruta de contenedor (se puede repetir)
│   ├── 12_vehiculo_elegido.sql         vehículo elegido de la ruta y medidas NULL de la mercancía (se puede repetir)
│   ├── 13_correo_mailsender.sql        seguimiento de MailSender en Correos y resultado diferido a Nexo (se puede repetir)
│   ├── 14_cotizacion_finalizada.sql    cotización finalizada: finalizadaAt y finalizadaPor (propuesta; se puede repetir)
│   ├── 15_rol_director.sql             rol interno director (se puede repetir)
│   ├── 16_multiples_roles_activos.sql  varias asignaciones activas por usuario y rolSesion del refresh (propuesta; se puede repetir)
│   ├── 17_sap_margen_sincronizacion.sql tablas y SP de la copia local del margen de SAP (la lee el backend, HU-042.2)
│   ├── 18_sap_mapeo_fk_margenes_politica.sql  SapMapeoTrqType.lineaNegocioId (FK a MargenesPolitica; el backend la usa, HU-042.4)
│   ├── 19_precio_venta_conexos.sql     precioVenta de CotizacionServiciosConexos (se puede repetir)
│   ├── 20_seguro_mercancia.sql         tasa del seguro (TarifasSeguro) y seguro de la mercancía en la ruta (se puede repetir)
│   ├── 21_politica_seguros.sql         política de seguros por valor de la mercancía (4 tablas) y codigoServicio del conexo (se puede repetir)
│   ├── pendientes/            propuesta 04, pendiente de revisión (el ejecutador no la corre)
│   └── operacion/             scripts de datos de una sola vez que corre el DBA con aprobación
├── jobs/
│   └── sincronizar_margen_sap.ps1   paso de PowerShell del SQL Server Agent Job de HU-042.1 (no lo corre el ejecutador)
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

Necesitas `sqlcmd` y permiso para crear schemas, tablas e índices en la base.

**Windows (PowerShell):**

```powershell
cd db
# Crea la base si no existe, el esquema (01 y los ajustes 02 a 14) y los seeders:
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
`migrations_mssql/0009` y `0010`. `scripts/pendientes/` (propuestas pendientes de revisión: hoy el `04`) **no** la corre el
ejecutador. Tampoco `scripts/operacion/`: scripts de datos de una sola vez que corre el DBA con aprobación, como
`revincular_identidad_dbSolucionesTI_Prueba.sql` (#172, ver [`DEPLOY.md`](../DEPLOY.md#identidad-compartida-con-nexo-172)) y
`corregir_rol_comercial_32.sql` (#173: `Roles.idRolExterno` de `comercial` pasa de 34, que en `dbSolucionesTI_Prueba` es
`AdministracionZonasGeocerca`, a 32 `ClienteProComercial`; respalda `Roles` en `RespaldoRoles_173`, es idempotente y **requiere
aprobación; lo ejecuta el DBA**) y `mapeo_prueba_rol_director_209.sql` (#209, **solo prueba**: `gerente_comercial` 31 → 25 y
después `director` → 31, en ese orden por `uq_roles_idRolExterno`; respaldo en `RespaldoRoles_209`). El de producción es #212. El `06_idempotencia_captura.sql` salió de `pendientes/` con #191: ya lo corre el ejecutador.

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
   `scripts/03_orden_vehiculo_y_conexo_catalogo.sql`, `scripts/pendientes/04_cubicacion_segundo_plano.sql` (solo con la aprobación
   del DBA; el backend lo exige), `scripts/05_categoria_conexo.sql`, `scripts/06_idempotencia_captura.sql`,
   `scripts/07_motivo_busqueda_agotada.sql`, `scripts/08_retirar_vistas_actividad.sql`, `scripts/09_score_prioridad.sql`,
   `scripts/10_rechazo_linea.sql`, `scripts/11_peso_con_tara.sql`, `scripts/12_vehiculo_elegido.sql`,
   `scripts/13_correo_mailsender.sql`, `scripts/14_cotizacion_finalizada.sql`, `scripts/15_rol_director.sql`,
   `scripts/16_multiples_roles_activos.sql`, `seeder/01_lineas_negocio.sql`, `seeder/02_sap_mapeo_trqtype.sql` y `seeder/03_roles.sql`.
3. Si `dbo_cotifly` ya tiene tablas, el `01` avisa y no crea nada. Si falla otro paso, mira el error antes de seguir con
   los siguientes.

### Sin el ejecutador, con sqlcmd

```
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/01_crear_esquema.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/02_alter_cubicador.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/03_orden_vehiculo_y_conexo_catalogo.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/pendientes/04_cubicacion_segundo_plano.sql   # con aprobación del DBA
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/05_categoria_conexo.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/06_idempotencia_captura.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/07_motivo_busqueda_agotada.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/08_retirar_vistas_actividad.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/09_score_prioridad.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/10_rechazo_linea.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/11_peso_con_tara.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/12_vehiculo_elegido.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/13_correo_mailsender.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/14_cotizacion_finalizada.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/15_rol_director.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i scripts/16_multiples_roles_activos.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/01_lineas_negocio.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/02_sap_mapeo_trqtype.sql
sqlcmd -S <servidor> -d <base> -E -C -I -b -f 65001 -i seeder/03_roles.sql
```

`-E` autenticación de Windows, `-C` confiar en el certificado, `-I` activa `QUOTED_IDENTIFIER` (lo exigen los índices
filtrados), `-b` detiene el script en el primer error, `-f 65001` lee los archivos como UTF-8. Sin `-b`, SSMS o sqlcmd
siguen ejecutando después de un error.

## Qué crea

**Esquema** (`scripts/01_crear_esquema.sql` y los ajustes): 21 tablas en el schema `dbo_cotifly`, sin vistas: 20 del `01` y
`CapturasIdempotentes` del `06` (las 2 vistas del borrador de métricas se retiraron en HU-014.3, #152).

| Grupo | Tablas |
|---|---|
| Configuración | `MargenesPolitica`, `SapMapeoTrqType` |
| Identidad | `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` |
| Conversación del agente | `Sesiones`, `EventosConversacion`, `EventosHerramienta` |
| Cotizaciones | `CotizacionesSolicitud` (cabecera), `CotizacionLineasSolicitud` (ruta), `CotizacionVehiculosLineaSolicitud` (camiones), `CotizacionMercancias`, `CotizacionServiciosConexos`, `CotizacionArchivosLicitacion`, `CotizacionPdfsHistorial` |
| Márgenes y correo | `MargenAjustes`, `Correos`, `CorreoDestinatarios` |
| Idempotencia (`06`) | `CapturasIdempotentes` |

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
| `03_orden_vehiculo_y_conexo_catalogo.sql` | HU-023.3 (#158); HU-014 (#188) | Camión: `ordenVehiculo INT NULL` (CHECK ≥ 1, índice único filtrado por ruta; numera los camiones existentes). Conexo: `idServicioConexoCatalogo NVARCHAR(64) NULL`. Corrige el CHECK del motivo (el mismo del `07`). Idempotente, en una transacción. Obligatorio: el backend mapea las columnas |
| `05_categoria_conexo.sql` | #173; HU-014 (#188) | `CotizacionServiciosConexos.categoriaServicioConexo NVARCHAR(20) NULL` (sin CHECK de valores). Idempotente. Obligatorio: el backend mapea la columna |
| `06_idempotencia_captura.sql` | #191 (HU-026.1) | Tabla nueva `CapturasIdempotentes` (Idempotency-Key persistente) con su `UNIQUE (usuarioId, operacion, clave)`, CHECKs de estado y de JSON, e índices de vencimiento y de folio. Idempotente; recrea la tabla vacía de la propuesta de #173. Obligatorio: el backend la usa |
| `07_motivo_busqueda_agotada.sql` | HU-023 (#176) | `ck_cotizacionLineasSolicitud_motivoSinSeleccion` admite los 5 motivos (`BUSQUEDA_AGOTADA`) y exige `ISNULL(resultadoCubicacion, '') = 'SIN_SELECCION_AUTOMATICA'`. Idempotente, DROP + ADD `WITH CHECK` en una transacción; se detiene sin tocar nada si alguna ruta tiene motivo sin `SIN_SELECCION_AUTOMATICA` |
| `08_retirar_vistas_actividad.sql` | HU-014.3 (#152) | `DROP VIEW` de `vActividadPorComercial` y `vAjustesMargenPorComercial` (borrador de métricas retirado), cada uno solo si la vista existe. No toca tablas ni datos |
| `09_score_prioridad.sql` | HU-018.1 (#155) | `CotizacionesSolicitud.scorePrioridad DECIMAL(9,4) NULL` (el `scorePrioridad` de Nexo). Idempotente, `ADD` de columna NULL en una transacción. Obligatorio: el backend mapea la columna |
| `10_rechazo_linea.sql` | HU-028.2 (#182) / HU-028.1 (#183) | Camión: `motivoRechazo NVARCHAR(500) NULL` y `rechazadaAt DATETIMEOFFSET NULL` con el CHECK `ck_cotizacionVehiculosLineaSolicitud_rechazo`; `'rechazada'` en los tres CHECK de `estadoCosteo` (8 estados). Idempotente, en una transacción. Obligatorio: el backend mapea las columnas |
| `11_peso_con_tara.sql` | HU-030.1 (#184) | `CotizacionLineasSolicitud.pesoConTaraKg DECIMAL(12,2) NULL` con el CHECK `ck_cotizacionLineasSolicitud_pesoConTaraKg` (> 0): el peso con tara de una ruta `Contenedor`. Idempotente, `ADD` de columna NULL en una transacción. Obligatorio: el backend mapea la columna |
| `12_vehiculo_elegido.sql` | HU-032 (#197) | `CotizacionLineasSolicitud.tipoVehiculoElegido`/`carroceriaElegida NVARCHAR(100) NULL` y `CotizacionMercancias.largoCm/anchoCm/altoCm` NULL con el CHECK `ck_cotizacionPosiciones_medidas` (las tres NULL o las tres > 0). Idempotente, en una transacción. Obligatorio: el backend mapea las columnas |
| `13_correo_mailsender.sql` | HU-033 (#199) | `Correos`: `idMailSender`, `redirigidoA`, `intentosEntrega`, `enviadoAt`, `estadoActualizadoAt`, `siguienteConsultaAt`, `encoladoAt` (NULL) con los índices filtrados `ix_correos_siguienteConsultaAt` y `ux_correos_idMailSender` y el CHECK `ck_correos_estado`; `CotizacionesSolicitud`: `sincronizacionResultadoNexo` (CHECK), `comentarioResultadoPendiente`, `resultadoSincronizadoAt`, `errorSincronizacionResultado` (NULL) e `intentosSincronizacionResultado` (NOT NULL DEFAULT 0, CHECK ≥ 0). Idempotente. Obligatorio: el backend mapea las columnas |
| `14_cotizacion_finalizada.sql` | HU-036 (#205, #206) | `CotizacionesSolicitud.finalizadaAt DATETIMEOFFSET NULL` y `finalizadaPor UNIQUEIDENTIFIER NULL` con la FK `fk_cotizaciones_finalizadaPor` a `UsuariosWeb(id)` y el CHECK `ck_cotizaciones_finalizada` (las dos o ninguna). Idempotente, en una transacción, sin backfill. Obligatorio: el backend mapea las columnas. Propuesta pendiente de revisión de @scalderon-bs |
| `19_precio_venta_conexos.sql` | HU-043.1 (#245) | `CotizacionServiciosConexos.precioVenta DECIMAL(18,2) NOT NULL`, junto a `precio` (costo unitario, sin cambios). Backfill `precioVenta = precio` en las filas existentes, luego `NOT NULL`. Amplía `ck_cotizacionServiciosConexos_montos` con `precioVenta >= 0`. Idempotente, en una transacción, corrido dos veces sin error. Ya aplicado en la base compartida de pruebas |
| `15_rol_director.sql` | HU-037.1 (#209) | `Roles`: el CHECK `ck_roles_codigo` admite `director` (reemplazo solo si le falta; imprime el anterior) y la fila `director` (activa, `idRolExterno = NULL`). Idempotente. El `01` ya trae el CHECK con `director`; la fila la pone el 15 |
| `16_multiples_roles_activos.sql` | HU-040.1 (#229) | `UsuariosRoles`: retira `uq_usuariosRoles_usuarioId_activo` (varias asignaciones activas por usuario; `uq_usuariosRoles_usuarioId_rolId` sigue) e imprime su definición. `TokensRefresco.rolSesion NVARCHAR(30) NULL` con el CHECK `ck_tokensRefresco_rolSesion`. Idempotente, no cambia filas. Obligatorio: el backend mapea `rolSesion`. El `01` ya trae el estado final. Aprobado por @scalderon-bs (2026-11-02) |
| `20_seguro_mercancia.sql` | HU-045.1 (#263) | Tabla nueva `TarifasSeguro` (`id` UNIQUEIDENTIFIER `NEWID()`, `tasa DECIMAL(9,6)` con CHECK > 0 y < 1, `vigenteDesde`, `isActive`, `updatedAt`; índice único filtrado `uq_tarifasSeguro_activa`: una sola activa; fila inicial `0.003450`, solo si la tabla está vacía). `CotizacionLineasSolicitud`: `incluyeSeguro BIT NOT NULL DEFAULT 0`, `tasaSeguroAplicada DECIMAL(9,6) NULL`, `costoSeguro DECIMAL(18,2) NULL`, con `ck_cotizacionLineasSolicitud_seguroMontos` (costo ≥ 0, tasa entre 0 y 1, y si hay costo hay tasa) y `ck_cotizacionLineasSolicitud_seguroIncluido` (sin seguro, las dos en NULL). Idempotente, no cambia valores |
| `21_politica_seguros.sql` | HU-046.2 (#268) | Tablas nuevas `PoliticaSeguroTramos` (`valorDesde` exclusivo, `valorHasta` incluido o NULL sin tope; CHECK de valores; índice único filtrado `uq_politicaSeguroTramos_valorDesde_activo`), `PoliticaSeguroMedidas` (`codigo` único, `esCobrable`), `PoliticaSeguroTramoMedidas` (único `tramoId` + `medidaId`, CHECK `nivel` `recomendada` \| `obligatoria`) y `PoliticaSeguroMedidaConexos` (único `medidaId` + `codigoServicio`, sin FK a Nexo); las 4 con `id` UNIQUEIDENTIFIER `NEWID()`. `CotizacionServiciosConexos.codigoServicio NVARCHAR(40) NULL`. Carga inicial solo con las 4 tablas vacías; valida tramos contiguos y medidas obligatorias con conexos (THROW y no aplica nada). Idempotente, no cambia valores |

El `02` se detiene sin tocar nada si `idTipoMercancia` todavía tiene valores (un uuid no se puede convertir a código).
Cuando el diseño se estabilice, estos cambios se pasan al `01` y el script se retira.

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

## Mantener esto en sincronía

`scripts/01_crear_esquema.sql` es el resultado de aplicar todas las migraciones. **Cada migración nueva (`000N`) tiene que
reflejarse también ahí**, para que una instalación desde cero y una base migrada queden idénticas. Por eso las restricciones
de las tablas renombradas conservan los nombres que dejan las migraciones (`pk_cotizaciones`, `pk_cotizacionPosiciones`…).

## Verificar

Contra una base vacía **local o de pruebas, nunca la compartida**:

```powershell
.\ejecutar-bd.ps1 -Servidor localhost -Base dbCotifly_prueba -CrearBase
```

El resumen final debe mostrar 21 tablas (las 20 del `01` y `CapturasIdempotentes` del `06`), 6 líneas de negocio, 5 mapeos SAP TM y
2 roles. Al final de cada ajuste (`02` a `13`) hay una consulta para comprobar sus columnas. Para comprobar que quedó igual que
una base migrada, aplicar en otra base vacía `db/migrations_mssql/0001` a `0010` más los ajustes `02` a `13` (y `pendientes/04` en
las dos) y comparar columnas (por ejemplo con `INFORMATION_SCHEMA.COLUMNS`) entre las dos.
