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
- La respuesta es `{texto, accion_propuesta?}`. `accion_propuesta` trae `tipo`, `metodo`, `endpoint`, el `cuerpo` exacto (validado contra el esquema del endpoint destino), un `resumen` legible armado por el servidor con valores calculados por código y el `row_version` que vio el asistente.
- **Patrón propuesta → Confirmar:** el frontend muestra el resumen con un botón Confirmar. Solo al pulsarlo llama a `metodo endpoint` con `cuerpo` y `If-Match: row_version`. Ese endpoint es el mismo del botón de la pantalla, con las mismas validaciones. **La IA nunca ejecuta cambios ni escribe datos de negocio.**

Guardar los textos que redactó la IA (a) no contradice esta regla: son textos de presentación, el comercial los puede editar y no tocan costos, margen ni precios.

### 3. Ajuste de margen (HU-013.2)

- `PUT /web/cotizaciones/{folio}/costeo/margen` `{margen, justificacion?}`. El margen es libre entre 0 y 0,99 (400 `AJUSTE_MARGEN_INVALIDO`).
- La justificación es obligatoria si el margen queda por debajo del sugerido (400 `JUSTIFICACION_REQUERIDA`).
- Solo con costo disponible (409 `COSTEO_NO_LISTO`), sin resultado registrado (409 `ESTADO_NO_PERMITE`) y sin PDF (409 `PDF_YA_GENERADO`).
- Recalcula el flete y los conexos incluidos con `IClsPrecioService`, respetando la moneda y la TRM ya fijada.
- Registra el ajuste en `MargenAjustes` (`margenPolitica` = sugerido, `margenAplicado`, `diferencia`, `usuarioId`, `motivo` = justificación). `origen_margen` pasa a `negociado`. Ajustar al mismo margen no registra un ajuste nuevo.

### 4. Servicios conexos en Nexo (HU-013.3)

- `GET /web/servicios-conexos?q=&categoria=&page=&page_size=` consulta `GET /api/servicios-conexos?soloActivos=true` y **no expone `costoUnitario`**.
- `POST /web/cotizaciones/{folio}/costeo/servicios-conexos` `{items: [{linea_orden, servicio_id, cantidad}]}`:
  1. Cotifly traduce `linea_orden` a la línea de la cotización de Nexo (`lineaId`).
  2. Llama a `POST /api/cotizaciones/{id}/conexos` con `{items: [{lineaId, servicioConexoId, cantidad}]}`, **sin `valorAplicado`**: Nexo lo calcula y es la fuente de verdad del costo.
  3. Lee de nuevo la cotización, aplica el margen y deja el conexo incluido.
- Roles de Nexo para agregar: `Comercial`, `CoordinadorFletes` y `Admin` (confirmado en el código de Nexo según `docs/HU-005.1-mapeo-campos-nexo.md`; el swagger no lo declara). 403 → `NEXO_SIN_PERMISO`; 400/404 → `CONEXO_INVALIDO`; caído → `NEXO_NO_DISPONIBLE`.
- Las licitaciones no admiten conexos en esta iteración: su PDF de pliego no los imprime (ADR-017 §6).

### 5. Re-solicitar el costeo (HU-013.4)

- "Cotizar costo" significa volver a pedir el costeo a Nexo: `POST .../costeo/re-solicitar` `{motivo, datos_carga?}`. Amplía `reintentar`, que se conserva por compatibilidad.
- Permitido con `estado_costeo` en `costeo_vencido`, `entregada` o `error_envio`, y sin resultado. En otro caso, 409 `ESTADO_NO_PERMITE`.
- **Sin cambios de carga:** se reenvía la misma cotización (mismo folio) y responde 200.
- **Con cambios de carga:** se crea la versión `{folio base}-vN` (ADR-011) con el formulario de la vigente más los cambios. La versión anterior se conserva sin tocar, y la respuesta es 201 con `Location`.
- Origen, destino, cliente y línea no se cambian por esta vía: eso es otra cotización.
- Se audita con el motivo y el usuario. El mecanismo de Nexo para "reenviar la misma" (captura nueva o reabrir) está pendiente de confirmar.

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

- El script `db/migrations_mssql/0006_textos_y_concurrencia.sql` (HU-013.5, lo aplica el DBA) agrega `Cotizaciones.rowVersion` (`ROWVERSION`) y `Cotizaciones.textosCotizacion` (JSON).
- `GET .../costeo` devuelve `ETag` y `row_version`.
- Las mutaciones del costeo (`margen`, `servicios-conexos` PUT y POST, `re-solicitar`, `textos`, `textos/redactar` y `pdf`) aceptan `If-Match`. Si no coincide, responden 409 `CONFLICTO_CONCURRENCIA` y no cambian nada.
- `If-Match` es opcional: sin él no hay control. Antes del `0006` el encabezado se acepta y se ignora. El polling también actualiza `rowVersion`, así que una acción propuesta queda invalidada si el costeo cambió después de proponerla.

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

## Alternativas consideradas

- **Mantener el chat como cotizador con más validaciones:** el LLM seguiría decidiendo cuándo calcular y generar el PDF, y el chat no recoge el formulario ampliado de Nexo. Se descarta: la fuente de verdad es el formulario.
- **Que la IA ejecute las acciones con confirmación textual ("¿confirmas?" → "sí"):** la confirmación quedaría en manos del modelo, que interpreta el "sí". Con el botón Confirmar, la ejecución es del frontend sobre un endpoint determinista, auditable igual que un clic normal.
- **Guardar los conexos solo en Cotifly con un costo del catálogo:** duplicaría la fuente de verdad del costo. Nexo puede tener tarifas por ruta o revisión de Servicios Logísticos. Se agregan en Nexo y Cotifly solo aplica el margen.
- **`If-Match` obligatorio (428 sin él):** rompería al frontend actual y a las pruebas antes del `0006`. Se deja opcional y se recomienda siempre.
- **412 Precondition Failed para el conflicto:** es el código HTTP estándar, pero el frontend ya maneja los errores de negocio como 409 `ErrorNegocio`. Se usa 409 `CONFLICTO_CONCURRENCIA` (decisión del usuario).

## Consecuencias

- El PDF ya no depende de la IA: con la IA caída la cotización se entrega igual.
- El asistente es más simple: solo lee y propone, sin slot-filling de creación. ADR-004 queda limitado a la sesión por folio.
- El frontend cambia:
  - el formulario lleva `moneda`;
  - el detalle lleva botones de margen, conexos, "Cotizar costo" y textos;
  - el asistente se abre desde una cotización y muestra la acción propuesta con Confirmar;
  - se retira la vista "Asistente" para crear (HU-013.8).
- `RespuestaChat.precio`, `margen` y `pdf` quedan en `null` desde HU-013.6. Se conservan en el esquema por compatibilidad.
- El contrato ya declara los endpoints nuevos (`x-estado: pendiente`). La prueba de contrato los lista con su issue en `clsContratoRutasTests.PendientesDeImplementar` y `PropiedadesPendientes`; cada issue quita su entrada al implementar. Los `.feature` nuevos van con `@pendiente @ignore` hasta que su issue enlace los pasos.
- Las métricas de ajustes de margen (ADR-016) ganan los ajustes hechos desde el costeo.

## Pendientes

| Pendiente | Dueño |
|---|---|
| ¿El conexo agregado queda costeado de inmediato o lo revisa Servicios Logísticos? ¿Se puede agregar a una cotización `Entregada`? | Equipo de Nexo |
| Mecanismo para re-solicitar sin cambios de carga (captura nueva, reabrir la solicitud o recalcular) | Equipo de Nexo |
| Autenticación de servicio para Cotifly (hoy `X-Portal-Usuario-Id` del comercial, sin token) | Duban + equipo de Nexo |
| Orden de las líneas de la cotización de Nexo frente a las de la solicitud (para traducir `linea_orden` → `lineaId`) | Equipo de Nexo |
| Script `0006_textos_y_concurrencia.sql` (rowVersion y textosCotizacion) | Duban + DBA |
| Integrar #50 (PR #96) antes de HU-013.7 | Duban |
| ¿Permitir regenerar el PDF tras ajustar el margen o los textos? (hoy, versión nueva) | Negocio |
| Issue de gaps del formulario y migración de catálogos (`docs/HU-013-preguntas-nexo.md` §Gaps) | Coordinador + Duban |
| Compartir el contrato con el desarrollador del frontend | Duban |
