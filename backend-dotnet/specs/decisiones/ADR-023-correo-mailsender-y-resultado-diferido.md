# ADR-023 — Correo por MailSender, total sin desglose y resultado diferido a NEXO (HU-033)

- **Estado:** Aceptada (2026-10-22, decisiones del líder técnico). Issue #199 (HU-033). Completa #11 (correo) y #179 parte D.
- **Reemplaza:** el transporte `smtp_relay` (MailKit directo al relay) de #11 y los modos `smtp` y `wscorreos` (nunca implementados). En
  el PDF, la regla de HU-021 "tarifa = transporte y conexos aparte con su precio". En `/resultado`, el 409 `COTIZACION_NO_ENTREGADA` cuando
  NEXO todavía no entregó (HU-005.14).
- **Se mantiene:** la deduplicación de 5 minutos, la reserva del estado de envío con versión de negocio (ADR-018 §12), la auditoría de
  `enviar_correo`, la "perdida solo en Cotifly" antes del costeo (HU-013.17, #127) y el rechazo de líneas (HU-028.1, #183).

## Contexto

El correo se envía desde #11 por un relay SMTP interno (MailKit, puerto 25, sin AUTH), pero nunca se activó: faltaba confirmar el
transporte (D3), autorizar la IP de cada nodo Swarm en el relay y no había ninguna protección para QA (con `Correo:Habilitado=true` un
correo de pruebas podía llegar a un cliente). Entretanto se construyó el microservicio interno **MailSender** (`InnovacionIA/MailSender`,
.NET 10 + PostgreSQL + Hangfire), que encola el correo, lo entrega por el mismo relay corporativo y lo reintenta.

Contrato real de MailSender (leído del código, solo lectura):

- `POST /api/v1/emails` (multipart/form-data): `From`, `To[]`, `Cc[]`, `Bcc[]`, `Subject`, `Body`, `IsHtml`, `Priority`, `Metadata`,
  `Attachments[]`. Responde **202** `{id, status: 1, statusUrl}`; **422** (`ValidationProblemDetails`) si `from` falta o no es un correo,
  no hay ningún destinatario, una dirección pasa de 320 caracteres, `subject` falta o pasa de 998, `body` falta o `metadata` no es JSON;
  **400** si `priority` no es `Low`/`Normal`/`High`; **413** si un adjunto pasa de `MAX_ATTACHMENT_MB` (25 MB por defecto) o la suma de
  `MAX_TOTAL_MESSAGE_MB` (50 MB; el cuerpo HTTP admite 8 MB más). Header opcional `X-Correlation-ID` (se recorta a 128).
- **`From`:** cualquier dirección válida (`MailAddress`); no hay lista blanca: decide el relay (SPF/DMARC). Es también el remitente del
  sobre. **No hay `Reply-To`**. El cuerpo va en una sola parte (`IsHtml` elige texto o HTML).
- `GET /api/v1/emails/{id}` → `EmailStatusResponse` con `status` **numérico**: 0 `Draft`, 1 `Queued`, 2 `Processing`, 3 `Sent`,
  4 `RetryScheduled`, 5 `Failed`, 6 `Cancelled` (reservado), más `lastError`, `attemptCount`, `sentAt`; 404 si no existe.
- `GET /api/v1/emails/{id}/attempts` y `POST /api/v1/emails/{id}/retry` (202; 404 si no existe; un correo `Sent` se ignora).
- Reintentos propios: solo errores transitorios (4xx SMTP, red, timeout), hasta `RETRY_MAX_ATTEMPTS` (5), con espera exponencial de 30 s a
  15 min. Un 550 5.7.1 "Unable to relay" es permanente (`Failed`).
- Sin autenticación (red interna). `Metadata` es JSON libre (columna `jsonb`).

## Decisión

### 1. Transporte: MailSender (único)

- `clsTransporteMailSender` (HttpClient tipado, nombre `MailSender`) implementa `IClsTransporteCorreo` (encolar) e
  `IClsSeguimientoCorreoExterno` (consultar y reintentar). `Correo:Modo=mailsender` es el valor por defecto y el único válido con el correo
  habilitado; `Correo:MailSender:UrlBase` es obligatoria (solo en configuración; la de QA vive en Dokploy), con `TimeoutSegundos` (15),
  `Remitente` (`From` de respaldo) y `Prioridad` (`Normal`).
- Resiliencia: tiempo total y por intento = `TimeoutSegundos`; 2 reintentos **solo en métodos seguros** (el GET del estado): el POST no se
  reintenta porque MailSender no tiene clave de idempotencia y un reintento podría duplicar el correo; circuito. Un tiempo agotado en el POST
  se informa como incierto ("pudo quedar encolado: revisa el historial antes de reenviar").
- `X-Correlation-ID` = `{folio}:{id del envío en Cotifly}`; `Metadata` = `{origen, folio, correo_id, cotizacion_id}`.
- **`smtp_relay` (MailKit) se retira**, junto con `smtp` y `wscorreos`: (a) la issue #199 decide que MailSender reemplaza al SMTP directo;
  (b) MailSender ya hace el relay, los reintentos y su propio registro, y es el único que necesita la IP autorizada en el relay (no cada
  nodo de Cotifly); (c) mantener dos transportes con modelos distintos (síncrono y asíncrono) duplica la matriz de pruebas y deja un camino
  que nadie opera. Con el correo **deshabilitado** un `Correo__Modo` heredado (`smtp_relay` en los stacks) no impide arrancar; habilitado,
  sí. Si algún día hiciera falta volver, el transporte está en el historial de git (#11) detrás de la misma interfaz.

### 2. Estado asíncrono

- El 202 deja la fila de `Correos` en **`encolado`** con `idMailSender` y la consulta agendada (`siguienteConsultaAt`, script 13). El
  worker `clsWorkerEstadoCorreos` (mismo patrón que el polling del costeo: agenda, reclamo por fila para varias réplicas, un ámbito de DI
  por correo) consulta `GET /emails/{id}` cada 30 s: `Queued`/`Draft` → `encolado`; `Processing`/`RetryScheduled` → `enviando` (con el
  `lastError` del intento transitorio); `Sent` → `enviado` (`enviadoAt`); `Failed`/`Cancelled` → `fallido` (con `lastError`). Un 404 es
  `fallido` ("MailSender ya no conoce este correo"). Sin confirmación en 24 h → `fallido` (reintentable). MailSender caído: se reprograma
  sin tocar el estado.
- **Reintentos:** los hace MailSender. Cotifly expone `POST /web/cotizaciones/{folio}/correos/{correoId}/reintentar` → `/retry`, solo para
  el dueño y solo para un `fallido` que MailSender conoce (`CORREO_NO_REINTENTABLE` si no). Vuelve a `encolado` y el worker lo sigue.
- **Historial:** `GET /web/cotizaciones/{folio}/correos` (dueño).
- **`estado_envio` de la cotización** cambia cuando MailSender **acepta** el correo (con la reserva de ADR-018 §12, que bloquea los ajustes
  desde ese momento). Un `fallido` posterior **no** lo revierte: el comercial reintenta el mismo correo y el PDF que salió sigue siendo la
  foto entregada. Un rechazo al encolar (400/413/422, caída o tiempo agotado) sí deshace la reserva y deja una fila `fallido` sin
  `idMailSender` (no reintentable: se envía de nuevo).
- **Deduplicación de 5 minutos:** cuenta un envío `enviado`, `encolado` o `enviando` a los mismos `para`; un `fallido` no, salvo el
  **incierto** (auditoría #199): un tiempo agotado en el POST deja la fila `fallido` con el error `Resultado incierto: …`, que SÍ cuenta,
  y **conserva** la reserva del envío (el correo pudo salir; el PDF es la foto entregada).
- **Auditoría #199:** el reintento reserva la fila (`fallido` → `encolado` en una sentencia) antes del `/retry` y la libera si MailSender no
  lo acepta: un doble clic o dos réplicas piden un solo reintento (MailSender correría dos entregas a la vez). La edad máxima de la consulta
  se mide desde `Correos.encoladoAt` (la última aceptación: envío o reintento), no desde `createdAt`: si no, reintentar un correo de más
  de 24 h lo devolvía a `fallido` en la primera consulta. Cotifly valida una sola dirección por destinatario (MailSender separa `,`/`;`).

### 3. Protección fuera de producción

- **Segura por defecto (auditoría #199):** se redirige SIEMPRE salvo con las dos llaves juntas `Correo:Proteccion:Modo=ninguno` y
  `Correo:Proteccion:PermitirEnvioReal=true` (y `Production`). Una llave sin la otra impide arrancar; un modo desconocido redirige; si
  hubiera que redirigir sin un buzón válido, el correo no sale (falla cerrado; antes salía sin redirigir). **Producción en vivo:**
  `Correo__Proteccion__Modo=ninguno` y `Correo__Proteccion__PermitirEnvioReal=true`.
- `Correo:Proteccion:Modo` = `redirigir` (por defecto) | `ninguno`. Con `redirigir`, el mensaje que sale va **solo** a
  `Correo:Proteccion:BuzonPruebas` (sin CC ni CCO), con `[QA]` en el asunto y un aviso al inicio del cuerpo con los destinatarios
  originales (para, CC y CCO). En la base quedan los destinatarios **originales** y el buzón en `Correos.redirigidoA`: la deduplicación,
  los destinatarios previos y el historial se comportan igual que en producción.
- El validador (`clsValidadorOpcionesCorreo`) impide arrancar con el correo habilitado si, fuera de `Production`, el modo no es `redirigir`,
  o si con `redirigir` falta un buzón válido. En `Production` se admiten los dos (`redirigir` para un piloto).
- **Staging y QA corren con `ASPNETCORE_ENVIRONMENT=Production`** (para tener las mismas validaciones de secretos): por eso
  `infra/stack.dotnet.staging.yml` fija `Correo__Proteccion__Modo=redirigir` (no se lee del entorno) y el de producción lo deja en
  `redirigir` por defecto hasta salir en vivo.
- La vista previa trae `entorno_pruebas`, `redirigido_a` y un `aviso` para mostrar tal cual. El buzón está **por definir**.

### 4. Copia oculta al comercial

El correo del comercial (el dueño, `confirmadoPor`) va en `Bcc`, salvo que no tenga correo o ya esté en `para`/`cc`. Como MailSender no
admite `Reply-To`, el `From` es el comercial: las respuestas del cliente le llegan a él.

**Auditoría #199 (SPF/DMARC):** el `From` es configurable. `Correo:MailSender:ModoRemitente=comercial` (por defecto) usa el correo del
comercial solo si es UNA dirección válida de `Correo:MailSender:DominiosRemitente` (`boterosoto.com.co`); si no, `Remitente`.
`corporativo` usa siempre `Remitente` (para cuando TI no autorice al relay a enviar como cada comercial: un `550 5.7.1` es un fallo
permanente en MailSender). En ambos modos la firma del cuerpo trae nombre, correo y celular del comercial. Lo ideal es que MailSender
agregue `ReplyTo`: `From` corporativo y respuestas al comercial.

### 5. Contenido para el cliente: precio por ruta y total, sin desglose

- **PDF:** cada ruta imprime **un solo valor**, su precio final (`precioFinalLinea`: transporte y conexos con su margen). Los conexos se
  listan en "Estas tarifas incluyen" **sin precio** (en el pliego, con su ruta). Al final de la tarifa va el **VALOR TOTAL DE LA
  COTIZACIÓN** (Σ de las rutas que no rechazó la Mesa = `precioFinal`), con la moneda y la leyenda de impuestos. El resumen del pliego
  pasa de "TOTAL DEL PLIEGO (N RUTAS, SIN SERVICIOS CONEXOS)" a "VALOR TOTAL DE LA COTIZACIÓN (N RUTAS)". Costo, margen y flete nunca se
  imprimieron y siguen sin imprimirse. La referencia de maquetación (`clsMaquetacionPdfTests`) se regeneró solo para la variante Pliego.
  El Excel de la licitación **no cambia** (transporte por ruta y conexos aparte: es el formato del pliego del cliente); su total + los
  conexos = el total del PDF.
- **Correo:** pasa a HTML (MailSender envía una sola parte). Después del texto del comercial va un bloque fijo: precio de cada ruta, el total
  con la moneda, la leyenda de impuestos y, en USD, la nota de TRM mínima del PDF. Mismo formato de miles que el PDF.
- **Leyenda de impuestos:** el PDF no traía ninguna; se agrega `clsTextosEstandar.LeyendaImpuestos` en un solo lugar para PDF y correo,
  **pendiente de validación de negocio**.

### 6. Resultado ganada/perdida diferido a NEXO

- Si NEXO todavía no tiene la cotización Entregada —en `autocosteo` NEXO responde `409 COTIZACION_NO_ENTREGADA`; en `pendiente_costeo`
  Cotifly lo confirma leyendo el costeo—, el resultado se guarda **solo en Cotifly** con `sincronizacionResultadoNexo = pendiente` (y el
  comentario, para enviarlo después) y responde 200.
- El polling sigue consultando la solicitud (más allá de su edad máxima, hasta `CosteoNexo:EdadMaximaSincronizacionResultadoDias`, 30) y,
  cuando ve la entrega de la Mesa, envía el resultado **una sola vez** (`clsSincronizacionResultadoNexoService`): éxito → `sincronizado`;
  `RESULTADO_YA_DEFINIDO` → manda NEXO (como en `/resultado`) y queda anotado; `COTIZACION_NO_ENTREGADA` (carrera) → sigue pendiente; otro
  error → reintento cada `IntervaloReintentoConexosMinutos` hasta `MaximoFallosSincronizacionResultado` (10) y luego `no_sincronizable`.
  Solicitud descartada, costeo vencido o 30 días sin entrega → `no_sincronizable`. Idempotente: el paso a `sincronizado` es una escritura
  versionada; si pierde una carrera, el siguiente ciclo reenvía el MISMO resultado y NEXO responde 200 (mismo valor).
- **Cotifly no escribe como Mesa:** nunca llama a `/entregar`.
- **Coherencia con #127:** la perdida en `pendiente_costeo` sigue siendo la "perdida solo en Cotifly" (el cliente desistió antes del
  costeo): detiene el polling y **nunca** se sincroniza (`solo_cotifly`). La ganada en `pendiente_costeo`, en cambio, queda pendiente. Una
  ganada en `error_envio` sigue siendo 409 `COTIZACION_NO_ENTREGADA` (NEXO no recibió la solicitud).
- **Coherencia con #183:** una ruta rechazada no cambia la regla; la cabecera con todas las rutas rechazadas queda `entregada` (si NEXO la
  entrega) o `pendiente_costeo`, y la sincronización sigue a esa cabecera.
- Mientras está pendiente, otro `/resultado` con el mismo valor es 200 sin cambios y con otro valor `RESULTADO_YA_DEFINIDO`.
- API: `sincronizacion_resultado_nexo` (`pendiente`, `sincronizado`, `no_sincronizable`, `solo_cotifly` o `null`) y
  `error_sincronizacion_resultado_nexo` en `CotizacionResumen` (mis cotizaciones, detalle por folio, listado regional, respuesta de
  `/resultado`) y en `CosteoCotizacion` (con `resultado`). Un resultado anterior al script 13 se muestra `sincronizado` si la cotización
  está en NEXO y `solo_cotifly` si no.

### 7. Base de datos: `db/scripts/13_correo_mailsender.sql`

`Correos.messageId` (NVARCHAR(MAX), el Message-ID RFC del SMTP directo) no sirve para guardar el id de MailSender: no se puede indexar ni
es del mismo tipo. El 13 agrega `idMailSender` (único filtrado), `redirigidoA`, `intentosEntrega`, `enviadoAt`, `estadoActualizadoAt`,
`siguienteConsultaAt` (agenda, índice filtrado), `encoladoAt` (auditoría #199) y el CHECK `ck_correos_estado`; y en la cabecera la sincronización del resultado. Idempotente
y no destructivo; obligatorio ("falta 13" en `/health/ready`).

## Consecuencias

- Hay que cargar en Dokploy `Correo__MailSender__UrlBase` y el buzón de pruebas, aplicar el 13 y, para producción, autorizar la IP de
  MailSender en el relay y revisar SPF/DMARC del dominio.
- El frontend debe tratar el 200 del envío como "en camino" y seguir el historial; y mostrar "Pendiente de sincronizar con NEXO".
- Riesgos aceptados: un tiempo agotado en el POST puede dejar un correo encolado sin fila con id (se informa); un `fallido` posterior no
  libera la cotización para ajustes (se reintenta el mismo correo); el candado de envíos sigue siendo por proceso (`replicas: 1`).
