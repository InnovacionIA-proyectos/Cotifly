# ADR-020 — Conexión de Cotifly con el cubicador (HU-023)

- **Estado:** Aceptada (2026-10-09; HU-023.2 y HU-023.4 agregadas el 2026-10-13). Parte de la milestone HU-023 (#156 a #160).
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


## Decisión — HU-023.2 (#157): "Calcular cubicaje" contra el cubicador (transición previa al corte)

- **Qué cambia.** `POST /web/cubicacion` deja de reenviar a `POST /api/cubicacion` de Nexo y llama a `POST /recomendaciones` por
  `IClsCubicador` (`clsCalculoCubicajeService`) con `idLineaSolicitud = "PRUEBA-<guid>"` (uno nuevo en cada llamada), `origenSistema =
  COTIFLY`, `claseViajeSeleccionada` = `clase_viaje` y el `idUsuarioExterno` del comercial. **No se guarda nada** (ni base ni Nexo);
  el `POST` no se reintenta (lo garantiza el adaptador). Se retiraron `clsCubicacionNexo`, `IClsCubicacionProvider`,
  `clsCubicacionNoDisponible`, `clsFabricaCubicacion`, su registro (`AgregarCubicacion`) y los códigos `CUBICACION_NO_DISPONIBLE` y
  `CUBICACION_DEMASIADO_GRANDE`.
- **Respuesta.** Una lista de vehículos (`VehiculoCubicacion`: código y nombre del vehículo, código y nombre de la carrocería, peso,
  volumen y los dos porcentajes) más `resultado`, `motivo_sin_seleccion`, `condicionada`, `mercancias_condicionadas`, `advertencias`,
  `extradimension` y `mercancias[]` (el código con el que viajó cada ítem). Ya no trae clase de viaje ni clase de contenedor. Los nombres
  salen de `GET /tipos-vehiculo` y `/tipos-carroceria` (con caché); si no responden, el código. **Cambia de forma para el frontend
  actual** (ver `CONTRATO_API_WEB.md` §8.12).
- **Mercancía por código; descripción mientras tanto.** Cada ítem lleva `codigo_mercancia`. Sin código se usa `descripcion` y se
  resuelve al tipo **activo** más parecido del catálogo con `clsSugeridorMercancia` (componente puro de Aplicacion, que reutilizará la
  licitación en HU-023.5, #160; la firma `Sugerir(texto, catalogo)` se conserva y hay una sobrecarga con los criterios): sin tildes,
  sin mayúsculas y sin signos, contra el nombre y el código. **Prefiere no sugerir antes que sugerir mal** (validación de la #157):
  - **Números:** si el tipo tiene números (`CONTENEDOR_20`), el texto debe traer exactamente esos; "Contenedor 40 pies" nunca es
    `CONTENEDOR_20`. Un tipo sin números no se descarta por los del texto ("sacos de 50 kg" son pesos). Se apartó a propósito de la regla
    literal "si la descripción tiene números, el candidato debe tenerlos": con ella "Cemento en sacos de 50 kg" no resolvería nunca.
  - **Parecido:** Dice por palabras (con plurales) o por pares de letras. El piso de 0,7 por contenido solo aplica si todas las palabras
    del texto están en el tipo, o si todas las del tipo están en el texto **y** el tipo explica la primera palabra del texto (el núcleo en
    español): "Electrodomésticos en cajas" ya no es `CAJAS`; "Bobinas de acero laminado" sí es "Bobinas de acero".
  - **Umbral** `Cubicacion:ParecidoMinimoMercancia` (0,7 por defecto, siempre más de 0,5): "Carga seca" ya no es `CARGA_GENERAL` (0,5).
  - **Ambigüedad** `Cubicacion:MargenAmbiguedadMercancia` (0,05): si el segundo tipo queda a esa distancia o menos del mejor, nada.
  Sin sugerencia: 400 `MERCANCIA_DESCONOCIDA` con el ítem (mensaje y `detalle.item`), sin llamar al cubicador. Un ítem sin código ni
  descripción también es 400 (antes se cubicaba): cambio que rompe documentado en `CONTRATO_API_WEB.md` §8.1.
- **Plan B del corte** (`Cubicacion:IncluirSugerenciaLegada`, apagado por defecto): si el frontend no alcanza a pintar `vehiculos[]` para
  el 16, encenderlo agrega `sugerencia` con la forma anterior (`SugerenciaVehiculoLegada`, `deprecated` en el contrato) derivada del
  primer vehículo (`banda = cubicador`, `factor_utilizacion` = el mayor llenado / 100, clases de viaje y contenedor `null`). No vuelve
  `sugerencia_flota`. Se apaga y se retira cuando el frontend use la lista.
- **Sin respaldo.** Es solo una vista previa: sin cubicador configurado o con el cubicador caído (incluido el catálogo de mercancías al
  resolver una descripción) responde **503 `CUBICADOR_NO_DISPONIBLE`** con un mensaje para el comercial; el detalle técnico solo va al
  log. Los rechazos del contrato (400) y `CUBICADOR_NO_AUTORIZADO` / `CUBICADOR_RESPUESTA_INVALIDA` (503) llegan con su código.
- **Pruebas.** `clsSugeridorMercanciaTests` (tildes, mayúsculas, plurales, sin coincidencia, inactivos, empates),
  `clsCalculoCubicajeServiceTests` (petición de prueba, flota mixta con nombres y sin catálogo, los 4 motivos, condicionada, descripción,
  errores) y `clsCalcularCubicajeEndpointsTests` por HTTP con SQL Server y el cubicador falso (contrato de cada respuesta, y que las tablas
  de negocio no cambian), más `specs/features/calcular_cubicaje.feature`.

### Riesgo de la fecha del corte (viernes 16 de octubre)

Nexo **tampoco** consume todavía `POST /recomendaciones`: si el 16 el cubicador retira `POST /api/cubicacion`, Nexo también se queda
sin cubicar. Cotifly ya no depende de ese endpoint con esta HU, pero **la fecha real del corte hay que confirmarla con Nexo y con el
equipo del cubicador antes de comprometerla**, y hacer la verificación contra el cubicador de QA (con Kevin): flota mixta y un código de
mercancía desconocido rechazado con un mensaje claro. Mientras el cubicador de QA no tenga URL (P3), esa verificación no se ha hecho:
todo lo anterior se probó solo con el cubicador falso.

## Decisión — HU-023.4 (#159): envío a Nexo por camión con los datos del cubicador (decisiones del usuario del 2026-10-13)

Contrato de Nexo QA (rama `QA`, fusionado pero **todavía no desplegado**): `clsCapturaLineaRequest.GrupoRutaId` (`Guid?`, S5-HU-18.3),
`clsCapturaLineaRequest.IdRecomendacionVehiculo` (texto hasta 64, S5-HU-18.2), `clsCapturaPosicionRequest.CodigoTipoMercancia` (texto
hasta 40) y los dos primeros devueltos en `clsLineaCosteoDto` de `GET /api/solicitudes/{id}`.

1. **Identidad: `X-Portal-clsUsuario-Id`.** Es el encabezado que valida `clsIdentidadPortalAuthenticationHandler` de Nexo; al
   `X-Portal-Usuario-Id` que mandaba Cotifly Nexo le responde **401**. Queda en un solo lugar (`clsIdentidadNexo.Encabezado` e
   `Identificar`), que usan todos los adaptadores: clientes, municipios, catálogos maestros, costeo, conexos, costos de conexos y el
   análisis del Excel de licitación. Los Nexo falsos de las pruebas exigen el nombre nuevo (401 sin él) y una prueba impide volver a
   escribir el nombre a mano en `src/`.
2. **`grupoRutaId`** (antes `referenciaRuta`, petición N1): el id de la ruta de Cotifly (GUID en texto) en cada línea, al enviar y al
   leer el GET. Bandera **`Nexo:EnviarGrupoRutaId`** (antes `Nexo:EnviarReferenciaRuta`), **apagada** por defecto.
3. **`idRecomendacionVehiculo`** por línea: el camión del cubicador (`CotizacionVehiculosLineaSolicitud.idRecomendacionVehiculo`, GUID en
   texto, 36 caracteres). Bandera **`Nexo:EnviarDatosCubicador`**, **apagada** por defecto.
4. **`codigoTipoMercancia`** por posición: el `codigoMercancia` de la mercancía, con la misma bandera del punto 3. Un código de más de
   40 caracteres (Cotifly admite 64) no viaja: recortarlo mandaría otro código y Nexo trata `null` como "tipo desconocido".
5. **Emparejamiento en el polling** (`clsEmparejadorLineasNexo`):
   - con `grupoRutaId` **e** `idRecomendacionVehiculo` en la línea de Nexo, se asocia directo con el camión de esa ruta y esa
     recomendación (único por camión, aun entre camiones idénticos; no exige que el contenido cuadre, porque la Mesa pudo cambiar el
     vehículo). Un par repetido en Nexo o que no está en Cotifly no se usa y la línea sigue por contenido; una línea con otra
     recomendación nunca coincide con el camión;
   - solo con `grupoRutaId`: por grupo y luego por contenido (la regla N1 que ya existía);
   - sin ninguno: la lógica de antes (contenido y orden estable). No hace falta la columna de orden.
6. **Peso.** Con **un solo camión** la línea lleva las posiciones y el **peso declarado de la ruta** (sin él, la suma de las
   posiciones), que ya cuadra al 1 % con ellas: así Nexo no responde `PESO_INCONSISTENTE` aunque el `pesoTotalKg` del cubicador difiera.
   El peso del cubicador se guarda en el camión y se muestra en Cotifly, pero no viaja. Con **varios camiones** no cambia: el peso de
   cada camión, sin posiciones. La regla es `clsLineaSolicitud.PesoLineaNexo` y "un solo camión" cuenta TODOS los camiones de la ruta (también los descartados): un descarte posterior al envío no cambia lo que viajó. El emparejador compara con ese mismo peso **en una sola pasada** (validación: comparar primero con el peso del cubicador cruzaba costos entre rutas). Las posiciones siguen la misma regla.
7. **Códigos de vehículo y carrocería del cubicador:** se mandan **ya, sin bandera**, en `tipoVehiculo` y `carroceria` (era el
   comportamiento desde HU-023.3). El usuario **acepta que Nexo cree filas nuevas en su catálogo** de configuraciones de vehículo cuando
   un código del cubicador no exista allí (Nexo resuelve o crea por texto libre). Se descartó la bandera de "cuarentena" de la issue.
8. **Línea sin vehículo** ("sin selección automática" o respaldo): **no cambia**; viaja vacía y la resuelve la Mesa. Se espera la 19.2 de
   Nexo (estado `PorCostear` para esas líneas).
9. **429 y `Retry-After`.** Nexo limita sus listados **por rol** (política `listados-estandar`: 120 por minuto por rol;
   `listados-pesados`: 20, o 60 para Admin) y responde `429 DEMASIADAS_SOLICITUDES` con `Retry-After: 30`. **El cupo se comparte por
   rol:** todos los comerciales de Cotifly (y los usuarios de Nexo con el mismo rol) gastan el mismo. `clsCupoNexoHandler` (en los dos
   clientes HTTP de Nexo, por fuera de la resiliencia) bloquea **solo las rutas limitadas en Nexo** (`/api/catalogos/*` y la bandeja `GET /api/solicitudes`; no el polling `/api/solicitudes/{id}`, ni cotizaciones, conexos, clientes o municipios, así que un 429 de catálogos no aumenta el backoff del polling) hasta que pase el
   `Retry-After` (segundos o fecha; una fecha pasada es 0 s; 30 s si no viene; tope 5 min) y responde un 429 local sin llamar a Nexo; la tubería ya no reintenta un 429. El 429
   llega como **`COSTEO_NEXO_NO_DISPONIBLE`** (el detalle trae `HTTP 429 DEMASIADAS_SOLICITUDES ... (Retry-After 30 s)`), nunca como
   `COSTEO_NEXO_RECHAZADA`; en clientes, municipios y catálogos es su "no disponible" de siempre (los catálogos sirven la copia de
   respaldo). Un 429 a un POST con efecto (conexos) cuenta como "no enviado". Los POST no se bloquean (el alta no está limitada).

**Pruebas:** encabezado nuevo en todos los adaptadores (sus pruebas leen `X-Portal-clsUsuario-Id` y los falsos exigen el nombre),
payload idéntico con las banderas apagadas (salvo el encabezado y la regla del peso) y con los campos nuevos encendidas, cada bandera por
separado, el límite de 40, emparejamiento de dos camiones idénticos de la misma ruta con y sin los campos nuevos, peso de un camión con
el del cubicador a más del 1 % y el 429 sin reintento con el bloqueo compartido entre los dos clientes de Nexo.

**Encender las banderas** cuando Nexo despliegue su rama `QA` en el ambiente correspondiente: su swagger declara
`additionalProperties: false`, así que mandarlas antes podría hacer que rechace el alta. Primero `Nexo__EnviarGrupoRutaId`, luego
`Nexo__EnviarDatosCubicador`, y verificar con el GET que vuelven en `lineas[]`.

## Consecuencias

- HU-023.4: sin desplegar el encabezado nuevo, **ninguna** llamada a Nexo autentica (401). Es un cambio que hay que desplegar ya.
- HU-023.4: un 429 de Nexo detiene por 30 s todas las lecturas de Cotifly (búsqueda de clientes, catálogos, municipios, polling).
- El frontend debe mandar `codigo_mercancia` (HU-023.3) y mostrar el nombre del vehículo desde `GET /web/tipos-vehiculo`.
- HU-023.2: la respuesta de `POST /web/cubicacion` cambia de forma (lista de vehículos) y su caída pasa a 503; el frontend de hoy debe
  ajustarse antes del corte.
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
| **Confirmar la fecha real del corte de `POST /api/cubicacion`** (Nexo tampoco consume `/recomendaciones`) y verificar "Calcular cubicaje" contra el cubicador de QA (flota mixta, código desconocido) | Duban con Nexo y el equipo del cubicador, antes del viernes 16 |
| Frontend: pintar la lista de vehículos de `POST /web/cubicacion` y mandar `codigo_mercancia` (HU-023.2) | Frontend (HU-023-FE) |
| Llave por ambiente en los secretos de Dokploy (`Cubicador__ApiKey`) | Duban (sin tocar el clúster sin autorización) |
