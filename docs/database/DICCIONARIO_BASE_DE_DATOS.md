# Diccionario de datos — `dbo_cotifly` (SQL Server)

**Vigente desde HU-021. Actualizado en HU-021.14 (2026-10-13).**

Qué significa cada columna de las tablas de cotizaciones, quién la escribe y quién la lee. Los tipos, nulos, restricciones e
índices están en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](ESQUEMA_OBJETIVO_SQLSERVER.md) (Parte A, con el diagrama ER); la decisión
de diseño, en [ADR-019](../../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md). Fuente de verdad:
`db/scripts/01_crear_esquema.sql` (= `0001` a `0010`) y el modelo EF
(`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/Configurations/`).

> La versión anterior de este diccionario describía la base **Postgres** del bot en Python y quedó al final como
> [anexo obsoleto](#anexo--diccionario-de-la-base-postgres-obsoleto). HU-022.1 (#146) decide si se borra.

## 1. Para qué sirve esta base

Es la base de **trazabilidad** de Cotifly, no el ERP. Guarda la cotización completa: quién la pidió, qué rutas y camiones
tiene, qué costó cada camión en Nexo, con qué margen se le puso precio a cada ruta, qué PDF y qué correo salieron, y si el
negocio se ganó. Nexo (costos y conexos), SAP TM (margen) y el cubicador (vehículos) son fuentes externas: de ellas queda
solo la foto de lo que se usó.

## 2. Cómo se llena una cotización

1. **Captura** (`POST /web/solicitudes-costeo` o licitación por Excel): se crea la cabecera (`CotizacionesSolicitud`) con
   una fila por ruta (`CotizacionLineasSolicitud`), sus mercancías y sus conexos. El cubicador recomienda los camiones de
   cada ruta y se crea una fila por camión (`CotizacionVehiculosLineaSolicitud`). Sin cubicador, la ruta queda con un
   camión sin vehículo que resuelve la Mesa de Costeo de Nexo. Cubica el sistema donde nace la solicitud: si nace en
   Cotifly, Cotifly llama al cubicador; Nexo no vuelve a cubicar lo que le llega de Cotifly.
2. **Envío a Nexo:** una línea de Nexo por camión. La cabecera guarda `solicitudNexoId`.
3. **Polling:** empareja cada camión con su línea de Nexo (`lineaSolicitudNexoId`), guarda su flete, retorno y estado, y
   la ruta recalcula sus totales y su precio. Los conexos se envían a Nexo una sola vez, pegados al primer camión vigente
   de la ruta (decisión 10); el registro de cada envío va en `EventosHerramienta`.
4. **Ajustes del comercial** (margen por ruta, conexos, levante, textos): suben `versionNegocio`, recalculan la ruta y la
   cabecera y siguen la regla del PDF (ADR-018). El ajuste de margen queda en `MargenAjustes` con su ruta.
5. **PDF y correo:** `pdfPath`, `CotizacionPdfsHistorial`, `Correos` y `CorreoDestinatarios`.
6. **Resultado:** `ganada`, `motivoRechazo`, `marcadoAt`.

## 3. `CotizacionesSolicitud` — la cabecera

Una fila por cotización (y por cada versión `-vN`). Antes del `0009` se llamaba `Cotizaciones` y tenía también los datos de
la línea; ahora esos datos son de la ruta.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno | Captura | Todo el agregado |
| `folio` | Identificador de negocio, único: `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` (ADR-011, decisión 11) | Captura, con el candado `sp_getapplock` | API, PDF, correo, asistente |
| `clienteNombre`, `nitCliente`, `origenCliente` | Cliente (o prospecto) | Captura (búsqueda de clientes en Nexo) | PDF, Nexo, folio |
| `tipoSolicitud` | `spot` (formulario) o `licitacion` (Excel) | Captura | Envío a Nexo, PDF |
| `deadline` | Fecha límite que pide el cliente | Captura | Nexo, métricas |
| `notas` | Nota para la Mesa de Nexo (no sale en el PDF) | Captura | Nexo (al enviar, Cotifly le suma en el mensaje cuántas líneas son de cada ruta) |
| `moneda`, `trmAplicada` | Moneda de la cotización y TRM usada (0 en COP). Es de la cabecera: todas las rutas usan la misma (ADR-014) | Captura, re-solicitud, `-vN` | Cálculo del precio de cada ruta, PDF |
| `precioFinal` | Σ `precioFinalLinea` de las rutas, con conexos. NULL mientras alguna ruta no tenga precio | Dominio (recalculo) | "Mis cotizaciones", PDF, métricas |
| `estadoCosteo` | Resumen del estado de las rutas ("el más atrasado") | Dominio | API, polling, métricas |
| `motivoEstadoCosteo` | Explicación del estado (por ejemplo `SOLICITUD_NEXO_DESCONOCIDA`) | Envío, polling | API |
| `solicitudNexoId` | Id de la **solicitud** de Nexo (lo devuelve el alta; lo consulta el polling). Antes `costeoId` (0010) | Envío a Nexo | Polling, resultado |
| `cotizacionNexoId` | Id de la **cotización** de Nexo | Polling | Conexos, resultado en Nexo |
| `costeoEnviadoAt`, `costeoSiguienteConsultaAt`, `costeoConsultasFallidas`, `costeoListoAt` | Agenda y seguimiento del polling | Envío, polling | Worker del polling |
| `versionNegocio` | Versión de negocio: viaja como `ETag` y vuelve en `If-Match` (428/409) | Cada cambio que ve el comercial y el polling cuando cambia algo | Concurrencia optimista (ADR-018) |
| `version` | Número de versión del folio (`-vN`) | Nueva versión | Folio, asistente |
| `pdfPath`, `pdfDesactualizado` | PDF vigente y si un cambio lo invalidó | Generación del PDF; mutaciones | PDF, correo |
| `textosCotizacion` | JSON con asunto e introducción del PDF (IA con respaldo estándar) | Redacción (ADR-018) | PDF, correo |
| `estadoEnvio` | `sin_enviar`, `enviada`, `reenviada` | Correo | Regla del PDF, métricas, vistas |
| `ganada`, `motivoRechazo`, `marcadoAt` | Resultado del negocio | Seguimiento | Métricas |
| `usuarioId` | Comercial dueño | Captura | Autorización, métricas |
| `spaceId`, `threadId` | Hilo de origen (legado de Google Chat, retirado en #105) | Solo la cotización legada del chat (`clsGenerarPdfService`, apagada por defecto con `Pdf:PermitirCotizacionChat`) | Lectura de cotizaciones antiguas |
| `createdAt`, `updatedAt` | Marcas de tiempo | Interceptor de EF | Listados |

**Retiradas por el `0009` o la decisión 1:** `referencia`, `valorEstimado`, `costeoId` (vuelve como `solicitudNexoId`),
`costeoUltimaConsultaAt`, los campos de la línea (origen, destino, vehículo, costo, margen, línea de negocio) y el JSON
`serviciosConexos`.

## 4. `CotizacionLineasSolicitud` — la ruta

Una fila por ruta que pidió el comercial (una fila del Excel en una licitación). Tiene lo que es igual para todos sus
camiones, los totales de costo, el margen y el precio.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `cotizacionSolicitudId`, `orden` | Cabecera y posición de la ruta (1..N) | Captura | API (`linea_orden`), PDF |
| `rutaOrigenIndice` | Posición en el formulario o fila del Excel (base 0) | Captura | Reporte de errores de la licitación, Excel exportado |
| `lineaNegocioId` | Línea de negocio de la ruta (`MargenesPolitica`). Cada ruta puede tener la suya | Captura | Margen de política, `lineaNegocio` de cada camión en Nexo, métricas por ruta |
| `origenMunicipioId`, `destinoMunicipioId` | Id del municipio en Nexo (`tbl_municipios`), obligatorio en toda captura nueva (decisión 2) | Captura (catálogo `GET /web/municipios`) | Envío a Nexo |
| `origenNombre`, `destinoNombre` | Copia del nombre del catálogo, para mostrar y para que la cotización no cambie si cambia el catálogo (0010) | Captura | PDF, emparejamiento, asistente |
| `ciudadDevolucionMunicipioId` / `Nombre`, `ciudadLevanteMunicipioId` / `Nombre` | Ciudades del contenedor, id + nombre (decisión 2) | Captura | Nexo, PDF |
| `tipologia`, `claseViaje`, `claseContenedor`, `exclusivo`, `consolidado`, `retornaContenedor`, `levanteContenedorDiferente` | Condiciones del viaje y del contenedor (catálogos de Nexo) | Captura | Envío a Nexo |
| `horasCargue`, `horasDescargue` | Horas de la ruta (0 a 240) | Captura | Nexo |
| `pesoKg` | Peso total declarado de la ruta | Captura | Cubicador, respaldo de un camión |
| `valorMercancia` | Valor de la mercancía (para el seguro; no es costo) | Captura | Nexo |
| `valorLevanteContenedor` | Un levante; lo digita el comercial y se copia a cada camión (decisión 4) | Captura, `CambiarValorLevante` | Camiones |
| `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal` | Suma de los camiones que cuentan | Dominio (polling) | Precio |
| `costoServiciosConexos` | Σ precio × cantidad de los conexos | Dominio | Precio |
| `costoFinal` | Fletes + retorno + levante + conexos | Dominio | Precio, métricas |
| `margenPoliticaSugerido`, `margenSAPSugerido` | Margen sugerido por la política de la línea y por SAP TM (fracción). El vigente es el de SAP si existe | Envío (resolución del margen) | Ajuste de margen, asistente |
| `margenAplicado`, `origenMargen`, `metodoCalculo` | Margen con el que se calcula el precio, de dónde salió (`sap_tm`, `politica_linea`, `negociado`) y el método (`margen` o `markup`) | Envío; ajuste por ruta (decisiones 6 y 9) | Precio, métricas |
| `precioFinalLinea` | `costoFinal` con el margen, en la moneda de la cabecera | Dominio | PDF, cabecera, métricas |
| `estadoCosteo` | Resumen del estado de sus camiones | Dominio | Cabecera, API |

## 5. `CotizacionVehiculosLineaSolicitud` — el camión

Una fila por camión. Si el cubicador recomienda 7 camiones para una ruta, son 7 filas. Nexo costea cada camión por separado.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId` | Ruta del camión | Captura | Agregado |
| `idRecomendacionVehiculo` | Fila de la recomendación del cubicador; NULL si no salió de una | Captura (cubicador) | Trazabilidad con el cubicador |
| `tipoVehiculo`, `carroceria` | Código del vehículo (por ejemplo `3S3`) y carrocería. Los define el cubicador o la Mesa de Costeo de Nexo, no el comercial (decisión 7). NULL = sin selección automática | Captura; polling (si la Mesa los pone o cambia) | Envío a Nexo, emparejamiento, PDF |
| `pesoKg` | Peso total del camión | Captura | Nexo, emparejamiento |
| `costoFlete`, `costoRetornoContenedor` | Lo que cobra Nexo por el camión | Polling | Totales de la ruta |
| `costoLevanteContenedor` | Copia del levante de la ruta | Captura, `CambiarValorLevante` | Totales de la ruta |
| `lineaSolicitudNexoId` | Línea de Nexo emparejada con este camión | Polling (emparejamiento) | Polling, envío de conexos |
| `estadoCosteo` | Estado del camión (los 7 estados). `descartada` = la Mesa borró su línea; no cuenta en totales ni se reenvía | Envío, polling | Resumen de la ruta |
| `createdAt` | Además de la marca de tiempo, lleva el orden de la recomendación (`ahora + orden × 1 ms`) | Interceptor de EF | Orden de los camiones |

## 6. `CotizacionMercancias` — lo que se va a mandar (ex `CotizacionPosiciones`)

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId`, `orden` | Ruta y posición | Captura | Agregado |
| `idTipoMercancia` | Tipo de mercancía del catálogo del cubicador (`GET /web/tipos-mercancia`). Obligatorio en toda captura nueva | Captura | Cubicador |
| `nombreMercancia` | Copia del nombre del catálogo (si el catálogo no responde, el que mandó el cliente) | Captura | PDF, asistente |
| `cantidad`, `largoCm`, `anchoCm`, `altoCm`, `pesoKg` | Unidades y medidas | Captura | Cubicador; posiciones hacia Nexo cuando la ruta tiene un solo camión |

> **HU-023 (pendiente):** `db/scripts/02_alter_cubicador.sql` cambiará `idTipoMercancia` por `codigoMercancia` y agregará
> las columnas de resultado de la cubicación. Todavía no está en esta rama.

## 7. `CotizacionServiciosConexos` — conexos de la ruta

Son de la ruta, no del camión: la cantidad ya dice cuántos. Reemplazan el JSON `serviciosConexos` (decisión 3).

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId` | Ruta | Captura, ajustes | Agregado |
| `idServicioConexo` | Id del catálogo de Nexo (capturados o copiados a una `-vN`) o id de la fila de la cotización de Nexo (agregados o importados desde Nexo). Único por ruta | Captura, `POST`/`PUT .../servicios-conexos`, polling (#124) | Envío diferido a Nexo (`clsResolutorConexosCatalogo`) |
| `nombreServicioConexo` | Copia del nombre (0010) | Igual | PDF |
| `precio` | Costo unitario de Nexo al momento de capturar, antes del margen. No se vuelve a cotizar | Captura (catálogo de Nexo), `-vN` (catálogo refrescado) | Totales y precio de la ruta |
| `cantidad` | Cantidad | Captura, `PUT` | Totales, Nexo |

Quitar un conexo borra su fila (no hay columna `incluido`). El estado del envío a Nexo (`pendiente`, `enviado`, `incierto`,
`fallido_definitivo`) no tiene columna: se lee de los registros `conexos_envio_nexo` de `EventosHerramienta`.

## 8. Otras tablas

| Tabla | Qué guarda |
|---|---|
| `MargenAjustes` | Cada ajuste de margen de un comercial: `margenPolitica` (el sugerido de la ruta), `margenAplicado`, `diferencia`, `motivo` y la ruta (`lineaSolicitudId`). Base de `vAjustesMargenPorComercial` y de las métricas por ruta |
| `CotizacionArchivosLicitacion` | El Excel original del pliego (hasta 10 MB), con su SHA-256 y la hoja usada |
| `CotizacionPdfsHistorial` | Los PDF anteriores de una cotización y por qué se invalidaron (`ajuste_margen`, `servicios_conexos`, `textos`, `re_solicitud`) |
| `Correos`, `CorreoDestinatarios` | Cada envío del PDF al cliente, su estado y sus destinatarios (`para`, `cc`) |
| `EventosHerramienta` | Auditoría de herramientas y registros de proceso: `conexos_envio_nexo`, `conexos_pendientes_nexo`, `cambiar_servicios_conexos` (JSON en `input`/`outputResumen`, sin montos al asistente) |
| `EventosConversacion`, `Sesiones` | Historial y estado del asistente por folio |
| `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` | Identidad: usuarios de ClientePro, rol interno, refresh tokens (hash) y access tokens revocados |
| `MargenesPolitica` | Líneas de negocio, margen de política y nombre equivalente en Nexo (`mapeoLineasNexo`). Desde el `0009`: Nacional, Distribucion, Importacion, Exportacion, Internacional y DevolucionContenedor (salen ITR, Almacenamiento y Almacenamiento3PL) |
| `SapMapeoTrqType` | TrqType de SAP TM → línea de negocio. Falta el de `DevolucionContenedor` (lo da negocio o SAP) |

---

# Anexo — Diccionario de la base Postgres (OBSOLETO)

> **OBSOLETO.** Todo lo que sigue describe la base **PostgreSQL** del bot en Python (SQLAlchemy y Alembic, tablas en
> `snake_case`). Esa base ya no la usa el backend .NET y Alembic se borró en HU-021.2. Lo vigente está arriba y en
> [`ESQUEMA_OBJETIVO_SQLSERVER.md`](ESQUEMA_OBJETIVO_SQLSERVER.md). HU-022.1 (#146) decide si este anexo se borra.

**Título original:** Diccionario de datos — Base de datos Cotifly (Postgres)

Complemento de [`../../BASE_DE_DATOS.md`](../../BASE_DE_DATOS.md) (que explica motor, capas de
acceso y cómo conectarse). Este documento va **columna por columna**: qué significa cada
una, de dónde sale el valor, un ejemplo real, **si algo la vuelve a leer** y **qué
constraints tiene hoy en la BD real** (verificado contra Postgres, no solo contra el
código declarado).

> Nota de alcance (HU-002): este documento es solo **inventario y evidencia** (qué existe,
> de dónde sale, si se lee, qué constraints tiene). No incluye veredictos de "eliminar /
> conectar / agregar FK" ni la evaluación de seguridad de cada FK candidata — esa parte se
> arma aparte, en la propuesta de HU-002, a partir de esta evidencia.

### 1. Para qué sirve esta base de datos

Es la BD de **trazabilidad** de Cotifly, no el ERP. Registra todo lo que pasa
alrededor de una cotización de flete: quién la pidió, con qué costo/margen/precio se
calculó, si se ajustó el margen a mano, si se envió por correo y si el negocio se
concretó. SAP TM (márgenes) y el tarifario de Google Drive (costos) son **fuentes
externas** que el bot consulta en el momento — sus datos no viven aquí, solo queda la
foto de lo que se usó en cada cotización (`fuente_tarifa`, `origen_margen`).

Motor: **PostgreSQL 16**. Acceso: SQLAlchemy 2.x + Alembic (`shared/models.py` = tablas,
`shared/repos.py` = toda lectura/escritura, `db/migrations/versions/` = historial versionado).

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
> `docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md` (`[HU-002.2]`).

### 2. Cómo se llena (flujo de una cotización)

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

### 3. Tablas

Cada tabla trae ahora, además de significado/origen:

- **¿Se lee en código?** — si algo (endpoint, herramienta MCP, `sheets_sync.py`, vista SQL)
  vuelve a consultar esa columna después de escribirla, y dónde. Verificado por búsqueda
  de uso en `orquestador/`, `mcp_server/` y `shared/` (no en `tests/`, que no cuenta como
  consumo real). Valores usados: **Sí** (con dónde), **No** (se escribe pero nada la
  vuelve a leer en producción) y **Solo vistas/Gerencia** (solo la consulta una vista SQL
  o un `psql` manual, ningún endpoint de la app la expone).
- **Constraints reales** — verificado contra la base viva (`\d+ <tabla>` en
  `docker compose exec postgres psql`, 2026-09-21), no solo lo declarado en
  `shared/models.py`. Se anota cuando algo declarado en el ORM no llegó a crearse en la
  BD real.

#### 3.1 `cotizaciones` — el registro central

> **HU-002.2:** eliminadas `payload`, `origen`, `destino`, `fuente_tarifa`. `estado` se
> dividió en `estado_costeo` (Text, sin poblar — HU-005) y `estado_envio` (Text, reemplaza
> el uso de envío del `estado` anterior). Nuevas sin poblar: `costeo_id`, `bd_cliente`.
> `linea_negocio_id` (FK a `margenes_politica.id`) **sí está poblada** (corrección
> 2026-09-22, migración `0008`): no dependía de Nexo, sale de un cruce local contra
> `margenes_politica` (sembrada desde `0002`). `linea_negocio` conserva su FK real
> a `margenes_politica.linea_negocio`. `comercial_email` → `usuario_id` (Integer, FK a
> `usuarios_web.id`, sin poblar hasta HU-004 — `generar_pdf` ya no tiene de dónde sacar
> este dato con el login desactivado). `efectiva`/`seguimiento_nota`/
> `seguimiento_actualizado` → `ganada`/`motivo_rechazo`/`marcado_at` (migración `0007`,
> misma semántica boolean, sin poblar hasta que HU-005 construya el botón y sincronice
> con el `Resultado` de Nexo).


Una fila por cotización generada (PDF emitido). Se llena **una sola vez** al llamar
`generar_pdf` (`mcp_server/server.py`); las columnas de seguimiento (`efectiva`, …) se
actualizan después desde la Consola web.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico interno. | Generado por Postgres (`gen_random_uuid()`). | `3f1a2b7c-9e21-4d3a-8c11-5a0e2f9d1b44` | Sí — `enviar_cotizacion` (`orquestador/web.py`) y como `cotizacion_id` en `generar_pdf`/`consultar_cotizacion`. |
| `folio` | text (único) | Identificador de negocio de la cotización. | Campo `referencia` del payload que arma el agente; si ya existe se versiona a `-v2`, `-v3`… (`CotizacionRepo.folio_versionado`). | `PCN-19032026` | Sí — lookup por folio, respuesta de casi todas las herramientas/endpoints, `sheets_sync.py`. |
| `linea_negocio` | text | Línea de negocio de Botero Soto que atiende la ruta. | Campo `linea_negocio` (o `linea`/`nombre`) del payload, escrito por el agente según lo que dijo el comercial. | `Nacional` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`. |
| `cliente_nombre` | text | Nombre del cliente que recibe la cotización. | Campo `cliente` del payload (si es texto, se usa tal cual; si es objeto `{nombre, id}`, se toma `nombre`). | `LONGYIN` | Sí — mismos 3 puntos anteriores. |
| `cliente_id_sap` | text | Código del cliente en SAP (para cruzar el margen real). | `cliente_id_sap` / `cliente_id` del payload, o `cliente.id` si el cliente vino como objeto. | `100234` | Sí, pero **solo** en `sheets_sync.py` (columna "Cliente ID SAP" del tablero). No aparece en `consultar_cotizacion` ni en `/web/mis-cotizaciones`. |
| `origen` | text | Ciudad/lugar donde inicia el transporte. | Campo `tarifa.origen` del payload (la ciudad que se pasó a `buscar_tarifa`). | `YUMBO` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`. |
| `destino` | text | Ciudad/lugar de entrega. | Primer renglón de `tarifa.filas[0].destino`, o `tarifa.destino` si no hay filas. | `BUN` | Sí — mismos 3 puntos. |
| `costo` | numeric | Costo de la tarifa encontrada (antes de margen). | Resultado de `buscar_tarifa`, que el agente copia al campo `costo` del payload. | `3000000` | Sí — `consultar_cotizacion`, `sheets_sync.py` (utilidad/margen real). **No** en `/web/mis-cotizaciones`. |
| `margen_politica` | numeric | Margen que proponía la política/SAP **antes** de cualquier ajuste manual. | Resultado de `obtener_margen` (decimal, `13.5% → 0.135`). | `0.15` | Sí — `consultar_cotizacion`, `sheets_sync.py`. **No** en `/web/mis-cotizaciones`. |
| `margen_aplicado` | numeric | Margen que realmente se usó para calcular el precio (puede ser el mismo que `margen_politica` o uno negociado). | Valor que el comercial confirmó; si difiere del de política, además genera una fila en `ajustes_margen`. | `0.15` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`, KPI "margen aplicado promedio". |
| `metodo_calculo` | text | Fórmula usada para pasar de costo a precio: `margen` (precio = costo/(1-m)) o `markup` (precio = costo\*(1+m)). | Campo `metodo_calculo` del payload (default `"margen"` en la plantilla de la skill). | `margen` | Sí, pero **solo** en `sheets_sync.py`. No se devuelve en ningún endpoint/herramienta de consulta. |
| `precio_final` | numeric | Precio de venta que se le cotiza al cliente. | Resultado de `calcular_precio`, copiado por el agente a `precio_final` (o `precio`) del payload. | `3529411.76` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`, KPIs (ticket promedio, utilidad total). |
| `moneda` | text | Moneda del precio. | Fija en `"COP"` (hardcodeado; el proyecto aún no cotiza en otra moneda). | `COP` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`. |
| `fuente_tarifa` | text | De dónde salió el costo: `drive_flat_files` (hoy, Excel en Drive) o `api_fletes` (a futuro). | Campo `fuente` que devuelve `buscar_tarifa`, copiado al payload como `fuente_tarifa`. | `drive_flat_files` | Sí, pero **solo** en `consultar_cotizacion` y `sheets_sync.py`. No en `/web/mis-cotizaciones`. |
| `origen_margen` | text | De dónde salió el margen: `sap_tm` (SAP tenía el cliente) o `politica_linea` (fallback a `margenes_politica`). | Campo `origen` que devuelve `obtener_margen`. | `politica_linea` | Sí, mismos 2 puntos que `fuente_tarifa`. |
| `payload` | jsonb | Snapshot completo del payload de cotización (todo lo que armó el agente: contactos, filas de tarifa, notas, etc.), para reconstruir el PDF o auditar qué se envió. | El mismo dict `cotizacion` que llega a `generar_pdf`. | `{"referencia": "PCN-19032026", "cliente": "LONGYIN", "tarifa": {...}, ...}` | **No** — ningún endpoint, herramienta ni `sheets_sync.py` la vuelve a leer. Solo aparece en `tests/` (roundtrip). |
| `pdf_path` | text | Ruta/clave donde quedó guardado el PDF (volumen local o bucket S3/MinIO según `PDF_STORAGE_MODO`). | Devuelto por el almacén de PDFs (`construir_almacen().guardar(...)`) al persistir el archivo. | `pdfs/PCN-19032026.pdf` | Sí, indirectamente — se comprueba `if c.pdf_path` para armar `pdf_url` (`consultar_cotizacion`, `/web/mis-cotizaciones`), pero el valor crudo nunca se expone. |
| `estado` | text | Ciclo de vida de la cotización: `generada` (recién creado el PDF) → `enviada` (correo confirmado) → `reenviada` (se reenvió). | `"generada"` al crear la fila; se sobreescribe a `"enviada"` cuando `enviar_correo` confirma el envío (`orquestador/web.py`). | `enviada` | Sí — `consultar_cotizacion`, `/web/mis-cotizaciones`, `sheets_sync.py`, las 3 vistas SQL. **Nota:** el valor `"reenviada"` está documentado en el comentario del modelo pero ningún punto del código lo asigna hoy (solo `"generada"`/`"enviada"`). |
| `comercial_email` | text | Correo del comercial que generó la cotización (identidad real, no la que diga el cliente en el chat). | Campo `comercial.email` (o `correo`) del payload — viene del login (token) o del remitente de Chat. | `nvargas@boterosoto.com.co` | Sí — filtro de `/web/mis-cotizaciones` y `enviar_cotizacion` (verifica dueño), `consultar_cotizacion`, `sheets_sync.py`, las 3 vistas, e índice dedicado `ix_cotizaciones_comercial`. |
| `space_id` | text | Espacio de Google Chat (o `web/<id>` si vino de la Consola) donde se generó. | Lo inyecta el Orquestador en cada llamada al MCP. | `spaces/AAAA1234` | **No** en ningún endpoint/herramienta ni en `sheets_sync.py`. Solo la vista `v_trazabilidad_cotizaciones` la selecciona, y ese SELECT no lo ejecuta ningún código de la app (nadie consulta esa vista desde Python). |
| `thread_id` | text | Hilo de conversación dentro del space. | Igual que `space_id`, inyectado por el Orquestador. | `threads/BBBB5678` | **No** — mismo caso que `space_id`. |
| `efectiva` | boolean (nullable) | Seguimiento comercial: ¿el negocio se concretó? `NULL` = aún sin marcar. | Lo marca el comercial desde "Mis cotizaciones" en la Consola (`POST /web/cotizaciones/{folio}/seguimiento`). | `true` | Sí — `/web/mis-cotizaciones`, `sheets_sync.py` (columna "Efectividad" y KPIs de efectividad). |
| `seguimiento_nota` | text | Nota libre sobre el seguimiento (ej. motivo de por qué no se concretó). | Mismo endpoint de seguimiento, campo `nota`. | `Cliente aplazó el despacho a abril` | Sí — `/web/mis-cotizaciones`, `sheets_sync.py`. |
| `seguimiento_actualizado` | timestamptz | Cuándo se marcó/actualizó el seguimiento. | `datetime.now(UTC)` en el momento del `POST` de seguimiento. | `2026-09-18 14:32:10+00` | **No** — se escribe en `CotizacionRepo.marcar_seguimiento` pero ningún endpoint, vista ni `sheets_sync.py` la devuelve o consulta. |
| `created_at` | timestamptz | Cuándo se generó la cotización. | `now()` de Postgres al insertar. | `2026-03-19 10:05:22+00` | Sí — se usa/ordena en todos los endpoints, `sheets_sync.py` y las 3 vistas. |
| `updated_at` | timestamptz | Última vez que se modificó la fila (p. ej. al marcar `estado="enviada"` o el seguimiento). | `now()` de Postgres, con `onupdate` automático en cualquier `UPDATE`. | `2026-03-19 10:07:40+00` | **No** — ningún SELECT, vista ni reporte la consulta; solo existe como metadato automático de Postgres. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `id`. UNIQUE: `uq_cotizaciones_folio` (folio). Índices: `ix_cotizaciones_cliente` (cliente_nombre), `ix_cotizaciones_comercial` (comercial_email). Coincide con lo declarado en `shared/models.py` — sin sorpresas aquí.
- Referenciada por: `ajustes_margen.cotizacion_id` y `correos_enviados.cotizacion_id` (ambas ya son FK reales hacia `cotizaciones.id`, ver 3.2/3.3).
- Sin FK ni constraint hacia `usuarios_web.email` (para `comercial_email`) ni hacia `sesiones`/`eventos_conversacion` (para `space_id`/`thread_id`): estas 3 relaciones son hoy 100% texto libre, sin integridad referencial a nivel de BD.

#### 3.2 `ajustes_margen` — auditoría de desvíos

> **HU-002.2:** `comercial_email` → `usuario_id` (Integer, FK a `usuarios_web.id`, sin
> poblar hasta HU-004).


Una fila **cada vez** que un comercial negocia un margen distinto al que propuso la
política/SAP. La llena la herramienta `registrar_ajuste_margen`. Es la base de la vista
`v_ajustes_margen_por_comercial` que usa Gerencia para auditar.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del ajuste. | `gen_random_uuid()`. | `9a12...` | No — no la devuelve ninguna herramienta/endpoint ni la selecciona `sheets_sync.py`. |
| `cotizacion_id` | UUID (FK → `cotizaciones.id`) | A qué cotización pertenece el ajuste (si ya existe). | Parámetro `cotizacion_id` de la herramienta; puede ser `NULL` si el ajuste se registra antes de generar el PDF. | `3f1a2b7c-...` | Solo vistas/Gerencia — se usa en el `JOIN`/subquery de `v_ajustes_margen_por_comercial` y `v_trazabilidad_cotizaciones`, ningún código Python la lee directo. |
| `folio` | text | Folio de la cotización, para trazar aunque no haya `cotizacion_id` aún. | Parámetro `folio` de la herramienta. | `PCN-19032026` | Sí — `sheets_sync.py` (hoja "Ajustes_Margen"). |
| `margen_politica` | numeric | Margen que proponía la política/SAP. | Parámetro `margen_politica` (lo que devolvió `obtener_margen`). | `0.15` | Sí — `sheets_sync.py`. |
| `margen_aplicado` | numeric | Margen que finalmente pidió aplicar el comercial. | Parámetro `margen_aplicado` (lo que negoció el comercial con el cliente). | `0.12` | Sí — `sheets_sync.py`. |
| `diferencia` | numeric | `margen_aplicado - margen_politica`, redondeado. | Calculado en `AjusteMargenRepo.registrar` (no lo manda el agente). | `-0.03` | Sí — `sheets_sync.py` y la vista `v_ajustes_margen_por_comercial` (promedio/mín/máx). |
| `motivo` | text | Por qué se ajustó (texto libre que el agente le pide al comercial). | Parámetro `motivo` de la herramienta. | `Cliente frecuente, negociación directa con Gerencia` | Sí — `sheets_sync.py`. |
| `comercial_email` | text | Quién hizo el ajuste. | Parámetro `comercial` de la herramienta. | `nvargas@boterosoto.com.co` | Sí — `sheets_sync.py` y la vista `v_ajustes_margen_por_comercial` (agrupa por este campo). |
| `created_at` | timestamptz | Cuándo se registró el ajuste. | `now()` de Postgres. | `2026-03-19 10:04:58+00` | Sí — `sheets_sync.py` (orden) y la vista (`ultimo_ajuste`). |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `id`. FK real: `ajustes_margen_cotizacion_id_fkey` → `cotizaciones(id)`, **nullable** (coincide con que el ajuste se puede registrar antes de tener `cotizacion_id`). Sin índice sobre `cotizacion_id` ni sobre `comercial_email`, aunque ambas se usan para agrupar/filtrar en las vistas y en `sheets_sync.py` — a evaluar si conviene un índice (nota de rendimiento, no de esquema).

#### 3.3 `correos_enviados` — trazabilidad de envíos

> **HU-002.2:** `confirmado_por` cambió de texto (el correo) a Integer (FK a
> `usuarios_web.id`, sin poblar hasta HU-004) — mismo nombre. El envío real (remitente,
> `Reply-To` del correo al cliente) sigue usando el correo tal cual, sin cambios; solo
> cambió lo que queda persistido en esta tabla.


Un registro por cada **intento** de envío del PDF al cliente (exitoso o fallido). La
llena la herramienta `enviar_correo`. `CorreoRepo.duplicado_reciente` la consulta antes
de enviar, para no mandar el mismo correo dos veces en 5 minutos.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del envío. | `gen_random_uuid()`. | `7c44...` | No — no se devuelve ni se selecciona en ningún lado. |
| `cotizacion_id` | UUID (FK → `cotizaciones.id`) | Cotización que se envió. | Parámetro `cotizacion_id` de la herramienta. | `3f1a2b7c-...` | Sí — `CorreoRepo.duplicado_reciente` filtra por esta columna antes de cada envío, y el `JOIN` de `sheets_sync.py`/`v_trazabilidad_cotizaciones`. |
| `para` | text[] | Destinatarios principales. | Parámetro `para` (lista de correos que dio el comercial/cliente). | `{contacto@longyin.com}` | Sí — `CorreoRepo.duplicado_reciente` compara el set de destinatarios, `sheets_sync.py`. |
| `cc` | text[] (nullable) | Copias. | Parámetro `cc`. | `{nvargas@boterosoto.com.co}` | Sí — `sheets_sync.py`. |
| `asunto` | text | Asunto del correo. | Parámetro `asunto`. | `Cotización PCN-19032026 — Botero Soto` | Sí — `sheets_sync.py`. |
| `message_id` | text | ID del mensaje devuelto por el proveedor de correo (WSCorreos/SMTP), para rastrear el envío en el sistema externo. | Lo devuelve el servicio de correo tras el envío exitoso. | `<20260319100530.ABCD@boterosoto>` | Sí — se devuelve al front en `enviar_cotizacion`/`enviar_correo`, y `sheets_sync.py`. |
| `confirmado_por` | text | Comercial que autorizó el envío (obligatorio: sin esto el MCP rechaza la herramienta). | Parámetro `confirmado_por`. | `nvargas@boterosoto.com.co` | Sí — `sheets_sync.py`. |
| `estado` | text | `enviado` o `fallido`. | `"enviado"` si el transporte no lanzó excepción; `"fallido"` si `EnvioSmtpError`. | `enviado` | Sí — `CorreoRepo.duplicado_reciente` filtra `estado == "enviado"`, `sheets_sync.py` (cuenta enviados vs. total). |
| `error` | text (nullable) | Detalle del fallo, si lo hubo. | Mensaje de la excepción `EnvioSmtpError`. | `Timeout conectando a smtp.boterosoto.com.co` | Sí — `sheets_sync.py`. |
| `created_at` | timestamptz | Cuándo se intentó el envío. | `now()` de Postgres. | `2026-03-19 10:08:15+00` | Sí — ventana de 5 min en `duplicado_reciente`, orden en `sheets_sync.py` y en el `JOIN` de la vista. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `id`. FK real: `correos_enviados_cotizacion_id_fkey` → `cotizaciones(id)`, **`NOT NULL`** (a diferencia de `ajustes_margen`, aquí sí es obligatorio tener la cotización antes de registrar el correo — coherente con el flujo: no se puede enviar un correo de algo que no existe). Sin índice propio sobre `cotizacion_id` (se recorre por `cotizacion_id` en cada envío para chequear duplicados — candidato a índice por volumen, no por integridad).

#### 3.4 `eventos_conversacion` — historial del agente

> **HU-002.2:** `usuario_email` → `usuario_id` (Integer, FK a `usuarios_web.id`, sin poblar
> hasta HU-004). Ahora tiene FK compuesta real `(space_id, thread_id) → sesiones`.


Un registro por cada **turno** (mensaje del usuario o respuesta del asistente) de una
conversación. Reconstruye el historial de un hilo (`ConversacionRepo.historial`) y
alimenta el contexto que el agente ve en el siguiente turno.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del turno. | `gen_random_uuid()`. | `1b90...` | No — no se expone en `GET /sesiones/{space_id}` ni en ningún otro lado. |
| `space_id` | text | Espacio de Chat (o `web/<id>`) del turno. | Lo trae el webhook de Chat o la sesión web. | `spaces/AAAA1234` | Sí — `ConversacionRepo.historial` filtra por esta columna. |
| `thread_id` | text | Hilo dentro del espacio. | Igual que arriba. | `threads/BBBB5678` | Sí — mismo filtro. |
| `usuario_email` | text (nullable) | Quién escribió el turno (si se conoce identidad). | Token de la Consola web, o remitente del evento de Chat. | `nvargas@boterosoto.com.co` | Sí — devuelto en `GET /sesiones/{space_id}` (`orquestador/sesiones_api.py`). |
| `rol` | text | `user` (lo escribió una persona) o `assistant` (lo respondió el agente). | Lo fija el Orquestador según quién generó el turno. | `user` | Sí — mismo endpoint, y se usa para reconstruir el historial que ve el LLM en el siguiente turno. |
| `contenido` | text (nullable) | El texto del mensaje. | El mensaje tal cual llegó (o la respuesta del LLM). | `Cotízame Yumbo a Buenaventura para LONGYIN` | Sí — mismo endpoint + contexto del agente. |
| `herramientas` | jsonb (nullable) | Qué herramientas del MCP invocó el agente en ese turno (y con qué resultado resumido). | Lo arma el runner del agente al ejecutar el turno. | `{"buscar_tarifa": {"encontrada": true, "costo": 3000000}}` | Sí — mismo endpoint. |
| `created_at` | timestamptz | Cuándo ocurrió el turno (define el orden cronológico del hilo). | `now()` de Postgres, o un valor explícito (`cuando`) si se re-procesa un evento con fecha propia. | `2026-03-19 10:01:00+00` | Sí — orden en `ConversacionRepo.historial` y en el endpoint de soporte. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `id`. Índice: `ix_eventos_conv_thread` sobre (`space_id`, `thread_id`) — coincide con el filtro real de `ConversacionRepo.historial`. Sin FK hacia `sesiones(space_id, thread_id)`: la relación existe en la práctica (todo turno pertenece a una sesión) pero no está declarada como tal en la BD.

#### 3.5 `eventos_herramienta` — auditoría técnica

Un registro por **cada invocación** a una herramienta del MCP (independiente de si tuvo
éxito o no). La llena automáticamente el middleware de auditoría (`AuditoriaMiddleware`),
no depende del agente ni del negocio — es el log técnico.

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `id` | UUID (PK) | Identificador técnico del evento. | `gen_random_uuid()`. | `5e21...` | No — nadie la consulta (`AuditoriaRepo` no tiene ningún método de lectura, solo `registrar`). |
| `herramienta` | text | Nombre de la herramienta del MCP invocada. | Nombre de la función (`buscar_tarifa`, `generar_pdf`, …). | `obtener_margen` | Sí — `sheets_sync.py` (hoja "Uso_Herramientas", agrupa por esta columna). |
| `input` | jsonb (nullable) | Parámetros con los que se llamó la herramienta. | Argumentos recibidos por el `@mcp.tool`. | `{"linea_negocio": "Nacional", "cliente_id": "100234"}` | **No** — se escribe en cada llamada pero ningún endpoint, reporte ni `sheets_sync.py` la vuelve a leer. Es auditoría "de solo escritura" hoy (solo consultable a mano por psql). |
| `output_resumen` | jsonb (nullable) | Resultado resumido de la herramienta. | Lo que devolvió la función (o el error estructurado). | `{"margen": 0.15, "origen": "politica_linea"}` | **No** — mismo caso que `input`. |
| `exito` | boolean | Si la herramienta terminó sin lanzar excepción. | Capturado por el middleware alrededor de la llamada. | `true` | Sí — `sheets_sync.py` (cuenta exitosos/fallidos por herramienta). |
| `duracion_ms` | int | Cuánto tardó la llamada. | Cronometrado por el middleware. | `142` | Sí — `sheets_sync.py` (duración promedio). |
| `thread_id` | text (nullable) | Hilo asociado (para cruzar con `eventos_conversacion`). | Parámetro `thread_id` que reciben casi todas las herramientas. | `threads/BBBB5678` | **No** — se escribe siempre, pero ningún `WHERE`/reporte filtra o agrupa por `thread_id` (el cruce con `eventos_conversacion` que sugiere el comentario del modelo no está implementado). |
| `created_at` | timestamptz | Cuándo se invocó. | `now()` de Postgres. | `2026-03-19 10:01:03+00` | Sí — `sheets_sync.py` (`MAX` como "último uso"). |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `id`. Índice: `ix_eventos_herr_thread` sobre `thread_id` — **este índice hoy no tiene ninguna consulta real que lo aproveche** (ver `thread_id` arriba: se escribe pero nunca se filtra por él). Sin FK hacia `sesiones` ni `eventos_conversacion`.

#### 3.6 `sesiones` — estado de la conversación

> **Uso actual en el backend .NET (HU-013.9, #105).** Google Chat se retiró y `Sesiones` / `EventosConversacion` **no se
> borran**: guardan la auditoría de las conversaciones de Chat y siguen recibiendo las del asistente por folio.
> - `spaceId`: `spaces/<id>` en las sesiones antiguas de Google Chat (ya no se crean); `web/<sesion_id>` en la consola web.
> - `threadId`: `spaces/<id>/threads/<id>` en Google Chat; `web/<sesion_id>` en el chat legado de la consola (sin folio,
>   ya no se crean); `web/<sesion_id>/<FOLIO>` en el asistente por folio (HU-013.6), una conversación por comercial y folio.
>   `EventosHerramienta.threadId` lleva ese mismo valor para la auditoría de las herramientas del asistente.
> - `estadoAgente`: en las sesiones nuevas solo `{folioEnProgreso, cotizacionId, version, moneda}`; las antiguas conservan el
>   JSON del slot-filling legado (se lee ignorando los campos retirados y no se reescribe).
> - `EventosConversacion.rol`: `user`, `assistant` y `herramienta` (resultados de las herramientas de una iteración).


> **HU-002.2:** columna `contexto` (JSONB) eliminada — nunca se leía. `estado` ahora tiene
> `CHECK (estado IN ('activa','archivada'))`.


PK compuesta (`space_id`, `thread_id`): una fila por hilo. Se crea la primera vez que
llega un mensaje de ese hilo (`SesionRepo.obtener_o_crear`) y se archiva por un barrido
de inactividad (`SESION_TTL_HORAS`).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `space_id` | text (PK) | Espacio de Chat/web. | Igual que en `eventos_conversacion`. | `spaces/AAAA1234` | Sí — `SesionRepo.por_space`, `GET /sesiones/{space_id}`. |
| `thread_id` | text (PK) | Hilo dentro del espacio. | Igual que arriba. | `threads/BBBB5678` | Sí — lookup por PK compuesta en `obtener_o_crear`/`archivar`, devuelto en `GET /sesiones/{space_id}`. |
| `estado` | text | `activa` o `archivada`. | `"activa"` al crear; pasa a `"archivada"` cuando el barrido de inactividad la toca. | `activa` | Sí — filtro `estado == "activa"` en `SesionRepo.inactivas`, devuelto en `GET /sesiones/{space_id}`. |
| `contexto` | jsonb (nullable) | Estado interno de la ventana deslizante de contexto del agente (qué recuerda de la conversación). | Lo actualiza el runner del agente en cada turno. | `{"ultima_herramienta": "calcular_precio"}` | **No** — `SesionRepo.obtener_o_crear` la inicializa en `{}` y **nada más la vuelve a escribir ni a leer** en todo el código de producción. Es una columna declarada para un mecanismo (ventana deslizante de contexto) que hoy no está conectado. |
| `ultima_actividad` | timestamptz | Última vez que hubo un turno en el hilo. | `datetime.now(UTC)` cada vez que se "toca" la sesión (`SesionRepo.tocar`). | `2026-03-19 10:08:20+00` | Sí — filtro en `SesionRepo.inactivas` (umbral de `SESION_TTL_HORAS`), orden en `por_space`, devuelto en `GET /sesiones/{space_id}`. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK compuesta: (`space_id`, `thread_id`). Sin más índices ni constraints. Coincide con `shared/models.py`.

#### 3.7 `usuarios_web` — comerciales de la Consola

> **HU-002.2 — rediseño completo:** la PK deja de ser `email` (Text) y pasa a ser `id`
> (Integer, el mismo id de SAP que entregará ClientePro/HU-004, no autoincremental).
> Se eliminaron el `email`/`nombre` viejos (del login propio), y la tabla se vació
> (`TRUNCATE`) porque ninguna fila existente tenía un id de SAP real que asignarle.
> Se agregó `telefono`, y (migración `0009`, 2026-09-22) `nombre_usuario`/
> `nombre_completo`/`email` — mapeo de `tblSAPUsuariosApp` del diseño original (§2.6),
> construidas ya aunque sin poblar, mismo criterio que las columnas de Nexo.
> **Consecuencia:**
> `POST /web/login` quedó desactivado (responde 501) y con él `GET /web/mis-cotizaciones`,
> `POST /web/cotizaciones/{folio}/enviar` y `POST /web/cotizaciones/{folio}/seguimiento` —
> todos dependían de identificar al comercial por `email`. Cotizar por Google Chat, que
> usa la identidad propia de Google (no este login), no se vio afectado.


Identifica a quien usa el front web (`/web`). No hay contraseña: se crea/actualiza en
cada login válido (`UsuarioWebRepo.upsert`).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `email` | text (PK) | Correo corporativo del comercial (identidad). | Campo `email` del `POST /web/login`, validado contra `@boterosoto.com.co`. | `nvargas@boterosoto.com.co` | Sí — lookup por PK en `upsert` (para decidir si crea o actualiza), y se compara contra `comercial_email`/token en varios endpoints de `web.py`. |
| `nombre` | text (nullable) | Nombre para mostrar en la Consola. | Campo `nombre` opcional del login (se actualiza si llega uno nuevo). | `Nathaly Vargas Garcia` | Sí — se devuelve en la respuesta de `POST /web/login` y se incrusta en el token (`crear_token`). |
| `created_at` | timestamptz | Primera vez que este correo hizo login. | `now()` de Postgres, en el primer `upsert`. | `2026-02-01 08:00:00+00` | **No** — no aparece en ningún endpoint ni reporte. |
| `ultima_sesion` | timestamptz (nullable) | Último login. | `datetime.now(UTC)` en cada `upsert` (login nuevo o repetido). | `2026-03-19 09:58:00+00` | **No** — se actualiza en cada login pero nunca se consulta/expone (no hay, por ejemplo, un listado de "comerciales activos" que la use). |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `email`. Sin más constraints ni índices. Coincide con `shared/models.py`.

#### 3.8 `margenes_politica` — configuración: margen por línea

> **HU-002.2:** se agregó `id` (Integer serial, **nueva PK**) e `is_active` (Boolean,
> default `true`). `linea_negocio` deja de ser PK y pasa a `UNIQUE` — `cotizaciones`
> referencia esta tabla tanto por `linea_negocio` (FK directa, texto) como por la nueva
> `linea_negocio_id` (FK a `id`, poblada desde `0008` — no dependía de Nexo). El estado
> *provisional* de los valores (**15% en las 8 líneas, sin excepción** — corrección
> 2026-09-22: se había documentado por error que `ITR` estaba al 50%, verificado
> contra la BD real que no es así) sigue igual — HU-002.1 decidió no bloquear el cierre
> con la validación de Gerencia Comercial, se ajustará desde el panel admin cuando
> exista.


**No la llena el bot.** Es la política de margen por defecto (fallback cuando SAP TM no
tiene el cliente). Se sembró con la migración `0002_seed_config`; se edita a mano en BD
cuando Gerencia Comercial valide los valores reales (hoy son **provisionales**, todas al
15%).

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `linea_negocio` | text (PK) | Línea de negocio. | Sembrado por la migración `0002_seed_config`; editable a mano. | `Nacional` | Sí — lookup por PK en `MargenPoliticaRepo.obtener` (llamado por `MargenResolver.resolver` en el fallback). |
| `margen` | numeric | Margen a aplicar, en decimal (`0.15` = 15%). | Igual. | `0.15` | Sí — es el valor que efectivamente usa `MargenResolver` cuando no hay dato de SAP. |
| `metodo_calculo` | text | Fórmula por defecto para esa línea (`margen` o `markup`). | Igual. | `margen` | **No** — se sembró con valor `"margen"` para las 8 líneas, pero `MargenResolver.resolver` solo lee `pol.margen`; nunca accede a `pol.metodo_calculo`. El `metodo_calculo` que sí se aplica a una cotización viene del payload que arma el agente (columna `cotizaciones.metodo_calculo`), no de esta configuración. |
| `updated_at` | timestamptz | Última vez que se ajustó el valor. | `now()` de Postgres, con `onupdate` automático. | `2026-07-16 00:00:00+00` | **No** — no se consulta en ningún lado. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK: `linea_negocio`. Sin más constraints ni índices. Coincide con `shared/models.py`.

#### 3.9 `mapeo_trqtype` — configuración: TrqType SAP ↔ línea de negocio

**Tampoco la llena el bot.** Traduce el `TrqType` de SAP TM a la línea de negocio interna,
para saber qué política de margen le corresponde. PK compuesta (`trq_type`,
`sales_org`); `sales_org=''` es el comodín "aplica a todas las organizaciones de venta".

| Columna | Tipo | Significado | De dónde se obtiene | Ejemplo | ¿Se lee en código? |
|---|---|---|---|---|---|
| `trq_type` | text (PK) | Código de tipo de transporte en SAP TM. | Sembrado por `0002_seed_config`; editable a mano. | `OPTN` | Sí — filtro en `MapeoTrqTypeRepo.linea_de`/`trqtype_de`. |
| `sales_org` | text (PK) | Organización de ventas SAP a la que aplica (`''` = todas). | Igual. | `` (vacío = comodín) | Sí — filtro `sales_org.in_([sales_org, ""])` en ambos métodos del repo. |
| `linea_negocio` | text | Línea de negocio interna equivalente. | Igual. | `Nacional` | Sí — filtro/valor de retorno en ambos métodos del repo. |
| `activo` | boolean | Si el mapeo está vigente (permite desactivar sin borrar histórico). | Igual, default `true`. | `true` | Sí — filtro `activo.is_(True)` en ambos métodos del repo. |

**Constraints reales (Postgres, verificado 2026-09-21):**
- PK compuesta: (`trq_type`, `sales_org`). **Divergencia encontrada:** `shared/models.py` declara además `UniqueConstraint("trq_type", "sales_org", name="uq_trqtype_salesorg")` en `__table_args__`, pero esa constraint **nunca se creó en la BD real** — `db/migrations/versions/0001_initial.py` solo define la tabla con la PK compuesta, no ejecuta ningún `create_unique_constraint` para `uq_trqtype_salesorg`. Confirmado con `pg_constraint`: la tabla solo tiene `mapeo_trqtype_pkey`. En la práctica no genera ningún problema (la PK ya cubre esa unicidad), pero el modelo ORM declara algo que la migración no aplicó — el ORM y el esquema real están desalineados en este punto.

### 4. Vistas de solo lectura (no son tablas, se recalculan al consultarlas)

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

**¿Se leen en código?** `v_actividad_por_comercial` y las otras dos **no las consulta
ningún endpoint ni módulo de `orquestador/`/`mcp_server/`** — `shared/sheets_sync.py`
solo usa `v_actividad_por_comercial` (una de las tres); las otras dos
(`v_ajustes_margen_por_comercial`, `v_trazabilidad_cotizaciones`) solo aparecen
referenciadas en `DEPLOY.md` como consulta manual por `psql` para Gerencia, y en un test
de humo (`tests/test_despliegue.py`) que solo verifica que la vista responda. Es decir,
2 de las 3 vistas dependen hoy de que alguien las corra a mano.

### 5. Referencias

- Motor, cómo conectarse local/producción, backup/restore: [`../../BASE_DE_DATOS.md`](../../BASE_DE_DATOS.md).
- Modelos ORM: `shared/models.py`. Consultas/escrituras: `shared/repos.py`.
- Dónde se escribe cada tabla en el flujo de negocio: `mcp_server/server.py` (herramientas
  `generar_pdf`, `registrar_ajuste_margen`, `enviar_correo`) y `orquestador/web.py` (login,
  seguimiento).
