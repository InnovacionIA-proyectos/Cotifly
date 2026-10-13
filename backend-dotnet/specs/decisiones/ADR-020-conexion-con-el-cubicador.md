# ADR-020 — Conexión de Cotifly con el cubicador (HU-023)

- **Estado:** Aceptada (2026-10-09). Parte de la milestone HU-023 (#156 a #160).
- **Reemplaza:** el contrato provisional de la integración con el cubicador de ADR-019 (sección "Integración con el cubicador",
  HU-021.5, #133). Lo demás de ADR-019 sigue vigente.
- **Fuentes:** `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Backend.md` (HU-023, decisiones del 2026-10-08),
  `docs/conexion_nexo/2026-10-08_PETICIONES_AL_CUBICADOR.md` (peticiones P1 a P7) y la hoja de ruta del cubicador del 2026-10-08
  (contrato, sección 2).

## Contexto

La #133 construyó el adaptador del cubicador con un contrato provisional (`idTipoMercancia`, una fila por vehículo con su resultado).
El 2026-10-08 el cubicador publicó su contrato: la mercancía viaja por **código**, la respuesta es por ruta (`resultado`,
`motivoSinSeleccion`, `condicionada`) con una lista de vehículos completos, y los errores llegan como `{codigo, mensaje}`. El usuario
decidió (2026-10-09) **evolucionar** lo de la #133 hacia ese contrato, no rehacerlo: se conservan el puerto, la fábrica, la versión "no
disponible", el respaldo (`Cubicador:RespaldoSiFalla`), el presupuesto por captura, el orden de los camiones y la observabilidad.

## Decisión — HU-023.1 (#156): adaptador y catálogos

### Contrato

- **Petición** (`clsPeticionRecomendacion`): `idLineaSolicitud` (id de la ruta; `PRUEBA-<guid>` en la vista previa de HU-023.2),
  `origenSistema = COTIFLY`, `claseViajeSeleccionada` (opcional: la clase de viaje de la ruta, una preferencia), `usuarioId` (el
  `idUsuarioExterno` del comercial en texto; el planificador lo lee una vez por petición) y `mercancias[]` con `codigoMercancia`,
  `cantidad`, `largoCm`, `anchoCm`, `altoCm` y `pesoKg` (por unidad). Los nulos no viajan.
- **Respuesta** (`clsRecomendacionCubicador`): `idRecomendacion`, `resultado` (`ASIGNADO` o `SIN_SELECCION_AUTOMATICA`),
  `motivoSinSeleccion` (`EXTRADIMENSIONADO`, `SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX`, `SIN_CARROCERIA_COMPATIBLE`),
  `condicionada`, `mercanciasCondicionadas[]`, `vehiculos[]` (`idRecomendacionVehiculo`, `codigoVehiculo`, `codigoCarroceria`,
  `pesoTotalKg`, `volumenTotalM3`, `porcentajeLlenadoVolumen`, `porcentajeLlenadoPeso`, `nombreVehiculo` opcional), `advertencias[]` y
  `extradimension`. Las tres últimas (y las mercancías condicionadas) se leen con tolerancia (texto u objeto) hasta que lleguen los
  ejemplos reales (P2).

### Contra el fallo silencioso

`clsCubicadorHttp.Validar` rechaza con **`CUBICADOR_RESPUESTA_INVALIDA`** (503) toda respuesta que no cumpla el contrato: `ASIGNADO`
sin vehículos o con un vehículo al que le falta cualquiera de los siete campos obligatorios; un `resultado` o un motivo desconocido;
`ASIGNADO` con motivo o "sin selección" sin motivo; un `idRecomendacion` que no es uuid; un peso no positivo, un volumen negativo o un
porcentaje fuera de 0 a 100 (los `CHECK` del script `02`); un código de más de 64 caracteres; JSON ilegible. El detalle (qué campo
faltó) va solo al log. **Nunca va al respaldo y no se guarda nada**: tapar un dato malo con un camión sin vehículo es justamente el
fallo silencioso que describe la hoja de ruta.

### Errores

| Cubicador | Cotifly | HTTP al cliente | ¿Respaldo? |
|---|---|---|---|
| 400 `MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS` | el mismo código, con un mensaje de Cotifly y el del cubicador (hasta 300 caracteres) | 400 | No |
| 401/403 `NO_AUTORIZADO` | `CUBICADOR_NO_AUTORIZADO` (log de error: revisar `Cubicador:ApiKey`). Nunca 401: no es la sesión del comercial | 503 | **No** (auditoría: una llave mala no se convierte en silencio en rutas sin vehículo) |
| 503, 5xx, 404 (ruta sin publicar), 408, 429, red, circuito abierto, tiempo agotado | `CUBICADOR_NO_DISPONIBLE` | 503 | Sí (caída) |
| Otro 400 u otro 4xx | `CUBICADOR_NO_DISPONIBLE` (rechazo no previsto) | 503 | Sí, solo esa ruta |
| 200 fuera del contrato | `CUBICADOR_RESPUESTA_INVALIDA` | 503 | No |

### Tiempos, reintentos y salud

- `Cubicador:TiempoEsperaSegundos` pasa de 15 a **30** (el objetivo del cubicador es 10 s). El `POST` nunca se reintenta; los `GET`
  de catálogo sí (2 veces), con su tope propio `Cubicador:TiempoMaximoCatalogoSegundos`.
- **Sonda `cubicador`** en `/health/ready` (P7): con `Cubicador:RutaSalud` (vacía por defecto mientras el cubicador no publique su
  endpoint de salud) hace un `GET` liviano. Si falla, solo saca a Cotifly de servicio con `Cubicador:RespaldoSiFalla = false`; con el
  respaldo encendido queda un warning.

### Catálogos

- `GET /web/tipos-mercancia` devuelve `codigo`, `nombre`, `categoria`, `cubicable`, `condicionada_cubicacion`,
  `comentario_condicion`, `activo` e `id` (uuid, obsoleto: solo traduce el `id_tipo_mercancia` anterior).
- Nuevos `GET /web/tipos-carroceria` y `GET /web/tipos-vehiculo` (P1; mientras no exista, 503). Los tres con la caché genérica
  `clsCacheCatalogoCubicador<TFila>` (`Cubicador:MinutosCacheCatalogos`).
- **Validación de códigos** (`clsValidadorCatalogosCaptura`): `tipo_vehiculo` y `carroceria` aceptan los códigos **activos** del
  cubicador. Si el código no está ahí, o el cubicador no está configurado o no responde, se validan como el vehículo digitado del
  respaldo: el vehículo contra el catálogo maestro de Nexo y la carrocería contra la lista fija `clsCatalogosCaptura.Carrocerias`.
- **Clase de contenedor (lo que le quedaba a HU-015.1):** se quitó la lista fija de 5 códigos. Se valida contra
  `GET /api/catalogos/clases-contenedor` de Nexo (con caché; sin Nexo, `CATALOGOS_FLETE_NO_DISPONIBLE` como la clase de viaje), más la
  lista provisional de extradimensionado (G15) mientras Nexo no la formalice. Como `clases-contenedor` es un catálogo opcional de Nexo
  (si falla llega vacío), una copia sin clases tampoco valida: `CATALOGOS_FLETE_NO_DISPONIBLE`, nunca texto libre hacia Nexo.

### Planificador

`clsPlanificadorCamionesCubicador` crea un camión por vehículo de `ASIGNADO` (flota mixta), con el volumen y los porcentajes en
`clsCamionPlaneado`; `SIN_SELECCION_AUTOMATICA` es un camión sin vehículo con el peso de la ruta; "condicionada" no bloquea nada. Los
rechazos del contrato y la respuesta inválida vuelven tal cual (no van al respaldo). Durante la transición, una mercancía con el
`id_tipo_mercancia` anterior y sin código se traduce al código con el catálogo (una lectura por petición); sin código, la ruta va al
respaldo sin llamar.

### Pruebas

Cubicador falso (`clsCubicadorFalso`) con los ejemplos del contrato en `clsEjemplosContratoCubicador` (P2 armado con la hoja de ruta):
flota mixta, cada motivo de "sin selección", condicionada, extradimensión y cada error. Pruebas del adaptador: respuesta incompleta
rechazada (cada campo), `401` y `503` traducidos, sin reintento del `POST`, tiempo agotado, catálogos con caché y la sonda.

## Decisión — HU-023.3 (#158): base de datos y solicitud con camiones

### Script `02` (Parte A, de @scalderon-bs, ya aplicado en la base compartida)

`db/scripts/02_alter_cubicador.sql` se trae **sin modificarlo**: `CotizacionMercancias.idTipoMercancia` → `codigoMercancia
NVARCHAR(64) NULL`; en el camión `volumenTotalM3 DECIMAL(12,3)`, `porcentajeLlenadoVolumen` y `porcentajeLlenadoPeso DECIMAL(5,2)`
(CHECK de 0 a 100); en la ruta `idRecomendacion`, `resultadoCubicacion` (CHECK `ASIGNADO`/`SIN_SELECCION_AUTOMATICA`),
`motivoSinSeleccion` (CHECK de los 4 motivos y solo con "sin selección") y `condicionada BIT NOT NULL DEFAULT 0`. El ejecutador lo
corre después del 01 (y en `-Auto` sobre una base existente).

**Conciliación con la cadena HU-021:**
- EF y el dominio pasan de `IdTipoMercancia` a `CodigoMercancia` (obligatorio en las mercancías nuevas, `NULL` en las anteriores) y
  mapean las 7 columnas nuevas con sus `CHECK`.
- `/health/ready` exige el 02: sin sus columnas responde `esquema incompleto: falta 02` (`DescripcionEsquemaSin02`).
- Las pruebas con Testcontainers aplican `0001` a `0010` más el 02, y `01_crear_esquema.sql` más el 02, y exigen el mismo esquema;
  el 02 se prueba idempotente sobre las dos.
- **Diferencias encontradas en el 02** (sin tocarlo): se escribió sobre un `01` anterior al `0010` (sin los nombres de municipio),
  pero no depende de esas columnas y aplica igual sobre el `01` de la cadena; no trae la columna de orden del camión
  (`ordenVehiculo`) ni el id del catálogo del conexo (#136); y su `ck_cotizacionLineasSolicitud_motivoSinSeleccion` acepta un motivo con
  `resultadoCubicacion` NULL (`NULL = 'SIN_SELECCION_AUTOMATICA'` es UNKNOWN y el CHECK pasa; el backend no lo permite). Las tres
  correcciones van en `db/scripts/pendientes/03_orden_vehiculo_y_conexo_catalogo.sql` (índice único filtrado
  `(lineaSolicitudId, ordenVehiculo)`, CHECK con `ISNULL` y consulta de verificación), **pendiente de revisión de @scalderon-bs**: fuera
  de la carpeta que corre el ejecutador; el backend no mapea sus columnas, así que las filas nuevas quedarían en NULL. Una prueba la
  aplica en una base aislada y la verifica idempotente.
- **Ejecutador con `-Auto`:** corre los ajustes (`02` en adelante) en **cada** arranque; el 02 es idempotente y no hace nada la segunda
  vez. En una base anterior al `0009` (sin `CotizacionMercancias`) su precondición se detiene con error y el ejecutador también (`-b`):
  hay que migrar primero con `0009` y `0010`.

### Solicitud con camiones (Parte B)

- **Contrato:** cada mercancía lleva `codigo_mercancia`, **obligatorio solo con el cubicador configurado** (con el catálogo disponible
  debe estar activo, `MERCANCIA_DESCONOCIDA`, y ser cubicable, `MERCANCIA_NO_CUBICABLE`). Sin cubicador no hay de dónde sacarlo
  (`GET /web/tipos-mercancia` responde 503): la mercancía se guarda con el código en `NULL`, queda el warning `MERCANCIA_SIN_CODIGO` y la
  ruta va al respaldo. `id_tipo_mercancia` queda **obsoleto**: con cubicador se traduce al código con el catálogo (sin catálogo,
  `CAMPO_REQUERIDO` en `codigo_mercancia`); sin cubicador se acepta y se guarda `NULL`. Vale para el formulario y la licitación.
- **Una llamada por ruta, todo o nada (formulario y licitación):** primero se piden los camiones de todas las rutas y al final se
  guarda todo en una transacción. Si una ruta falla (rechazo, respuesta inválida, o caída sin respaldo) no se guarda nada ni se envía nada a Nexo; las
  corridas ya creadas quedan huérfanas en el cubicador (se acepta).
- **Se guarda:** un camión por vehículo (`idRecomendacionVehiculo`, `tipoVehiculo` = `codigoVehiculo`, `carroceria` =
  `codigoCarroceria`, `pesoKg` = `pesoTotalKg`, volumen y porcentajes) y en la ruta `idRecomendacion`, `resultadoCubicacion`,
  `motivoSinSeleccion` y `condicionada`. "Sin selección" es un camión sin vehículo ni carrocería con el peso de la ruta, que viaja
  igual a Nexo.
- **No se guarda:** `mercanciasCondicionadas`, `advertencias` y `extradimension`; viajan en `cubicacion[]` de la respuesta de
  `POST /web/solicitudes-costeo` (`CubicacionRuta`). HU-023.5 (#160) los empieza a guardar (`avisosCubicacion`, JSON) porque la
  licitación se cubica en segundo plano; ver abajo.
- `GET .../costeo` trae por ruta `resultado_cubicacion`, `motivo_sin_seleccion` y `condicionada`; por camión `volumen_total_m3`,
  `porcentaje_llenado_volumen` y `porcentaje_llenado_peso`; por mercancía `codigo_mercancia`.
- **Recalcular una ruta:** `POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos` (`If-Match`) vuelve a pedir los
  camiones con el mismo id de ruta, los reemplaza y devuelve los avisos de esa llamada en `rutas[].avisos_cubicacion`. Solo si la
  solicitud **nunca llegó a Nexo** (`solicitudNexoId` vacío) y no hay un envío en curso ni incierto: la misma regla que el reenvío
  (`PuedeRecalcularCamiones` = `PuedeReenviarCosteo` + sin `solicitudNexoId`). **Carrera con el reintento (validador):** el reclamo del
  reenvío (`ReclamarReenvioCosteoAsync`) sube `versionNegocio`, y el recálculo guarda con la fila bloqueada (`UPDLOCK`) y la versión del
  `If-Match`: un recálculo con el ETag anterior responde 409 `CONFLICTO_CONCURRENCIA`, y uno con el ETag nuevo encuentra el envío en curso
  (409 `ESTADO_NO_PERMITE`); en el otro orden, el recálculo cambia `updatedAt` y el reclamo del reintento falla. Nunca quedan Nexo y la
  base con camiones distintos ni se pierde el `solicitudNexoId`. Después del envío se usa la re-solicitud, que también vuelve a planificar (y copia el resultado del cubicador
  cuando conserva los camiones). Mercancías sin código: 400, el comercial las vuelve a escoger.
- **PDF:** imprime el nombre del vehículo y de la carrocería del catálogo del cubicador (`GET /tipos-vehiculo` y `/tipos-carroceria`,
  con caché); si el catálogo no responde, o el código no está, imprime el código.
- **Agente de chat:** no captura mercancías: su ruta queda con un camión sin recomendación y sin resultado del cubicador.
- **No se implementó** `GET /recomendaciones/{idRecomendacionVehiculo}/mercancias` (reconsultar qué lleva cada camión): ninguna
  pantalla lo necesita todavía.

## Decisión — HU-023.5 (#160): licitación con cubicador

### Código de mercancía sugerido

`POST /web/licitaciones/analizar-excel` reconoce por su encabezado (sin tildes ni mayúsculas) la columna de la mercancía ("mercancía",
"producto", "tipo de carga", "commodity", "descripción") y la del código ("código mercancía", "cód. producto"). Por cada fila devuelve en
`hojas[h].mercancias` el código de la columna del Excel **tal cual** (`origen = excel`) o el tipo **activo y cubicable** más parecido del
catálogo (`origen = parecido`, `sugerido = true`, `similitud`), con `clsSugeridorMercancia` (creado en la #157 para
`POST /web/cubicacion`; un solo componente para las dos HU). Debajo de 0,5 no hay sugerencia. Es una sugerencia: el comercial la
confirma o la cambia, y `POST /web/licitaciones` valida el código contra el catálogo como siempre. Sin cubicador o con el catálogo
caído el análisis no falla (`catalogo_mercancias_disponible = false`).

### Cubicación en segundo plano

- **Al crear** (con el cubicador configurado y `Cubicador:LicitacionEnSegundoPlano`, por defecto sí): se valida todo como antes, pero
  las rutas se guardan sin llamar al cubicador, con un camión provisional sin vehículo (el del respaldo) y `estadoCubicacion =
  pendiente`. La respuesta es inmediata y la licitación queda en `pendiente_envio` **sin ir a Nexo**. Sin cubicador nada cambia (el
  respaldo no llama a nadie y la licitación sale de una vez). El formulario sigue síncrono.
- **Cola en la base** (migración `0011`, en la ruta): `estadoCubicacion` (`pendiente`, `cubicada`, `fallida`; `NULL` = se cubicó al
  guardarla), `cubicacionIntentos`, `cubicacionDisponibleAt` (reclamo y espera entre reintentos, con índice filtrado), `errorCubicacion`
  y `avisosCubicacion`. Se eligió la base y no una cola en memoria: sobrevive a reinicios y la comparten las réplicas, como la agenda
  del polling.
- **Procesador** (`clsWorkerCubicacionLicitacion`, `BackgroundService` como el polling): cada `Cubicador:IntervaloTickSegundos` lee
  hasta `Cubicador:LoteRutas` rutas con el turno vencido y las procesa en paralelo, cada una en su ámbito de DI
  (`clsCubicacionLicitacionService.CubicarAsync`): reclama la ruta con un `UPDATE` condicionado al `cubicacionDisponibleAt` leído (lo
  mueve a `ahora + Cubicador:ReclamoSegundos`), llama al planificador de HU-023.3 con el id de la ruta, reemplaza el camión provisional
  y guarda el resultado y los avisos.
- **Límite de llamadas (P6):** `Cubicador:MaximoParalelo` vale **para todas las réplicas juntas**: antes de cada llamada se toma un cupo
  `cotifly:cubicador:cupo:{i}` con `sp_getapplock` de sesión (`clsCupoCubicadorSql`, el mismo mecanismo del candado de la sesión del
  agente). Sin cupo libre la vuelta termina y las rutas esperan; si una réplica muere, SQL Server suelta su cupo al cortarse la conexión.
- **Envío a Nexo:** cuando la última ruta queda cubicada, la réplica que la cubicó toma el candado del envío de esa licitación
  (`cotifly:licitacion:envio:{id}`, `sp_getapplock` sin espera), la recarga y la reclama con el mismo reclamo del reintento
  (`updatedAt` + `versionNegocio`). El reclamo solo no bastaba (la prueba de dos réplicas lo mostró): una réplica que recarga después
  del reclamo de la otra, pero antes de que esta guarde el envío, la veía todavía en `pendiente_envio` y la reclamaba otra vez; con el
  candado, quien no lo obtiene no hace nada. Si el proceso muere entre la
  última ruta y el envío, cada vuelta recupera las licitaciones cubicadas sin enviar que llevan más de 2 min sin cambios. Se envía con
  la política de la primera ruta, como el reintento.
- **Reintentos y fallas:** solo una caída sin respaldo (`CUBICADOR_NO_DISPONIBLE`) se reintenta sola, hasta `Cubicador:MaximoIntentos`,
  esperando `Cubicador:EsperaReintentoSegundos` duplicado en cada intento. Con `Cubicador:RespaldoSiFalla` una caída ya da el camión sin
  vehículo (como en el formulario). Un rechazo de la petición, una respuesta inválida o la llave rechazada dejan la ruta `fallida` al
  primer intento (nunca se tapan con el respaldo, HU-023.1). Una excepción (base, red) deja la ruta reclamada hasta que vence el
  reclamo y otra vuelta la retoma.
- **Ruta fallida:** la licitación queda en `pendiente_envio`, no sale a Nexo y el error se ve por ruta. El comercial la reintenta con
  `POST /web/licitaciones/{folio}/cubicacion/reintentar` (vuelve a la cola) o la acepta con `{"usar_respaldo": true}` (camión sin
  vehículo para la Mesa de Costeo; si no queda nada pendiente, se envía a Nexo en la misma petición).
- **Progreso:** `GET /web/licitaciones/{folio}` trae `progreso_cubicacion` (`en_curso`, `con_fallas` o `completa`, con total,
  cubicadas, pendientes y fallidas), por ruta `estado_cubicacion`, `error_cubicacion`, el resultado y los `camiones`, y `cubicacion[]` con
  los avisos. No se agregó un endpoint de progreso aparte.

### Qué cambia del "todo o nada" de la #158

En el formulario, nada. En la licitación con cubicador el "todo o nada" pasa a ser **hacia Nexo**: la licitación sí se guarda aunque una
ruta falle después, pero nada viaja a Nexo hasta que todas sus rutas estén cubicadas (`CubicacionEnCurso` bloquea el reintento del
envío, el recálculo de una ruta y la reclamación del envío). Las corridas que el cubicador creó para las demás rutas siguen guardadas
(antes quedaban huérfanas).

### Excel

`GET /web/licitaciones/{folio}/excel` agrega la columna `Camiones Cotifly` (un camión por línea de la celda, con los nombres del
catálogo del cubicador o el código) y la hoja `Camiones Cotifly` (un camión por fila). `IClsLicitacionService.ExportarCotizacionAsync`
genera el mismo Excel para cualquier cotización propia con rutas: es la que debe usar "Ver detalle" (HU-018.1, parte C).

### Riesgos

- `Cubicador:MaximoParalelo` es un supuesto (4) hasta que el cubicador conteste P6. Cada llamada en curso ocupa una conexión a SQL
  Server sin pool (el cupo).
- Las rutas se atienden en orden de creación: una licitación grande retrasa a la siguiente (no hay reparto justo entre licitaciones).
- La cubicación en segundo plano no sube `versionNegocio`: un ETag leído durante la cubicación sigue valiendo, pero las mutaciones que
  dependen de los camiones están bloqueadas mientras tanto.
- El `0011` está pendiente de aprobación del DBA; sin él `/health/ready` responde "esquema incompleto: falta 0011".

## Consecuencias

- El frontend debe mandar `codigo_mercancia` (HU-023.3) y mostrar el nombre del vehículo desde `GET /web/tipos-vehiculo`.
- Un cubicador que cambie un campo obligatorio sin avisar ya no deja camiones sin datos: la captura falla con
  `CUBICADOR_RESPUESTA_INVALIDA` y queda un warning con el campo.
- Con el respaldo encendido, una llave mala se ve como rutas sin vehículo: vigilar el log de error y `cotifly.cubicador.respaldos`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Revisar y aplicar `db/scripts/pendientes/03_orden_vehiculo_y_conexo_catalogo.sql` (orden del camión e id del catálogo del conexo) | @scalderon-bs |
| Prueba de punta a punta de la solicitud con flota mixta contra el cubicador de QA (reconsultar el `idRecomendacionVehiculo` guardado) | Duban y Kevin, cuando exista P3 |
| P1 `GET /tipos-vehiculo`, P2 ejemplos reales, P3 URL de QA, P7 endpoint de salud; confirmar la forma de `advertencias`, `mercanciasCondicionadas` y `extradimension` | Equipo del cubicador |
| Prueba de punta a punta contra el cubicador de QA (con Kevin) | Duban y Kevin, cuando exista P3 |
| Llave por ambiente en los secretos de Dokploy (`Cubicador__ApiKey`) | Duban (sin tocar el clúster sin autorización) |
| Aprobar y aplicar `db/migrations_mssql/0011_cubicacion_segundo_plano.sql` (cola de la cubicación de la licitación, HU-023.5) | DBA |
| P6: confirmar cuántas llamadas en paralelo aguanta el cubicador y fijar `Cubicador__MaximoParalelo` | Equipo del cubicador |
