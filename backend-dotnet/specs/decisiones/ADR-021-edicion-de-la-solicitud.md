# ADR-021 — Edición de la solicitud por el comercial (HU-028)

- **Estado:** Aceptada (2026-10-19, decisiones del usuario sobre el diseño de la opción D). Issue #186 (HU-028).
- **Complementa:** ADR-018 §5 (re-solicitud) y §11 (regla del PDF) y ADR-019 (rutas y camiones). `datos_carga` de la re-solicitud
  queda obsoleto en favor del endpoint de edición.
- **Fuente:** diseño de la edición de la solicitud (opción D, combinada por estado) del 2026-10-19 y las decisiones del usuario que lo
  ajustan (esta ADR manda sobre el diseño donde difieren).

## Contexto

El líder técnico pidió que el comercial pueda editar una solicitud ya creada. Hasta HU-028:

- las rutas, mercancías y condiciones de una solicitud guardada eran inmutables en el dominio;
- lo más cercano era `re-solicitar` con `nueva_version` y `datos_carga`, que solo admitía **una** ruta (409 `VARIAS_RUTAS`) y tenía tres
  defectos: perdía `codigo_mercancia` (la `-vN` caía en el respaldo de un camión sin vehículo), llevaba las ciudades de devolución y
  levante sin id de municipio, y no volvía a cubicar cuando cambiaba la clase de viaje;
- NEXO no permite modificar una solicitud capturada, pero sí descartarla con `POST /api/solicitudes/{id}/rechazar` (204, 404
  `SOLICITUD_NO_ENCONTRADA`, 409 `SOLICITUD_YA_DESCARTADA`), y Cotifly no la usaba: `re-solicitar` en el mismo folio dejaba la solicitud
  anterior viva y huérfana en la bandeja de la Mesa.

## Decisión

### Qué se edita

Todo menos:

- **el cliente** (`nit_cliente`, `cliente_nombre`, `origen_cliente`): forma parte del folio y del margen de SAP;
- **el `tipo`** (spot/licitación): no tiene sentido cambiarlo;
- **la línea de negocio de cada ruta**. Una ruta **nueva** solo puede usar una línea que ya exista en la solicitud.

Un intento de cambiar un campo bloqueado responde **400 `CAMPO_NO_EDITABLE`** con `detalle.campo` (el primero), `detalle.campos` y
`detalle.errores`. Si el campo llega con el valor guardado se acepta (el frontend reenvía el formulario entero).

### Contrato

- `GET /web/cotizaciones/{folio}/solicitud`: el modelo editable completo por ruta (municipios por id y nombre, mercancías con
  `codigo_mercancia`, conexos con el id del catálogo de NEXO, condiciones, margen), los bloqueados, el modo que se aplicaría y sus avisos,
  y el `ETag`. El JSON sirve tal cual como cuerpo del `PUT`.
- `PUT /web/cotizaciones/{folio}/solicitud`: **reemplazo completo** con la forma de la captura más `linea_solicitud_id` por ruta.
  `If-Match` obligatorio (428 / 409); `Idempotency-Key` opcional (ámbito `editar_solicitud` por comercial; solo consumen la clave las
  ediciones aplicadas sin `error_envio`); `modo_esperado` opcional (409 `MODO_CAMBIO` si el estado cambió desde el `GET`). Responde el
  modo aplicado, el folio resultante y los avisos. Solo el dueño (una ajena es 404).

### Modo según el estado

| Modo | Cuándo | Qué hace |
|---|---|---|
| `en_sitio` | Nunca llegó a NEXO (`error_envio`, incluido el rechazo definitivo; `pendiente_envio` abandonado) | Aplica la edición, guarda con el control de versión y envía |
| `reemplazo` | En NEXO, sin entregar al cliente y sin resultado (también `pendiente_costeo`, con el aviso `PIERDE_TRABAJO_MESA`) | Reserva la versión (con la instantánea del costeo anterior en la auditoría), **rechaza la solicitud anterior en NEXO** y solo si quedó sin vida aplica la edición, invalida el PDF y captura de nuevo en el **mismo folio** |
| `nueva_version` | Entregada al cliente o con resultado | Crea la `-vN` con lo editado y el margen negociado de las rutas que se conservan. La original no cambia (solo reserva su versión) y **no** se rechaza en NEXO: es la que tiene el cliente |
| no permitida | Licitación, cotización del chat, envío o cubicación en curso | 409 `ESTADO_NO_PERMITE` con `detalle.motivo` (`LICITACION`, `SIN_FORMULARIO`, `ENVIO_EN_CURSO`, `CUBICACION_EN_CURSO`) |

**Rechazo en NEXO.** Motivo fijo "Edición de la solicitud desde Cotifly" (1-100) y comentario con el folio y el motivo del comercial.
204 → `descartada`; 409 `SOLICITUD_YA_DESCARTADA` → `ya_descartada` (se continúa, como pidió el usuario); 404
`SOLICITUD_NO_ENCONTRADA` → `no_encontrada` (se continúa: NEXO no tiene una solicitud viva, que es lo que la regla protege). Cualquier
otro fallo (5xx, red, tiempo agotado, otro 4xx) **no** captura otra solicitud: 503 `NEXO_NO_DISPONIBLE` (403 `NEXO_SIN_PERMISO`) con
`detalle.motivo = RECHAZO_NEXO_FALLIDO`; la versión ya subió con la reserva, así que el comercial recarga y reintenta.

### Rutas, margen y camiones

- Una ruta con `linea_solicitud_id` es la misma ruta: conserva su id, su línea de negocio y su **margen negociado** (decisión 3). Sin id
  es una ruta nueva (toma el margen sugerido al enviarse). Las guardadas que no vienen se quitan; sus ajustes de margen quedan en
  `MargenAjustes` con `lineaSolicitudId = NULL` y conservan cotización y folio (aviso `MARGEN_RUTA_QUITADA`).
- Las rutas que se conservan **no se reordenan** (`RUTAS_REORDENADAS`); las nuevas van en cualquier posición. Así los cambios de `orden`
  son monótonos y EF Core los ordena sin violar `uq_cotizacionLineasSolicitud_cotizacionId_orden` (un intercambio sería un ciclo). Las
  mercancías se actualizan en su lugar por la misma razón (`uq_cotizacionMercancias_lineaSolicitudId_orden`).
- Se reutilizan la validación y la preparación de la captura (`clsCapturaRutasService`: catálogos de NEXO, municipios, mercancías del
  cubicador, conexos, y el planificador con el cupo del cubicador y su respaldo). Solo se vuelve a cubicar una ruta nueva o una cuyo
  peso, mercancías o **clase de viaje** cambió (`clsReglaRecubicacion`, que corrige el defecto C); las demás conservan sus camiones
  vigentes, también el vehículo que la Mesa haya cambiado en NEXO (D2: la Mesa manda). El 202 de "Calcular cubicaje" no aplica: la
  edición es síncrona como la captura.
- Los conexos quedan exactamente los de la edición, con el costo unitario del catálogo de NEXO de hoy.
- `moneda` ausente conserva la guardada; si cambia (o en la `-vN`), se fija la TRM del día antes de escribir.
- Sin cambios reales: 200 con `sin_cambios = true` y no se toca nada (ni NEXO, ni el PDF, ni la versión).

### Licitaciones (fase 2)

No se editan en esta fase: 409 `ESTADO_NO_PERMITE` con `LICITACION` y el `GET` lo anuncia (`permitida: false`). La fase 2 reutilizará
la cubicación en segundo plano de HU-023.5 (una licitación de muchas rutas no cabe en el presupuesto de una petición).

### `re-solicitar` con `datos_carga`

Se corrigen los defectos A, B y C (código y nombre de la mercancía, ids de las ciudades del contenedor y la clase de viaje en la regla de
recubicación) y `datos_carga` queda **obsoleto** (`deprecated` en el contrato y un aviso `CAMPO_OBSOLETO` en el log). **No se delega**
en el servicio de edición: su semántica es otra (cambios parciales de una sola carga que siempre crean `-vN`, con `CotizacionResumen`
como respuesta) y la usan el frontend actual y la acción propuesta `re_solicitar_costeo` del asistente; delegarla cambiaría su contrato.

### Base de datos

**Sin script.**

- No se agregó `solicitudNexoIdReemplazada`: el orden "reservar → rechazar → aplicar" deja el id anterior en `solicitudNexoId` hasta que
  NEXO confirma el rechazo. Si el rechazo falla, nada se aplica y el reintento vuelve a rechazar. Si el proceso cae entre el rechazo y la
  edición, el polling ve la solicitud descartada (409 `SOLICITUD_DESCARTADA`) y el comercial vuelve a editar: el rechazo responde
  `ya_descartada` y la edición sigue. Si el polling guarda entre el rechazo y la edición (conflicto de versión), se recarga y se aplica una
  vez más mientras siga siendo la misma solicitud.
- El historial del PDF reutiliza el motivo `re_solicitud` (el CHECK `ck_cotizacionPdfsHistorial_motivo` no cambia).
- `MargenAjustes.lineaSolicitudId` ya es anulable: la aplicación lo pone en `NULL` en la misma transacción que quita la ruta.

## Consecuencias

- El comercial corrige cualquier solicitud sin crear otra cotización mientras no la haya entregado; nunca quedan dos solicitudes vivas
  en NEXO por una edición.
- NEXO cuenta el reemplazo como un "rechazo del comercial" en su bandeja (riesgo semántico del diseño, D5): el motivo lo distingue.
  Verificar en QA que el rol de Cotifly en NEXO puede rechazar sus propias solicitudes.
- El trabajo de la Mesa sobre la solicitud anterior se pierde en el reemplazo (aviso en el `GET` y en la respuesta).
- `GET .../nueva-version` y `datos_carga` quedan para compatibilidad hasta que el frontend use "Editar solicitud".
- Pendiente (fase 2): edición de licitaciones; reordenar rutas que se conservan.
