# Diccionario de datos — Base de datos Bot Cotizador (Cotifly)

Complemento de [`../BASE_DE_DATOS.md`](../BASE_DE_DATOS.md) (que explica motor, capas de
acceso y cómo conectarse). Este documento va **columna por columna**: qué significa cada
una, de dónde sale el valor y un ejemplo real.

## 1. Para qué sirve esta base de datos

Es la BD de **trazabilidad** del Bot Cotizador, no el ERP. Registra todo lo que pasa
alrededor de una cotización de flete: quién la pidió, con qué costo/margen/precio se
calculó, si se ajustó el margen a mano, si se envió por correo y si el negocio se
concretó. SAP TM (márgenes) y el tarifario de Google Drive (costos) son **fuentes
externas** que el bot consulta en el momento — sus datos no viven aquí, solo queda la
foto de lo que se usó en cada cotización (`fuente_tarifa`, `origen_margen`).

Motor: **PostgreSQL 16**. Acceso: SQLAlchemy 2.x + Alembic (`shared/models.py` = tablas,
`shared/repos.py` = toda lectura/escritura, `migrations/versions/` = historial versionado).

> **Actualizado por HU-002.2 (2026-09-22):** se ejecutó la propuesta de reestructuración
> aprobada en HU-002.1 (migración `0006_hu002_2_esquema_completo`). Cambios de fondo: se
> eliminaron `payload`, `contexto`, `origen`, `destino`, `fuente_tarifa`, y el `estado`
> único de `cotizaciones` se dividió en `estado_costeo`/`estado_envio`; se agregaron FKs
> reales (`eventos_conversacion→sesiones`, `linea_negocio→margenes_politica`,
> `usuario_id→usuarios_web`); `usuarios_web` y `margenes_politica` se rediseñaron. Varias
> columnas nuevas (`costeo_id`, `bd_cliente`, `linea_negocio_id`, `estado_costeo`,
> `usuario_id`, `confirmado_por`) **existen pero nadie las llena todavía** — dependen de
> HU-005 (Nexo) o HU-004 (ClientePro). Consecuencia directa: `POST /web/login` quedó
> desactivado (sin `email`/`nombre` en `usuarios_web` no hay cómo identificar a nadie), y
> con él `mis-cotizaciones`, `enviar` y `seguimiento` de la Consola web. Cotizar por Google
> Chat no se vio afectado. Detalle completo de la decisión:
> `docs/HU_Backlog_Cotifly_ClientePro.md` (`[HU-002.2]`).

## 2. Cómo se llena (flujo de una cotización)

1. El comercial escribe en la Consola web (`/web`) o en Google Chat.
2. El agente llama en orden a las herramientas del MCP: `buscar_tarifa` (costo, desde
   Drive) → `obtener_margen` (desde SAP TM o, si no hay dato, desde `margenes_politica`)
   → `calcular_precio` (aritmética determinista, no la hace el LLM).
3. Con esos resultados arma un payload de cotización y llama `generar_pdf`: ahí se
   **inserta la fila en `cotizaciones`** (folio, costo, márgenes, precio, PDF, etc.) — es
   el único punto de escritura de esa tabla.
4. Si el comercial cambia el margen que propuso la política/SAP, se llama
   `registrar_ajuste_margen` → inserta en `ajustes_margen`.
5. Al confirmar el envío, `enviar_correo` inserta en `correos_enviados` y marca
   `cotizaciones.estado = "enviada"`.
6. En paralelo, cada turno de conversación se guarda en `eventos_conversacion` y cada
   llamada a una herramienta del MCP en `eventos_herramienta` (auditoría técnica, vía
   middleware — no depende de que el agente "decida" guardarlo).
7. `sesiones` trackea el hilo (activa/archivada); `usuarios_web` se llena al hacer login
   en la Consola.
8. `margenes_politica` y `mapeo_trqtype` **no las llena el bot**: son configuración
   sembrada por una migración (`0002_seed_config`) y se edita a mano en BD cuando
   Gerencia Comercial valide los valores definitivos.

## 3. Tablas

### 3.1 `cotizaciones` — el registro central

> **HU-002.2:** eliminadas `payload`, `origen`, `destino`, `fuente_tarifa`. `estado` se
> dividió en `estado_costeo` (Text, sin poblar — HU-005) y `estado_envio` (Text, reemplaza
> el uso de envío del `estado` anterior). Nuevas sin poblar: `costeo_id`, `bd_cliente`,
> `linea_negocio_id` (FK a `margenes_politica.id`, HU-005). `linea_negocio` ahora tiene FK
> real a `margenes_politica.linea_negocio`. `comercial_email` → `usuario_id` (Integer, FK a
> `usuarios_web.id`, sin poblar hasta HU-004 — `generar_pdf` ya no tiene de dónde sacar
> este dato con el login desactivado). `efectiva`/`seguimiento_nota`/
> `seguimiento_actualizado` → `ganada`/`motivo_rechazo`/`marcado_at` (migración `0007`,
> misma semántica boolean, sin poblar hasta que HU-005 construya el botón y sincronice
> con el `Resultado` de Nexo).


Una fila por cotización generada (PDF emitido). Se llena **una sola vez** al llamar
`generar_pdf` (`mcp_server/server.py`); las columnas de seguimiento (`efectiva`, …) se
actualizan después desde la Consola web.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico interno. | Generado por Postgres (`gen_random_uuid()`). | `3f1a2b7c-9e21-4d3a-8c11-5a0e2f9d1b44` |
| `folio` | text (único) | Identificador de negocio de la cotización. | Campo `referencia` del payload que arma el agente; si ya existe se versiona a `-v2`, `-v3`… (`CotizacionRepo.folio_versionado`). | `PCN-19032026` |
| `linea_negocio` | text | Línea de negocio de Botero Soto que atiende la ruta. | Campo `linea_negocio` (o `linea`/`nombre`) del payload, escrito por el agente según lo que dijo el comercial. | `Nacional` |
| `cliente_nombre` | text | Nombre del cliente que recibe la cotización. | Campo `cliente` del payload (si es texto, se usa tal cual; si es objeto `{nombre, id}`, se toma `nombre`). | `LONGYIN` |
| `cliente_id_sap` | text | Código del cliente en SAP (para cruzar el margen real). | `cliente_id_sap` / `cliente_id` del payload, o `cliente.id` si el cliente vino como objeto. | `100234` |
| `origen` | text | Ciudad/lugar donde inicia el transporte. | Campo `tarifa.origen` del payload (la ciudad que se pasó a `buscar_tarifa`). | `YUMBO` |
| `destino` | text | Ciudad/lugar de entrega. | Primer renglón de `tarifa.filas[0].destino`, o `tarifa.destino` si no hay filas. | `BUN` |
| `costo` | numeric | Costo de la tarifa encontrada (antes de margen). | Resultado de `buscar_tarifa`, que el agente copia al campo `costo` del payload. | `3000000` |
| `margen_politica` | numeric | Margen que proponía la política/SAP **antes** de cualquier ajuste manual. | Resultado de `obtener_margen` (decimal, `13.5% → 0.135`). | `0.15` |
| `margen_aplicado` | numeric | Margen que realmente se usó para calcular el precio (puede ser el mismo que `margen_politica` o uno negociado). | Valor que el comercial confirmó; si difiere del de política, además genera una fila en `ajustes_margen`. | `0.15` |
| `metodo_calculo` | text | Fórmula usada para pasar de costo a precio: `margen` (precio = costo/(1-m)) o `markup` (precio = costo\*(1+m)). | Campo `metodo_calculo` del payload (default `"margen"` en la plantilla de la skill). | `margen` |
| `precio_final` | numeric | Precio de venta que se le cotiza al cliente. | Resultado de `calcular_precio`, copiado por el agente a `precio_final` (o `precio`) del payload. | `3529411.76` |
| `moneda` | text | Moneda del precio. | Fija en `"COP"` (hardcodeado; el proyecto aún no cotiza en otra moneda). | `COP` |
| `fuente_tarifa` | text | De dónde salió el costo: `drive_flat_files` (hoy, Excel en Drive) o `api_fletes` (a futuro). | Campo `fuente` que devuelve `buscar_tarifa`, copiado al payload como `fuente_tarifa`. | `drive_flat_files` |
| `origen_margen` | text | De dónde salió el margen: `sap_tm` (SAP tenía el cliente) o `politica_linea` (fallback a `margenes_politica`). | Campo `origen` que devuelve `obtener_margen`. | `politica_linea` |
| `payload` | jsonb | Snapshot completo del payload de cotización (todo lo que armó el agente: contactos, filas de tarifa, notas, etc.), para reconstruir el PDF o auditar qué se envió. | El mismo dict `cotizacion` que llega a `generar_pdf`. | `{"referencia": "PCN-19032026", "cliente": "LONGYIN", "tarifa": {...}, ...}` |
| `pdf_path` | text | Ruta/clave donde quedó guardado el PDF (volumen local o bucket S3/MinIO según `PDF_STORAGE_MODO`). | Devuelto por el almacén de PDFs (`construir_almacen().guardar(...)`) al persistir el archivo. | `pdfs/PCN-19032026.pdf` |
| `estado` | text | Ciclo de vida de la cotización: `generada` (recién creado el PDF) → `enviada` (correo confirmado) → `reenviada` (se reenvió). | `"generada"` al crear la fila; se sobreescribe a `"enviada"` cuando `enviar_correo` confirma el envío (`orquestador/web.py`). | `enviada` |
| `comercial_email` | text | Correo del comercial que generó la cotización (identidad real, no la que diga el cliente en el chat). | Campo `comercial.email` (o `correo`) del payload — viene del login (token) o del remitente de Chat. | `nvargas@boterosoto.com.co` |
| `space_id` | text | Espacio de Google Chat (o `web/<id>` si vino de la Consola) donde se generó. | Lo inyecta el Orquestador en cada llamada al MCP. | `spaces/AAAA1234` |
| `thread_id` | text | Hilo de conversación dentro del space. | Igual que `space_id`, inyectado por el Orquestador. | `threads/BBBB5678` |
| `efectiva` | boolean (nullable) | Seguimiento comercial: ¿el negocio se concretó? `NULL` = aún sin marcar. | Lo marca el comercial desde "Mis cotizaciones" en la Consola (`POST /web/cotizaciones/{folio}/seguimiento`). | `true` |
| `seguimiento_nota` | text | Nota libre sobre el seguimiento (ej. motivo de por qué no se concretó). | Mismo endpoint de seguimiento, campo `nota`. | `Cliente aplazó el despacho a abril` |
| `seguimiento_actualizado` | timestamptz | Cuándo se marcó/actualizó el seguimiento. | `datetime.now(UTC)` en el momento del `POST` de seguimiento. | `2026-09-18 14:32:10+00` |
| `created_at` | timestamptz | Cuándo se generó la cotización. | `now()` de Postgres al insertar. | `2026-03-19 10:05:22+00` |
| `updated_at` | timestamptz | Última vez que se modificó la fila (p. ej. al marcar `estado="enviada"` o el seguimiento). | `now()` de Postgres, con `onupdate` automático en cualquier `UPDATE`. | `2026-03-19 10:07:40+00` |

### 3.2 `ajustes_margen` — auditoría de desvíos

> **HU-002.2:** `comercial_email` → `usuario_id` (Integer, FK a `usuarios_web.id`, sin
> poblar hasta HU-004).


Una fila **cada vez** que un comercial negocia un margen distinto al que propuso la
política/SAP. La llena la herramienta `registrar_ajuste_margen`. Es la base de la vista
`v_ajustes_margen_por_comercial` que usa Gerencia para auditar.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del ajuste. | `gen_random_uuid()`. | `9a12...` |
| `cotizacion_id` | UUID (FK → `cotizaciones.id`) | A qué cotización pertenece el ajuste (si ya existe). | Parámetro `cotizacion_id` de la herramienta; puede ser `NULL` si el ajuste se registra antes de generar el PDF. | `3f1a2b7c-...` |
| `folio` | text | Folio de la cotización, para trazar aunque no haya `cotizacion_id` aún. | Parámetro `folio` de la herramienta. | `PCN-19032026` |
| `margen_politica` | numeric | Margen que proponía la política/SAP. | Parámetro `margen_politica` (lo que devolvió `obtener_margen`). | `0.15` |
| `margen_aplicado` | numeric | Margen que finalmente pidió aplicar el comercial. | Parámetro `margen_aplicado` (lo que negoció el comercial con el cliente). | `0.12` |
| `diferencia` | numeric | `margen_aplicado - margen_politica`, redondeado. | Calculado en `AjusteMargenRepo.registrar` (no lo manda el agente). | `-0.03` |
| `motivo` | text | Por qué se ajustó (texto libre que el agente le pide al comercial). | Parámetro `motivo` de la herramienta. | `Cliente frecuente, negociación directa con Gerencia` |
| `comercial_email` | text | Quién hizo el ajuste. | Parámetro `comercial` de la herramienta. | `nvargas@boterosoto.com.co` |
| `created_at` | timestamptz | Cuándo se registró el ajuste. | `now()` de Postgres. | `2026-03-19 10:04:58+00` |

### 3.3 `correos_enviados` — trazabilidad de envíos

> **HU-002.2:** `confirmado_por` cambió de texto (el correo) a Integer (FK a
> `usuarios_web.id`, sin poblar hasta HU-004) — mismo nombre. El envío real (remitente,
> `Reply-To` del correo al cliente) sigue usando el correo tal cual, sin cambios; solo
> cambió lo que queda persistido en esta tabla.


Un registro por cada **intento** de envío del PDF al cliente (exitoso o fallido). La
llena la herramienta `enviar_correo`. `CorreoRepo.duplicado_reciente` la consulta antes
de enviar, para no mandar el mismo correo dos veces en 5 minutos.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del envío. | `gen_random_uuid()`. | `7c44...` |
| `cotizacion_id` | UUID (FK → `cotizaciones.id`) | Cotización que se envió. | Parámetro `cotizacion_id` de la herramienta. | `3f1a2b7c-...` |
| `para` | text[] | Destinatarios principales. | Parámetro `para` (lista de correos que dio el comercial/cliente). | `{contacto@longyin.com}` |
| `cc` | text[] (nullable) | Copias. | Parámetro `cc`. | `{nvargas@boterosoto.com.co}` |
| `asunto` | text | Asunto del correo. | Parámetro `asunto`. | `Cotización PCN-19032026 — Botero Soto` |
| `message_id` | text | ID del mensaje devuelto por el proveedor de correo (WSCorreos/SMTP), para rastrear el envío en el sistema externo. | Lo devuelve el servicio de correo tras el envío exitoso. | `<20260319100530.ABCD@boterosoto>` |
| `confirmado_por` | text | Comercial que autorizó el envío (obligatorio: sin esto el MCP rechaza la herramienta). | Parámetro `confirmado_por`. | `nvargas@boterosoto.com.co` |
| `estado` | text | `enviado` o `fallido`. | `"enviado"` si el transporte no lanzó excepción; `"fallido"` si `EnvioSmtpError`. | `enviado` |
| `error` | text (nullable) | Detalle del fallo, si lo hubo. | Mensaje de la excepción `EnvioSmtpError`. | `Timeout conectando a smtp.boterosoto.com.co` |
| `created_at` | timestamptz | Cuándo se intentó el envío. | `now()` de Postgres. | `2026-03-19 10:08:15+00` |

### 3.4 `eventos_conversacion` — historial del agente

> **HU-002.2:** `usuario_email` → `usuario_id` (Integer, FK a `usuarios_web.id`, sin poblar
> hasta HU-004). Ahora tiene FK compuesta real `(space_id, thread_id) → sesiones`.


Un registro por cada **turno** (mensaje del usuario o respuesta del asistente) de una
conversación. Reconstruye el historial de un hilo (`ConversacionRepo.historial`) y
alimenta el contexto que el agente ve en el siguiente turno.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del turno. | `gen_random_uuid()`. | `1b90...` |
| `space_id` | text | Espacio de Chat (o `web/<id>`) del turno. | Lo trae el webhook de Chat o la sesión web. | `spaces/AAAA1234` |
| `thread_id` | text | Hilo dentro del espacio. | Igual que arriba. | `threads/BBBB5678` |
| `usuario_email` | text (nullable) | Quién escribió el turno (si se conoce identidad). | Token de la Consola web, o remitente del evento de Chat. | `nvargas@boterosoto.com.co` |
| `rol` | text | `user` (lo escribió una persona) o `assistant` (lo respondió el agente). | Lo fija el Orquestador según quién generó el turno. | `user` |
| `contenido` | text (nullable) | El texto del mensaje. | El mensaje tal cual llegó (o la respuesta del LLM). | `Cotízame Yumbo a Buenaventura para LONGYIN` |
| `herramientas` | jsonb (nullable) | Qué herramientas del MCP invocó el agente en ese turno (y con qué resultado resumido). | Lo arma el runner del agente al ejecutar el turno. | `{"buscar_tarifa": {"encontrada": true, "costo": 3000000}}` |
| `created_at` | timestamptz | Cuándo ocurrió el turno (define el orden cronológico del hilo). | `now()` de Postgres, o un valor explícito (`cuando`) si se re-procesa un evento con fecha propia. | `2026-03-19 10:01:00+00` |

### 3.5 `eventos_herramienta` — auditoría técnica

Un registro por **cada invocación** a una herramienta del MCP (independiente de si tuvo
éxito o no). La llena automáticamente el middleware de auditoría (`AuditoriaMiddleware`),
no depende del agente ni del negocio — es el log técnico.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del evento. | `gen_random_uuid()`. | `5e21...` |
| `herramienta` | text | Nombre de la herramienta del MCP invocada. | Nombre de la función (`buscar_tarifa`, `generar_pdf`, …). | `obtener_margen` |
| `input` | jsonb (nullable) | Parámetros con los que se llamó la herramienta. | Argumentos recibidos por el `@mcp.tool`. | `{"linea_negocio": "Nacional", "cliente_id": "100234"}` |
| `output_resumen` | jsonb (nullable) | Resultado resumido de la herramienta. | Lo que devolvió la función (o el error estructurado). | `{"margen": 0.15, "origen": "politica_linea"}` |
| `exito` | boolean | Si la herramienta terminó sin lanzar excepción. | Capturado por el middleware alrededor de la llamada. | `true` |
| `duracion_ms` | int | Cuánto tardó la llamada. | Cronometrado por el middleware. | `142` |
| `thread_id` | text (nullable) | Hilo asociado (para cruzar con `eventos_conversacion`). | Parámetro `thread_id` que reciben casi todas las herramientas. | `threads/BBBB5678` |
| `created_at` | timestamptz | Cuándo se invocó. | `now()` de Postgres. | `2026-03-19 10:01:03+00` |

### 3.6 `sesiones` — estado de la conversación

> **HU-002.2:** columna `contexto` (JSONB) eliminada — nunca se leía. `estado` ahora tiene
> `CHECK (estado IN ('activa','archivada'))`.


PK compuesta (`space_id`, `thread_id`): una fila por hilo. Se crea la primera vez que
llega un mensaje de ese hilo (`SesionRepo.obtener_o_crear`) y se archiva por un barrido
de inactividad (`SESION_TTL_HORAS`).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `space_id` | text (PK) | Espacio de Chat/web. | Igual que en `eventos_conversacion`. | `spaces/AAAA1234` |
| `thread_id` | text (PK) | Hilo dentro del espacio. | Igual que arriba. | `threads/BBBB5678` |
| `estado` | text | `activa` o `archivada`. | `"activa"` al crear; pasa a `"archivada"` cuando el barrido de inactividad la toca. | `activa` |
| `contexto` | jsonb (nullable) | Estado interno de la ventana deslizante de contexto del agente (qué recuerda de la conversación). | Lo actualiza el runner del agente en cada turno. | `{"ultima_herramienta": "calcular_precio"}` |
| `ultima_actividad` | timestamptz | Última vez que hubo un turno en el hilo. | `datetime.now(UTC)` cada vez que se "toca" la sesión (`SesionRepo.tocar`). | `2026-03-19 10:08:20+00` |

### 3.7 `usuarios_web` — comerciales de la Consola

> **HU-002.2 — rediseño completo:** la PK deja de ser `email` (Text) y pasa a ser `id`
> (Integer, el mismo id de SAP que entregará ClientePro/HU-004, no autoincremental).
> Se agregó `telefono`. Se eliminaron `email` y `nombre`, y la tabla se vació (`TRUNCATE`)
> porque ninguna fila existente tenía un id de SAP real que asignarle. **Consecuencia:**
> `POST /web/login` quedó desactivado (responde 501) y con él `GET /web/mis-cotizaciones`,
> `POST /web/cotizaciones/{folio}/enviar` y `POST /web/cotizaciones/{folio}/seguimiento` —
> todos dependían de identificar al comercial por `email`. Cotizar por Google Chat, que
> usa la identidad propia de Google (no este login), no se vio afectado.


Identifica a quien usa el front web (`/web`). No hay contraseña: se crea/actualiza en
cada login válido (`UsuarioWebRepo.upsert`).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `email` | text (PK) | Correo corporativo del comercial (identidad). | Campo `email` del `POST /web/login`, validado contra `@boterosoto.com.co`. | `nvargas@boterosoto.com.co` |
| `nombre` | text (nullable) | Nombre para mostrar en la Consola. | Campo `nombre` opcional del login (se actualiza si llega uno nuevo). | `Nathaly Vargas Garcia` |
| `created_at` | timestamptz | Primera vez que este correo hizo login. | `now()` de Postgres, en el primer `upsert`. | `2026-02-01 08:00:00+00` |
| `ultima_sesion` | timestamptz (nullable) | Último login. | `datetime.now(UTC)` en cada `upsert` (login nuevo o repetido). | `2026-03-19 09:58:00+00` |

### 3.8 `margenes_politica` — configuración: margen por línea

> **HU-002.2:** se agregó `id` (Integer serial, **nueva PK**) e `is_active` (Boolean,
> default `true`). `linea_negocio` deja de ser PK y pasa a `UNIQUE` — `cotizaciones`
> referencia esta tabla tanto por `linea_negocio` (FK directa, texto) como por la nueva
> `linea_negocio_id` (FK a `id`, sin poblar hasta HU-005). El estado *provisional* de los
> valores (15%/50% ITR) sigue igual — HU-002.1 decidió no bloquear el cierre con la
> validación de Gerencia Comercial, se ajustará desde el panel admin cuando exista.


**No la llena el bot.** Es la política de margen por defecto (fallback cuando SAP TM no
tiene el cliente). Se sembró con la migración `0002_seed_config`; se edita a mano en BD
cuando Gerencia Comercial valide los valores reales (hoy son **provisionales**, todas al
15%).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `linea_negocio` | text (PK) | Línea de negocio. | Sembrado por la migración `0002_seed_config`; editable a mano. | `Nacional` |
| `margen` | numeric | Margen a aplicar, en decimal (`0.15` = 15%). | Igual. | `0.15` |
| `metodo_calculo` | text | Fórmula por defecto para esa línea (`margen` o `markup`). | Igual. | `margen` |
| `updated_at` | timestamptz | Última vez que se ajustó el valor. | `now()` de Postgres, con `onupdate` automático. | `2026-07-16 00:00:00+00` |

### 3.9 `mapeo_trqtype` — configuración: TrqType SAP ↔ línea de negocio

**Tampoco la llena el bot.** Traduce el `TrqType` de SAP TM a la línea de negocio interna,
para saber qué política de margen le corresponde. PK compuesta (`trq_type`,
`sales_org`); `sales_org=''` es el comodín "aplica a todas las organizaciones de venta".

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo |
|---|---|---|---|---|
| `trq_type` | text (PK) | Código de tipo de transporte en SAP TM. | Sembrado por `0002_seed_config`; editable a mano. | `OPTN` |
| `sales_org` | text (PK) | Organización de ventas SAP a la que aplica (`''` = todas). | Igual. | `` (vacío = comodín) |
| `linea_negocio` | text | Línea de negocio interna equivalente. | Igual. | `Nacional` |
| `activo` | boolean | Si el mapeo está vigente (permite desactivar sin borrar histórico). | Igual, default `true`. | `true` |

## 4. Vistas de solo lectura (no son tablas, se recalculan al consultarlas)

Creadas por SQL directo en Alembic, sin modelo ORM propio — pensadas para Gerencia y para
la sincronización a Google Sheets (`shared/sheets_sync.py`, tablero Looker Studio):

- **`v_ajustes_margen_por_comercial`**: agrega `ajustes_margen` por comercial (`usuario_id`
  desde HU-002.2) + línea (número de ajustes, diferencia promedio/mín/máx, último ajuste).
- **`v_actividad_por_comercial`**: actividad agregada por comercial (`usuario_id` desde
  HU-002.2; cotizaciones totales/enviadas vía `estado_envio`, ajustes, última actividad).

> `v_trazabilidad_cotizaciones` se eliminó en HU-002.2: sin consumidor de código, y
> dependía de `origen`/`destino`/`fuente_tarifa`, ya eliminadas de `cotizaciones`.

```sql
SELECT * FROM v_ajustes_margen_por_comercial ORDER BY num_ajustes DESC;
```

## 5. Referencias

- Motor, cómo conectarse local/producción, backup/restore: [`../BASE_DE_DATOS.md`](../BASE_DE_DATOS.md).
- Modelos ORM: `shared/models.py`. Consultas/escrituras: `shared/repos.py`.
- Dónde se escribe cada tabla en el flujo de negocio: `mcp_server/server.py` (herramientas
  `generar_pdf`, `registrar_ajuste_margen`, `enviar_correo`) y `orquestador/web.py` (login,
  seguimiento).
