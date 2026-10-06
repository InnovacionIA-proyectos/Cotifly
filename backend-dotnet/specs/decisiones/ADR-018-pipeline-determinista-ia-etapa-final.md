# ADR-018 — Pipeline determinista de cotización e IA en la etapa final

- **Estado:** Aceptada (decisión del usuario del 2026-10-05). Se implementa por partes en HU-013.2 a HU-013.9 (#98 a #105); varios puntos quedan pendientes de confirmar con el equipo de Nexo (`docs/HU-013-preguntas-nexo.md`).
- **Fecha:** 2026-10-05
- **Issue / HU:** #97 (HU-013.1). Lo implementan #98 (HU-013.2), #99 (HU-013.3), #100 (HU-013.4), #101 (HU-013.5), #102 (HU-013.6), #103 (HU-013.7), #104 (HU-013.8) y #105 (HU-013.9).
- **Relación con otras ADR:** amplía ADR-003 (la IA solo redacta y propone), **reemplaza a ADR-011** en lo que toca a Google Chat (el formato del folio sigue vigente y se retoma en §7), se apoya en ADR-015 (costeo en Nexo, que pasa a ser el único cotizador), ADR-014 (TRM y USD) y ADR-017 (líneas de solicitud). Mapeo de campos Nexo ↔ Cotifly: `docs/HU-005.1-mapeo-campos-nexo.md`.

## Contexto

Hoy conviven dos formas de cotizar:

1. **El chat (ADR-003, ADR-004):** el agente recoge los datos por slot-filling y llama a herramientas que buscan la tarifa, resuelven el margen, calculan el precio y arman el payload del PDF. Aunque los números los calculan herramientas deterministas, el LLM decide cuándo llamarlas y con qué argumentos. Así, una conversación ambigua puede terminar en un PDF con datos que el comercial no revisó campo a campo. Además, el chat no recoge el formulario ampliado que Nexo necesita (carrocería, clase de viaje, contenedor, posiciones; ADR-015 §7).
2. **El formulario de costeo (ADR-015):** `POST /web/solicitudes-costeo`, el polling del costeo de Nexo, `clsPrecioCosteoService` y `POST /web/cotizaciones/{folio}/costeo/pdf`. Todo es determinista y el costo es el real de Nexo.

Otros hechos:

- Cotifly pasa a ser un módulo de formularios dentro de una página de ClientePro. Google Chat sale del alcance.
- El comercial necesita, después del costeo, cuatro acciones: ajustar el margen, pedir de nuevo el costo a Nexo, agregar servicios conexos y redactar el asunto y la introducción del PDF.
- Nexo es la fuente de verdad del costo, también para los servicios conexos: un conexo se agrega en Nexo, asociado a una línea de una cotización ya costeada (`POST /api/cotizaciones/{id}/conexos`, `clsConexoItemRequest.lineaId` obligatorio).
- No hay control de concurrencia. El polling y el comercial pueden escribir la misma cotización a la vez (pendiente anotado en ADR-015).

## Decisión

### 1. Un solo cotizador: el pipeline determinista

La cotización se produce siempre con el flujo de ADR-015, que pasa a ser el ÚNICO cotizador:

| Paso | Quién | Endpoint de Cotifly |
|---|---|---|
| Crear la cotización (COP o USD) y enviarla a Nexo | Formulario | `POST /web/solicitudes-costeo` |
| Costo del flete y de los conexos | Nexo (API) | polling (`clsWorkerCosteoNexo`) |
| Margen sugerido | SAP TM con el BP, o la política de la línea | se resuelve al enviar (ADR-015 §5) |
| Precio del flete y de los conexos | Código (`IClsPrecioService`) | `GET /web/cotizaciones/{folio}/costeo` |
| Ajustar el margen | Comercial | `PUT .../costeo/margen` (HU-013.2) |
| Agregar servicios conexos | Comercial, en Nexo | `GET /web/servicios-conexos` y `POST .../costeo/servicios-conexos` (HU-013.3) |
| "Cotizar costo" (re-solicitar a Nexo) | Comercial | `POST .../costeo/re-solicitar` (HU-013.4) |
| Asunto e introducción del PDF | IA, con el texto estándar de respaldo; el comercial los edita | `POST .../costeo/textos/redactar` y `PUT .../costeo/textos` (HU-013.5) |
| PDF | Código (MigraDoc, ADR-006) | `POST .../costeo/pdf` |
| Resultado | Comercial, primero en Nexo | `POST .../resultado` (ADR-015 §6) |

**Las cotizaciones solo se crean por formulario.** El chat deja de crearlas (flag `Chat:CreacionHabilitada=false` en HU-013.6; después se borra el código). Las cotizaciones de chat que ya existen siguen visibles en "Mis cotizaciones".

### 2. La IA solo en la etapa final, y nunca escribe datos

La IA tiene dos funciones, y solo esas:

**(a) Redacta** el asunto y la introducción del PDF (HU-013.5):

- **Recibe** cliente, línea de negocio, origen y destino, tipo de vehículo, nombres de los conexos incluidos, moneda y nombre del comercial.
- **Nunca recibe** costos, margen ni precios.
- Tiene 15 s como máximo y su salida JSON se valida contra un esquema: sin montos, URL ni correos, asunto de hasta 120 caracteres e introducción de hasta 900.
- Si tarda, falla o la salida no valida, se usan los textos de `clsTextosEstandar` (`fuente = estandar`). El PDF **siempre** sale: `POST .../costeo/pdf` nunca llama a la IA, imprime los textos guardados o los estándar.
- Se audita el proveedor, el modelo, la duración y si se usó el respaldo.

**(b) Propone** acciones finales desde el asistente de `/web/chat` (HU-013.6):

- `/web/chat` exige `folio` (400 `FOLIO_REQUERIDO`): el asistente trabaja sobre una cotización ya creada, del comercial.
- Herramientas de solo lectura o propuesta: `consultar_cotizacion`, `proponer_ajuste_margen`, `proponer_re_solicitud` y `buscar_servicio_conexo`. Se retiran `generar_pdf` (payload armado por el LLM), `calcular_precio`, `obtener_margen`, `buscar_tarifa` y las herramientas de creación.
- **Ninguna herramienta entrega montos de dinero al LLM.** Con el margen y un precio, el LLM podría deducir el costo de Nexo. Salida exacta de `consultar_cotizacion`:

  | Campo | Ejemplo |
  |---|---|
  | `folio`, `version` | `PCN-900555111-DG-05102026-001`, 1 |
  | `estado_costeo`, `estado_envio`, `tiene_resultado`, `pdf_desactualizado` | `autocosteo`, `sin_enviar`, `false`, `false` |
  | `cliente` (nombre), `linea_negocio` | `Cliente SAS`, `Nacional` |
  | `origen`, `destino`, `tipo_vehiculo` | `Buenaventura`, `Bogotá D.C.`, `3S3` |
  | `moneda` | `COP` |
  | `margen_actual`, `margen_sugerido`, `origen_margen` (porcentajes) | `0.15`, `0.15`, `politica_linea` |
  | `conexos` (solo nombre, cantidad e incluido) | `[{"nombre": "Cargue", "cantidad": 1, "incluido": true}]` |

  Nunca trae `precio_final`, `costo`, totales, valores de conexos, `costoUnitario` ni la TRM aplicada a montos. `buscar_servicio_conexo` devuelve id, nombre, categoría y unidad, sin `costoUnitario`. Las propuestas reciben del LLM solo el margen, el motivo, los cambios de carga o el id del servicio y la cantidad.
- La respuesta es `{texto, accion_propuesta?}`. `accion_propuesta` trae `tipo`, `metodo`, `endpoint`, el `cuerpo` exacto (validado contra el esquema del endpoint destino), un `resumen` legible y el `row_version` que vio el asistente. **El `resumen` lo arma el SERVIDOR después del LLM**, con plantilla y valores de `IClsPrecioService` (por ejemplo, "Ajustar el margen de 15 % a 18 %: el precio pasa de 3.000.000 a 3.109.756 COP"). El LLM no lo escribe ni ve esos montos. El `texto` del LLM se valida contra cifras de dinero (ADR-003, HU-003.10).
- `RespuestaChat.precio`, `margen` y `pdf` quedan `deprecated` y siempre en `null`, y `SalidaPrecio` (que trae `costo`) deja de aparecer en cualquier respuesta del chat.
- **Patrón propuesta → Confirmar:** el frontend muestra el resumen con un botón Confirmar. Solo al pulsarlo llama a `metodo endpoint` con `cuerpo` y `If-Match: row_version`. Ese endpoint es el mismo del botón de la pantalla, con las mismas validaciones. **La IA nunca ejecuta cambios ni escribe datos de negocio.**

Guardar los textos que redactó la IA (a) no contradice esta regla: son textos de presentación, el comercial los puede editar y no tocan costos, margen ni precios.

### 3. Ajuste de margen (HU-013.2)

- `PUT /web/cotizaciones/{folio}/costeo/margen` `{margen, justificacion?}`. El margen es libre entre 0 y 0,99 (400 `AJUSTE_MARGEN_INVALIDO`).
- La justificación es obligatoria si el margen queda por debajo del sugerido (400 `JUSTIFICACION_REQUERIDA`).
- Solo con costo disponible (409 `COSTEO_NO_LISTO`). Con PDF se aplica la regla de §11: si la cotización no se ha enviado ni tiene resultado, se ajusta y el PDF queda desactualizado; si no, 409 `PDF_YA_ENVIADO`.
- Recalcula el flete y los conexos incluidos con `IClsPrecioService`, respetando la moneda y la TRM ya fijada.
- Registra el ajuste en `MargenAjustes` (`margenPolitica` = sugerido, `margenAplicado`, `diferencia`, `usuarioId`, `motivo` = justificación). `origen_margen` pasa a `negociado`. Ajustar al mismo margen no registra un ajuste nuevo.

### 4. Servicios conexos en Nexo (HU-013.3)

- `GET /web/servicios-conexos?q=&categoria=&page=&page_size=` consulta `GET /api/servicios-conexos?soloActivos=true` y **no expone `costoUnitario`**.
- `POST /web/cotizaciones/{folio}/costeo/servicios-conexos` `{items: [{linea_orden, servicio_id, cantidad}]}`:
  1. Cotifly traduce `linea_orden` a la línea de la cotización de Nexo (`lineaId`).
  2. Llama a `POST /api/cotizaciones/{id}/conexos` con `{items: [{lineaId, servicioConexoId, cantidad}]}`, **sin `valorAplicado`**: Nexo lo calcula y es la fuente de verdad del costo.
  3. Lee de nuevo la cotización, aplica el margen y deja el conexo incluido. Si Nexo lo agregó pero la relectura falla: 503 `NEXO_NO_DISPONIBLE` con `detalle.agregado_en_nexo = true`, sin reintentar el POST. El polling lo recoge.
- **Línea sin cotización en Nexo:** si la línea existe en Cotifly pero no hay línea de la cotización de Nexo asociada (sin flete, o sin su id guardado), 409 `LINEA_SIN_COTIZACION_NEXO`, sin llamar a Nexo. **#99 debe guardar el id de la línea de la cotización de Nexo** (`clsCotizacionLineaDto.id`) junto a `lineaSolicitudId` en `clsLineaCosteoNexo` (`Cotifly.Dominio/Cotizaciones/clsCosteoConsultadoNexo.cs`). Hoy solo se guarda el `lineaSolicitudId`.
- `items` de 1 a 20; vacío o con más de 20, 400 por esquema y sin llamar a Nexo. La regla de §11 aplica igual que en el margen.
- Roles de Nexo para agregar: `Comercial`, `CoordinadorFletes` y `Admin` (confirmado en el código de Nexo según `docs/HU-005.1-mapeo-campos-nexo.md`; el swagger no lo declara). 403 → `NEXO_SIN_PERMISO`; 400/404 → `CONEXO_INVALIDO`; caído → `NEXO_NO_DISPONIBLE`.
- Las licitaciones no admiten conexos en esta iteración (409 `ESTADO_NO_PERMITE` con `detalle.motivo = LICITACION`): su PDF de pliego no los imprime (ADR-017 §6).
- Excluir un conexo (`PUT .../servicios-conexos`) no lo borra en Nexo: el swagger no tiene DELETE de conexos y el total de Nexo puede diferir del PDF (pregunta abierta).

### 5. Re-solicitar el costeo (HU-013.4)

- "Cotizar costo" significa volver a pedir el costeo a Nexo: `POST .../costeo/re-solicitar` `{motivo, datos_carga?, nueva_version?}`. Amplía `reintentar`, que se conserva por compatibilidad.
- **Mismo folio** (sin cambios de carga y sin `nueva_version`):
  - permitido con `estado_costeo` en `costeo_vencido`, `entregada` o `error_envio` (si no, 409 `ESTADO_NO_PERMITE`);
  - solo si la cotización no se ha enviado ni tiene resultado (si no, 409 `PDF_YA_ENVIADO`);
  - se reenvía la misma cotización, responde 200 y el PDF queda desactualizado (§11).
- **Versión nueva** (con `datos_carga`, o con `nueva_version: true` sin cambios de carga):
  - se crea `{folio base}-vN` (ADR-011) con el formulario de la vigente más los cambios, si los hay, y la versión anterior se conserva sin tocar;
  - responde 201 con `Location`;
  - permitida también si ya se envió o tiene resultado; no lo está con un costeo en curso (`pendiente_envio`, `pendiente_costeo`: 409 `ESTADO_NO_PERMITE`).
- `datos_carga` acepta los mismos campos de línea que la captura (vehículo, carrocería, tipología, clase de viaje y de contenedor, exclusivo, consolidado, retorno y levante con sus ciudades, aprovisionamiento, horas, peso, valor de mercancía y posiciones), con las mismas condiciones (gap G3).
- Origen, destino, cliente y línea no se cambian por esta vía: eso es otra cotización.
- Se audita con el motivo y el usuario. El mecanismo de Nexo para "reenviar la misma" (captura nueva o reabrir) está pendiente de confirmar.
- **Implementación (#100).** Mientras Nexo no ofrezca "reabrir", re-solicitar es una **captura nueva** (`POST /api/captura-solicitudes`) con lo guardado, por `IClsEnvioCosteoService.EnviarGuardadaAsync` (el mismo envío del alta y de las licitaciones):
  - mismo folio: el `If-Match` se comprueba con `GuardarCambiosSiVersionAsync` al reclamar (estado `pendiente_envio`, costeo anterior olvidado, PDF invalidado con `MotivoInvalidacionPdf.ReSolicitud`, versión de negocio + 1) y solo después se llama a Nexo; el costo, el precio y los ids anteriores quedan en `EventosHerramienta` (`costeo_nexo_re_solicitud`). Un `error_envio` por rechazo definitivo de Nexo no se reenvía igual (`ESTADO_NO_PERMITE`);
  - versión nueva: `IClsGeneradorFolioService.CrearNuevaVersionAsync` con la identidad de la original (cliente, línea, comercial y moneda; la TRM se fija de nuevo, ver abajo), su formulario, líneas y posiciones, y los cambios de carga validados con `clsSolicitudCosteo.Validar` (las reglas de la captura, incluidas las condicionales de G3 cuando llegue #106). Con cambios de carga, una licitación responde `ESTADO_NO_PERMITE` (`LICITACION`);
  - `Idempotency-Key`: `IClsAlmacenIdempotencia` (§12). La repetición con la misma clave, comercial y huella devuelve la respuesta guardada; las simultáneas se serializan; los errores y un 200 con `error_envio` no consumen la clave.
  - **Reserva antes de Nexo (#100, corrección H1).** Todo se valida antes de escribir. Después, la cotización original sube su versión de negocio con `GuardarCambiosSiVersionAsync(If-Match)`, también al crear `-vN`: de N peticiones simultáneas con el mismo `If-Match` (sin clave) solo una crea la versión y llama a Nexo; las demás reciben `CONFLICTO_CONCURRENCIA`.
  - **Instantánea en la auditoría.** En la MISMA transacción de la reserva se escribe en `EventosHerramienta` (`costeo_nexo_re_solicitud`, fase `reservada`) la instantánea completa del costeo anterior: estado, ids de solicitud y cotización de Nexo, costo, precio, moneda y TRM, margen aplicado, sugerido, de política, origen y método, conexos, cada línea (id de Nexo, estado, costo y precio), PDF y versión. Después de Nexo se escribe la fase `resultado` (folio de origen y resultante: `Cotizaciones` no tiene `cotizacionOrigenId`, la trazabilidad de `-vN` queda en la auditoría).
  - **Margen negociado (decisión del usuario, 2026-10-06).** Si el comercial ajustó el margen (`negociado`), la re-solicitud en el mismo folio lo conserva sobre el costo nuevo, falle o no la resolución del margen; `margenSugerido` y `origenMargenSugerido` se refrescan con SAP TM o la política como referencia (`clsEnvioCosteoService.ResolverMargenAsync`). En `-vN` el margen negociado de la original se copia.
  - **Trazabilidad en Nexo.** La captura nueva agrega a `notas` "Re-solicitud de {folio}; reemplaza la solicitud {id anterior}" (en una línea nueva, sin superar 2000 caracteres: se recortan las notas existentes). La solicitud anterior sigue viva en Nexo (`docs/HU-013-preguntas-nexo.md`, pregunta 4).
  - **Moneda (decisión del usuario, 2026-10-06; ADR-014 §5).** La versión nueva `-vN` es una cotización nueva: copia la moneda pero **vuelve a fijar la TRM del día** (`IClsFijacionMonedaService.FijarAsync`) antes de reservar la versión de la original y antes de llamar a Nexo; sin TRM, `TRM_NO_DISPONIBLE` (400, igual que el formulario y las licitaciones) y no se crea nada (ni la reserva, ni la `-vN`, ni la captura). Re-solicitar en el mismo folio y reintentar conservan la moneda y la TRM fijadas.
  - **Catálogos (#106, G2).** Los `datos_carga` de `-vN` se validan con `IClsValidadorCatalogosCaptura` antes de reservar, y la captura usa el envío normalizado de #106.
  - **Restricción para #102.** Los eventos `costeo_nexo_re_solicitud` traen costos y se escriben SIN `ThreadId`: el asistente no debe leer `EventosHerramienta` de esta herramienta (ni de otra con montos) para construir su contexto.

### 6. Moneda en el formulario (HU-013.7)

- `moneda` (`COP` por defecto, o `USD`) en `POST /web/solicitudes-costeo`, con el mismo contrato que `POST /web/licitaciones` (ADR-014 §5).
- La TRM se fija al crear con `clsFijacionMonedaService`. Sin TRM, 400 `TRM_NO_DISPONIBLE`: no se guarda nada ni se envía nada a Nexo.
- El polling convierte a USD el precio del flete y de los conexos; el costo sigue en COP.
- El PDF lleva la nota de TRM mínima con la `trm_aplicada`.
- **Depende de #50 (PR #96, rama `50-hu-0121-trm-dolarapi-nota-pdf`)**, que aún no está en `QA`.

### 7. Folio, sesión y retiro de Google Chat (HU-013.9)

- **ADR-011 queda reemplazada por esta ADR.** Su formato de folio sigue vigente tal cual: `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`, generado por el servidor con las iniciales del comercial; la v2+ reutiliza el folio base. Lo que se retira es la identidad y la entrega por Google Chat.
- Se retiran `POST /chat/events` (marcado `deprecated` en el contrato), su controlador, `Infraestructura/GoogleChat/*`, las variables `GChat__*` y su validación al arrancar.
- El estado de la conversación se simplifica a una sesión web por folio.
- Las tablas `EventosConversacion` y `Sesiones` **no se borran** (auditoría): solo dejan de alimentarse.

### 8. Concurrencia optimista

- **El script `db/migrations_mssql/0006_textos_y_concurrencia.sql` es prerrequisito de #98 (HU-013.2) y lo entrega #98** (lo aplica el DBA). Agrega la versión de la cotización (en la implementación, `Cotizaciones.versionNegocio`: ver §12), `Cotizaciones.textosCotizacion` (JSON, lo usa #101) y lo que necesita la regla del PDF de §11 (marca de PDF desactualizado e historial de PDF). #99, #100 y #101 dependen de él.
- `GET .../costeo` devuelve `ETag` y `row_version`. **Toda respuesta 200 o 201 de una mutación del costeo devuelve el `ETag` nuevo**, incluidos `PUT .../servicios-conexos`, `re-solicitar` (200 y 201) y `pdf`.
- **`If-Match` es OBLIGATORIO en las mutaciones de la etapa final:** `margen`, `servicios-conexos` PUT y POST, `re-solicitar`, `textos` y `textos/redactar`. Sin él, 428 `PRECONDICION_REQUERIDA`; si no coincide, 409 `CONFLICTO_CONCURRENCIA`, sin cambios. En `POST .../costeo/pdf` sigue opcional. Antes del `0006` el servidor no lo exige. El polling también sube la versión cuando el costeo cambia (estado, costo o precio), así que una acción propuesta queda invalidada si el costeo cambió después de proponerla.
- **`Idempotency-Key`** (opcional) en `POST .../servicios-conexos` y `POST .../re-solicitar`: una repetición con la misma clave y el mismo usuario en 24 h devuelve la respuesta guardada sin volver a ejecutarla. Un doble clic no duplica el conexo en Nexo (cuyo POST no es idempotente) ni crea `-v2` y `-v3`. La misma clave con otro cuerpo responde 400. El frontend genera un UUID por intención (al pulsar Confirmar o el botón).

### 9. Estados de Nexo ↔ `estado_costeo` (vigente, ADR-015 §2 y §4)

La Parte 1 de `docs/HU-005.1-mapeo-campos-nexo.md` marcaba que los estados de Nexo no correspondían a los de Cotifly. El mapeo vigente en el código (`clsDerivacionEstadoCosteo`) es este:

| Nexo | Cotifly |
|---|---|
| (sin solicitud: falta o falló el alta) | `pendiente_envio` / `error_envio` |
| Solicitud `Pendiente` o `EnCosteo` con alguna línea sin flete | `pendiente_costeo` |
| Solicitud `Pendiente` o `EnCosteo` con todas las líneas con flete y la cotización en `Borrador` | `autocosteo` |
| Cotización `Entregada` | `entregada` |
| Solicitud `Descartada` (409 `SOLICITUD_DESCARTADA`) | `descartada` |
| 72 h sin costeo (propio de Cotifly) | `costeo_vencido` |

`GET /api/solicitudes/{id}` pasa la solicitud de `Pendiente` a `EnCosteo` (efecto pendiente con Nexo, ADR-015). Cotifly no distingue esos dos estados.

### 10. Contrato con Nexo para pedir el flete

`POST /web/solicitudes-costeo` cubre todos los campos de la Parte 2 del mapeo (encabezado, línea y posiciones). El contrato documenta en cada campo su nombre en Nexo y lo que Nexo hace con él.

Lo que el contrato fija como regla:

- **Catálogos:** el frontend envía solo códigos de catálogo. `POST /api/captura-solicitudes` resuelve texto libre y crea tipos de vehículo nuevos si no hay coincidencia.
- **Fuente de los catálogos:** `/web/catalogos-flete` migra de `biblioteca-sap-historico/filtros` (histórico de tarifas) a los catálogos maestros `GET /api/catalogos/tipos-vehiculo`, `clases-viaje`, `clases-contenedor` y `lineas-negocio`. La carrocería sigue en código hasta que Nexo publique su catálogo, y la tipología es un enum de 7 valores.

Los gaps entre el mapeo, el DTO actual y el swagger de Nexo están en `docs/HU-013-preguntas-nexo.md` §Gaps. Se implementan en una issue aparte.

**Implementación (HU-013.10, #106).** Cotifly valida por código antes de guardar: `tipo_vehiculo` y `clase_viaje` contra los catálogos maestros de Nexo, y `carroceria`, `tipologia` y `clase_contenedor` contra listas en código (con la lista provisional de extradimensionado). Los catálogos maestros se guardan en memoria (`Nexo:MinutosCacheCatalogos`, 30 min) y, **si Nexo no responde**, se usa la última copia mientras tenga menos de `Nexo:HorasCatalogosVencidos` (24 h): los catálogos cambian poco y un código válido hace unas horas lo sigue siendo. Sin copia utilizable, `GET /web/catalogos-flete` y `POST /web/solicitudes-costeo` responden 400 `CATALOGOS_FLETE_NO_DISPONIBLE` y no se guarda nada: se descartó aceptar el texto sin validar (crearía vehículos duplicados en Nexo) y guardar la cotización para validarla después (un reintento enviaría lo no validado). `MargenesPolitica.mapeoLineasNexo` se revisa contra `GET /api/catalogos/lineas-negocio` como advertencia en el log al enviar, con la copia en memoria: no hay identidad de servicio para hacerlo al arrancar ni en `/health/ready` (Nexo exige `X-Portal-Usuario-Id`), y Nexo resuelve la línea por coincidencia aproximada, así que no se bloquea el envío. `tipos-vehiculo` y `clases-viaje` son obligatorios (solo los oficiales); si fallan `clases-contenedor` o `lineas-negocio` quedan vacíos con un warning y `clase_contenedor` se valida contra la lista fija. `CATALOGOS_FLETE_NO_DISPONIBLE` responde 503, como el resto de "Nexo no disponible". Los 400 `CLIENTE_*` de Nexo son reintentables (el reintento vuelve a resolver el BP); `ENUM_INVALIDO`, `LINEA_INCOMPLETA` y `PESO_*` son definitivos.

**Pendientes (issues aparte):** en las licitaciones, las filas del Excel siguen sin `origenMunicipioId`/`destinoMunicipioId`, sin horas propias por línea y sin validación por código de catálogo (llegan como texto del pliego); y la autenticación entre servicios con Nexo (hoy solo `X-Portal-Usuario-Id`, pregunta 6 de `docs/HU-013-preguntas-nexo.md`).

### 11. Regla del PDF (cerrada)

El PDF es la foto de lo que se entrega al cliente. La regla depende de si ya se entregó:

- **No enviada y sin resultado** (`estado_envio = sin_enviar` y `ganada` nulo). Se permiten en el mismo folio:
  - ajustar el margen;
  - agregar o seleccionar conexos;
  - redactar o editar los textos;
  - re-solicitar sin cambios de carga.

  Cada una **invalida el PDF actual**: `pdf_url = null` y `pdf_desactualizado = true` en `CosteoCotizacion`. El PDF anterior queda en el historial, con su archivo y su fila de auditoría. El comercial lo regenera con `POST .../costeo/pdf`, que con un PDF vigente sigue respondiendo `PDF_YA_GENERADO`.
- **Ya enviada** (`/enviar`, `estado_envio` `enviada` o `reenviada`) **o con resultado:** esas acciones responden 409 `PDF_YA_ENVIADO`. La `detalle` dice cómo crear la versión: `{accion: "nueva_version", metodo: "POST", endpoint: ".../costeo/re-solicitar", cuerpo: {motivo, nueva_version: true}}`.
  - La versión `-vN` sin cambios de carga se crea con `re-solicitar` y `nueva_version: true`.
  - Con cambios de carga, con `datos_carga`.
  - La versión nueva pasa otra vez por Nexo, porque el costeo de la anterior pudo vencer. Sobre ella se ajusta el margen, los conexos y los textos, y se genera su PDF.
- La regla la implementa #98 junto con el `0006` para el margen y la selección de conexos. Cambia el comportamiento actual de `PUT .../servicios-conexos`, que hoy responde `PDF_YA_GENERADO`. #99, #100 y #101 la aplican en sus endpoints.

Con esto queda cerrado el pendiente "¿permitir regenerar el PDF tras ajustar?".

### 12. Decisiones de implementación (#98)

- **Versión de negocio en lugar de `ROWVERSION`.** El `ETag` es `Cotizaciones.versionNegocio` (`INT`, opaca para el cliente). La sube la aplicación solo con cambios que ve el comercial: margen, conexos, textos, re-solicitud, PDF, y el polling cuando cambia el estado, el costo o el precio. El reclamo y la agenda del polling (dos `UPDATE` por consulta, hasta unas 600 versiones en 72 h en `autocosteo`) no la cambian, así que un `ETag` no se vence solo por el paso del tiempo. Además el DBA evita reescribir la tabla al agregar un `ROWVERSION`. Reemplaza lo que §8 llamaba "`rowVersion`".
- **Comparación con bloqueo, no token global de EF.** `IClsUnitOfWork.GuardarCambiosSiVersionAsync` lee la versión con `UPDLOCK` en la misma transacción del guardado y solo confirma si coincide. La usan las mutaciones de la etapa final (con el `If-Match`), el PDF (siempre, con la versión que cargó, aunque no venga `If-Match`) y el polling (con la versión que leyó antes de consultar a Nexo: si el comercial ajustó entretanto, el polling descarta su cálculo y repite en el siguiente ciclo, resultado `Conflicto`). Todos bloquean primero la cotización y después escriben sus líneas; si aun así SQL Server elige la transacción como víctima de un interbloqueo (1205), el método devuelve `false` y el caso de uso responde `CONFLICTO_CONCURRENCIA`. Para #99, #100 y #101 quedan el filtro `[clsRequiereIfMatch]` (428) y el filtro global que publica el `ETag` de toda respuesta `IClsConVersionFila`.
- **Envío por correo y resultado también versionados.** El envío reserva el estado (enviada o reenviada, versión + 1) con `GuardarCambiosSiVersionAsync` ANTES de que el correo salga y lo deshace si el transporte falla; con el PDF desactualizado o la versión cambiada responde 409 `CONFLICTO_CONCURRENCIA` sin enviar. El resultado ganada/perdida sube la versión y guarda con comparación (si pierde, recarga y reaplica, porque Nexo ya lo tiene). Así un ajuste nunca invalida un PDF que ya salió: de un ajuste y un envío simultáneos gana exactamente uno.
- **Margen sugerido con trazabilidad.** `Cotizaciones.margenSugerido` y `origenMargenSugerido` (`sap_tm` o `politica_linea`) se fijan al resolver el margen al enviar o crear la cotización; el ajuste no los cambia ni toca `margenPolitica`. La regla de la justificación usa el sugerido, y cada fila de `MargenAjustes` lo copia en `margenPolitica`. El 0006 llena las filas existentes con lo que haya (`margenAplicado` si nadie ajustó; la política si ya estaba negociada).
- **Una sola función de precio.** `clsPrecioMonedaCosteo` la comparten el polling y el ajuste: costo en pesos por el módulo de Precio y, en otra moneda, conversión con la `trmAplicada` fijada en la cotización (lista para #103).
- **Historial del PDF.** `CotizacionPdfsHistorial` guarda la referencia del PDF invalidado, la acción, el comercial y la fecha; la acción queda además en `EventosHerramienta` (`ajustar_margen_costeo`, sin montos). Al regenerar, el archivo anterior se copia en el almacén como `{folio}-hN.pdf` (no se borra; no se descarga por `/pdfs`) y el nuevo conserva `{folio}.pdf`. Retención propuesta: la misma de los PDF del almacén (ver `docs/10-operacion.md`).
- **Justificación.** Se guarda sin secuencias ANSI completas ni caracteres de control, con los espacios repetidos colapsados. Es texto libre del comercial y puede traer datos personales del cliente: retención y tratamiento en `docs/10-operacion.md`.
- **Métricas.** `vAjustesMargenPorComercial` gana `numAjustesBajoSugerido` (`diferencia < 0`) y `GET /web/metricas/ajustes-margen` lo expone como `num_ajustes_bajo_sugerido`.
- **No se reutiliza `IClsAjusteMargenService`** (chat, HU-003.4): guarda por su cuenta y pone un motivo por defecto. El ajuste del costeo confirma el ajuste, los precios, la invalidación del PDF y la versión en una sola transacción, y deja la justificación nula por encima del sugerido.
- **Reversa del 0006:** `db/migrations_mssql/rollback/0006_textos_y_concurrencia_reversa.sql` (destructiva para esas columnas y el historial; primero se vuelve a la versión anterior de la aplicación).
- **Idempotencia (#100, adoptada por #99).** `IClsAlmacenIdempotencia.EjecutarUnaVezAsync<T>(ambito, clave, huella, accion, seGuarda, ct)` es la única abstracción de `Idempotency-Key` del proyecto: `ambito = "{operacion}:{usuarioId}"`, `huella` = SHA-256 del folio y el cuerpo, semáforo por clave, 24 h de vigencia, purga de claves vencidas y fallidas, y `seGuarda` (por defecto, solo los éxitos) decide qué resultados consumen la clave. La implementación vigente es en memoria (`clsAlmacenIdempotenciaEnMemoria`) y vale mientras la API corre con `replicas: 1` (`infra/stack.dotnet.dokploy.yml`). Al escalar se crea la tabla de idempotencia con un script `0008` y una implementación SQL de la misma interfaz.

## Alternativas consideradas

- **Mantener el chat como cotizador con más validaciones:** el LLM seguiría decidiendo cuándo calcular y generar el PDF, y el chat no recoge el formulario ampliado de Nexo. Se descarta: la fuente de verdad es el formulario.
- **Que la IA ejecute las acciones con confirmación textual ("¿confirmas?" → "sí"):** la confirmación quedaría en manos del modelo, que interpreta el "sí". Con el botón Confirmar, la ejecución es del frontend sobre un endpoint determinista, auditable igual que un clic normal.
- **Guardar los conexos solo en Cotifly con un costo del catálogo:** duplicaría la fuente de verdad del costo. Nexo puede tener tarifas por ruta o revisión de Servicios Logísticos. Se agregan en Nexo y Cotifly solo aplica el margen.
- **`If-Match` opcional en todas las mutaciones:** dejaba sin protección el Confirmar de una propuesta vieja y el doble clic. Se exige en las mutaciones de la etapa final desde el `0006` (antes el servidor no lo puede verificar) y se complementa con `Idempotency-Key`. En `POST .../costeo/pdf` sigue opcional, porque regenerar el PDF no cambia datos.
- **Bloquear todo cambio después del primer PDF (`PDF_YA_GENERADO`, el borrador de esta ADR):** obligaba a crear `-vN` por cualquier retoque antes de enviar. Se reemplaza por la regla de §11.
- **412 Precondition Failed para el conflicto:** es el código HTTP estándar, pero el frontend ya maneja los errores de negocio como 409 `ErrorNegocio`. Se usa 409 `CONFLICTO_CONCURRENCIA` (decisión del usuario).

## Consecuencias

- El PDF ya no depende de la IA: con la IA caída la cotización se entrega igual.
- El asistente es más simple: solo lee y propone, sin slot-filling de creación. ADR-004 queda limitado a la sesión por folio.
- El frontend cambia:
  - el formulario lleva `moneda`;
  - el detalle lleva botones de margen, conexos, "Cotizar costo" y textos;
  - el asistente se abre desde una cotización y muestra la acción propuesta con Confirmar;
  - se retira la vista "Asistente" para crear (HU-013.8).
- `RespuestaChat.precio`, `margen` y `pdf` quedan `deprecated` y en `null` desde HU-013.6. Se conservan en el esquema por compatibilidad.
- El contrato ya declara los endpoints nuevos (`x-estado: pendiente`). La prueba de contrato los lista con su issue en `clsContratoRutasTests.PendientesDeImplementar` y `PropiedadesPendientes`; cada issue quita su entrada al implementar. Los `.feature` nuevos van con `@pendiente @ignore` hasta que su issue enlace los pasos.
- Las métricas de ajustes de margen (ADR-016) ganan los ajustes hechos desde el costeo.

## Pendientes

| Pendiente | Dueño |
|---|---|
| ¿El conexo agregado queda costeado de inmediato o lo revisa Servicios Logísticos? ¿Se puede agregar a una cotización `Entregada`? | Equipo de Nexo |
| Mecanismo para re-solicitar sin cambios de carga (captura nueva, reabrir la solicitud o recalcular) | Equipo de Nexo |
| Autenticación de servicio para Cotifly (hoy `X-Portal-Usuario-Id` del comercial, sin token) | Duban + equipo de Nexo |
| Orden de las líneas de la cotización de Nexo frente a las de la solicitud (para traducir `linea_orden` → `lineaId`) | Equipo de Nexo |
| Script `0006_textos_y_concurrencia.sql` (`versionNegocio`, `textosCotizacion`, `pdfDesactualizado`, `margenSugerido`, `CotizacionPdfsHistorial` y `numAjustesBajoSugerido`): entregado en #98, pendiente de aprobación y de aplicarse una vez por ambiente | Duban + DBA |
| ¿Existe DELETE de conexos en Nexo? ¿Agregar un conexo cambia el estado o `entregadaAt` de la cotización? | Equipo de Nexo |
| Integrar #50 (PR #96) antes de HU-013.7 | Duban |
| Issue de gaps del formulario y migración de catálogos (`docs/HU-013-preguntas-nexo.md` §Gaps) | Coordinador + Duban |
| Compartir el contrato con el desarrollador del frontend | Duban |
