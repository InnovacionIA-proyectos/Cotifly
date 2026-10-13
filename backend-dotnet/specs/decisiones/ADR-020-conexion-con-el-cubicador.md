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
  `POST /web/solicitudes-costeo` (`CubicacionRuta`). La licitación responde `Licitacion` y no los trae (pendiente de HU-023.5).
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
