# ADR-015 — Costeo en Nexo: persistencia, envío, polling, margen y resultado

- **Estado:** Aceptada (se implementa por partes: #24 persistencia, #21 envío, #22 polling, #23 margen y "Mis cotizaciones", #42 resultado). Varios puntos quedan pendientes de confirmar con el equipo de Nexo (ver Pendientes).
- **Fecha:** 2026-09-28
- **Issue / HU:** #24 (HU-005.8), #21 (HU-005.5), #22 (HU-005.6), #23 (HU-005.7), #42 (HU-005.14). Relacionada con ADR-003 (el LLM no pone números), ADR-004 (estado de la conversación), ADR-005 (búsqueda de clientes en Nexo), ADR-008 (esquema solo por scripts) y ADR-011 (folio).
- **Nota de numeración:** ADR-013 es el despliegue en Dokploy y ADR-014 la TRM (HU-012); esta queda como ADR-015.

## Contexto

El tarifario de Google Drive se retiró: el costo de una cotización lo calcula Nexo (Cabina de Fletes). Hechos leídos del repo `NEXO` (`CONTRATO.md`, `docs/contrato-integracion/README.md` y los controllers), que mandan sobre lo que dicen las issues:

- **Identidad:** Nexo no tiene login. Cada petición lleva `X-Portal-Usuario-Id` con el id de la persona en la tabla de la administración (en Cotifly, `UsuariosWeb.idUsuarioExterno`, el mismo que ya usan clientes, municipios y cubicación). Sin identidad válida responde 401 `SESION_INVALIDA`; con rol insuficiente, 403 `SIN_PERMISO_CABINA_FLETES`.
- **Errores:** siempre `{code, message, timestamp, details?}`.
- **Alta:** `POST /api/captura-solicitudes` (`Comercial`, `Admin`) con `CapturaSolicitudRequest` → `201 CapturaResultDto {id, lineas, rutasCreadas, vehiculosCreados, scorePrioridad, fletesCalculados, lineasPorCostear, costeoEnProceso}`. Con 10 líneas o menos, Nexo auto-costea en la misma petición (consulta sus bibliotecas y crea una cotización `Borrador` con los fletes que encuentre); con más, lo hace en segundo plano.
- **No existe `GET /api/captura-solicitudes/{id}`** (lo que suponía la HU-005.6). El estado de la solicitud sale de `GET /api/solicitudes/{id}` (`SolicitudCosteoDto`: `solicitud.estado` = `Pendiente|EnCosteo|Entregada|Descartada` y `solicitud.lineas[].id`). Los fletes viven en la cotización de Nexo, que se obtiene con `POST /api/cotizaciones {solicitudId}` ("crear o recuperar": devuelve la entregada si la solicitud ya se entregó, el borrador existente si hay uno, o crea un borrador vacío) → `CotizacionDto {id, estado: Borrador|Entregada, resultado, total, lineas[{lineaSolicitudId, valorFlete, valorAprovisionamiento, valorRetornoContenedor}], conexos[{nombre, categoria, cantidad, valorAplicado}]}`.
- **Resultado:** `POST /api/cotizaciones/{id}/resultado` (`Comercial`, `AdminTarifas`, `CoordinadorFletes`, `Admin`), donde `{id}` es el id de la **cotización** de Nexo (no el de la solicitud, como decía la HU-005.14), con `{resultado: "ganada"|"perdida", motivo?, comentario?}` → `200 CotizacionDto`. Re-marcar el mismo resultado es idempotente; cambiarlo responde `409 RESULTADO_YA_DEFINIDO`. **Desde la issue #171 de Nexo ya no hay "entrega implícita":** una cotización en `Borrador` responde `409 COTIZACION_NO_ENTREGADA`; solo `POST /api/cotizaciones/{id}/entregar` (roles `AdminTarifas`, `CoordinadorFletes`, `Admin`) la entrega.
- **Regla de `estado_costeo` (HU-005.7):** la "atención" de la bandeja de Nexo (`EfSolicitudesCatalogoRepository`) cruza cada línea de la solicitud contra las líneas de cotización por `LineaSolicitudId`.

## Decisión

### 1. Unidad y persistencia (#24)

- **Una cotización de Cotifly = una solicitud de Nexo con una sola línea.** `Cotizaciones` guarda un solo costo y un solo precio (ADR-004, "Números del PDF"); las licitaciones de muchas líneas quedan fuera de esta iteración.
- Script `db/migrations_mssql/0004_costeo_nexo.sql` (idempotente, **pendiente de aprobación de scalderon-bs y del DBA**, coordinado con HU-002 como el 0002 y el 0003):
  - 22 columnas del formulario ampliado en `Cotizaciones` (encabezado y línea de `CapturaSolicitudRequest`, nombres del mapeo aprobado en `docs/MAPEO_CAMPOS_NEXO_COTIFLY.md`);
  - 7 columnas de seguimiento: `cotizacionNexoId`, `motivoEstadoCosteo`, `costeoEnviadoAt`, `costeoUltimaConsultaAt`, `costeoSiguienteConsultaAt` (agenda del polling, índice filtrado), `costeoConsultasFallidas` y `serviciosConexos` (JSON, `CHECK ISJSON`);
  - tabla `CotizacionPosiciones` (cubicaje, 1 a N, centímetros y kilogramos como Nexo).
- Las columnas `costeoId`, `nitCliente` y `estadoCosteo` del 0001 se usan tal cual. `estadoCosteo` pasa a ser el enum `EstadoCosteo` (códigos snake_case).
- El costeo en sí no se duplica: el flete queda en `costo` (columna existente) cuando se detecta y el resto se consulta en Nexo por `costeoId`. La excepción son los servicios conexos (`serviciosConexos`): hace falta guardar la selección del comercial y el precio con margen de cada uno para el PDF.
- `/health/ready` responde 503 `esquema incompleto: falta 0004` si el script no está aplicado (el modelo EF de `Cotizaciones` ya lo exige). **Precondición de despliegue:** el 0004 aplicado antes de desplegar esta versión (`DEPLOY.md`).
- `ck_cotizaciones_estadoCosteo` limita `estadoCosteo` a los 7 códigos de la sección 2 (el 0004 se detiene si hay filas con otro valor).
- **Deuda consciente (HU-011):** los campos de línea quedan en `Cotizaciones`. HU-011 multi-línea requiere un script nuevo (000N; el 0005 es de la TRM, ADR-014) que mueva los campos de línea a `CotizacionLineasSolicitud`. Anotado también en el encabezado del 0004 para el DBA.

### 2. Estados (`Cotizaciones.estadoCosteo`)

| Código | Cuándo | ¿Se consulta a Nexo? |
|---|---|---|
| `pendiente_envio` | La cotización y el formulario ya se guardaron; falta (o quedó a medias) el envío | No |
| `error_envio` | Nexo no recibió la solicitud (tiempo agotado, 4xx, 5xx, red, cliente sin BP...). `motivoEstadoCosteo` dice por qué | No; el comercial reintenta |
| `pendiente_costeo` | Solicitud creada y al menos una línea sin flete | Sí |
| `autocosteo` | Todas las líneas con flete y la cotización de Nexo en `Borrador` | Sí, hasta la edad máxima (puede pasar a `entregada`) |
| `entregada` | La cotización de Nexo está `Entregada` | No (terminal) |
| `descartada` | La solicitud de Nexo está `Descartada` | No (terminal) |
| `costeo_vencido` | Se llegó a la edad máxima del polling sin costeo | No; queda visible en "Mis cotizaciones" |

`descartada` es el cuarto estado real de Nexo que el diseño original no tenía (pendiente anotado en el mapeo); `pendiente_envio`, `error_envio` y `costeo_vencido` son propios de Cotifly. `null` = la cotización no pasa por Nexo (chat con costo manual).

### 3. Envío (#21)

- `POST /web/solicitudes-costeo` recibe el formulario ampliado. El caso de uso:
  1. valida el formulario con los mismos límites que Nexo y exige `nit_cliente`;
  2. crea la cotización con folio de ADR-011 (con el NIT), el formulario y sus posiciones en `pendiente_envio`, y **confirma la transacción antes de llamar a Nexo**: lo que llenó el comercial nunca se pierde;
  3. resuelve el BP del cliente en el catálogo de Nexo (`IClsBuscadorClientes` con el NIT, candidato con el mismo NIT; ADR-005 §4: el BP solo sale del catálogo, no se persiste);
  4. arma `CapturaSolicitudRequest` con una línea: `lineaNegocio` = `MargenesPolitica.mapeoLineasNexo` de la línea (nulo si no tiene), catálogos con el código de Nexo tal cual;
  5. con 201 guarda `costeoId`, `costeoEnviadoAt`, el estado (`autocosteo` si `lineasPorCostear = 0`, si no `pendiente_costeo`) y la primera consulta del polling; con cualquier fallo deja `error_envio` y el motivo, y responde igual 201 con la cotización (no es un error del comercial).
- `POST /web/cotizaciones/{folio}/costeo/reintentar` reenvía la solicitud guardada (sin volver a llenar el formulario) desde `error_envio` o `pendiente_envio`; en otro estado responde 409 `COSTEO_NO_REINTENTABLE`.
- **Cliente HTTP propio `NexoCosteo`** (no el `Nexo` de las búsquedas): 30 s en total y por intento (el alta auto-costea en línea), **sin reintentos en POST** (el alta no es idempotente: un reintento automático podría duplicar la solicitud en Nexo) y 2 reintentos en GET. Es la única desviación del patrón de PR #65 y se justifica por eso; comparte `Nexo:UrlBase`, `X-Portal-Usuario-Id` y la fábrica `clsFabrica*` (`*Nexo` / `*NoDisponible`).
- **Validación de Nexo replicada antes de guardar** (`CapturarSolicitudValidador`): peso de la línea > 0 o posiciones con peso (`PESO_REQUERIDO`) y peso a no más del 1 % de `sum(cantidad × pesoKg)` (`PESO_INCONSISTENTE`); responde `SOLICITUD_COSTEO_INVALIDA` con el código de Nexo en el detalle.
- **Un 400 de Nexo en el alta es definitivo** (`COSTEO_NEXO_SOLICITUD_INVALIDA`): la cotización queda en `error_envio` con el motivo "Rechazo definitivo de Nexo..." y el reintento responde `COSTEO_NO_REINTENTABLE` (los mismos datos volverían a fallar; se corrige en una versión nueva). Tiempo agotado, 5xx, red y 401/403 siguen siendo reintentables.
- **Sin el token de la petición después de confirmar:** la búsqueda del BP, el alta en Nexo y el guardado del resultado usan `CancellationToken.None` (los limita el cliente `NexoCosteo`, 30 s). Si el comercial se desconecta cuando Nexo ya recibió el POST, el `costeoId` se guarda igual y un reintento no duplica la solicitud. El reintento vuelve a validar lo guardado antes de reenviar.
- Tiempo agotado: Nexo pudo haber creado la solicitud. El motivo lo advierte ("verifica en Nexo antes de reintentar"); Nexo no ofrece clave de idempotencia (pendiente).

### 4. Polling (#22)

- `clsWorkerCosteoNexo` (`BackgroundService`) cada `CosteoNexo:IntervaloTickSegundos` toma hasta `CosteoNexo:Lote` cotizaciones con `costeoSiguienteConsultaAt <= ahora`. Cada cotización se procesa en **su propio ámbito de DI** (un fallo no contamina a las demás) después de **reclamarla** con un `UPDATE` condicional sobre `costeoSiguienteConsultaAt` (dos réplicas nunca la procesan a la vez; reprocesar el mismo estado no cambia nada).
- Espaciado por edad: la siguiente consulta es `ahora + clamp(edad × FactorEdad, IntervaloInicial, IntervaloMaximo)`, multiplicado por `2^min(fallos, 5)` tras errores seguidos. Por defecto 5 s al inicio (como el frontend de Nexo), hasta 15 min, y edad máxima de 72 h: con `pendiente_costeo` pasa a `costeo_vencido`; con `autocosteo` se deja de consultar sin cambiar el estado.
- Consulta: `GET /api/solicitudes/{costeoId}` y `POST /api/cotizaciones {solicitudId}` (crear o recuperar). Derivación en el dominio (`clsDerivacionEstadoCosteo`): `Descartada` → `descartada`; cotización `Entregada` → `entregada`; toda línea con flete → `autocosteo`; si no, `pendiente_costeo`.
- **Descartada:** `ObtenerCosteoQuery` de Nexo responde **409 `SOLICITUD_DESCARTADA`** (nunca 200 con "Descartada"); el adaptador lo traduce a la foto `Descartada`, terminal.
- **Id no reconocido:** 404 `SOLICITUD_NO_ENCONTRADA` es terminal (`COSTEO_NEXO_SOLICITUD_NO_ENCONTRADA`): se deja de consultar con el motivo (sin costeo, `costeo_vencido`) en vez de reintentar 72 h.
- Otros errores (tiempo, 4xx, 5xx, JSON inválido): evento de auditoría con `exito = false` y se reprograma con espaciado; nunca detiene el lote. Cada cambio de estado deja un evento `costeo_nexo_estado` en `EventosHerramienta`.
- Opciones `CosteoNexo:*` validadas al arrancar; sin `Nexo:UrlBase` el worker no consulta (lo anota una vez al arrancar).

### 5. Margen y precio (#23)

- El margen se resuelve **al enviar** (ahí está el BP): `IClsMargenResolverService` con la línea y el BP → `margenPolitica`, `margenAplicado`, `origenMargen`, `metodoCalculo` y `lineaNegocioId` quedan en la cotización.
- Cuando el polling ve el costeo completo (`autocosteo` o `entregada`): `costo` = flete + aprovisionamiento + retorno del contenedor de la línea; `precioFinal` = `IClsPrecioService.Calcular(costo, margenAplicado, metodo)` (HU-003.4); `costeoListoAt`. Cada servicio conexo guarda su precio con el mismo margen y método, e `incluido = true` por defecto.
- Si el costeo cambia después, el precio se recalcula mientras no haya PDF; con PDF ya generado se conserva, se audita y `motivoEstadoCosteo` lo advierte (el comercial genera una versión nueva).
- **El costo bruto de Nexo nunca llega al comercial como precio:** `CotizacionResumen` no tiene costo y `precio_final` sigue `null` hasta que pasa por Margen/Precio; `GET /web/cotizaciones/{folio}/costeo` solo devuelve precios.
- Selección de servicios conexos: `PUT /web/cotizaciones/{folio}/costeo/servicios-conexos`. PDF: `POST /web/cotizaciones/{folio}/costeo/pdf` arma el payload desde la cotización (flete con `precioFinal` y los conexos incluidos con su precio) y reutiliza el módulo PDF (HU-003.5); si ya tiene PDF responde `PDF_YA_GENERADO`.

### 6. Resultado ganada/perdida (#42)

- `POST /web/cotizaciones/{folio}/resultado` `{resultado, motivo?, comentario?}`, solo el dueño (404 si no, como el resto de `/web/cotizaciones`).
- Dos escrituras: primero Nexo (`POST /api/cotizaciones/{cotizacionNexoId}/resultado`; si Cotifly aún no tiene `cotizacionNexoId`, lo recupera con `POST /api/cotizaciones {solicitudId}`), y **solo con 200** se actualizan `ganada`, `motivoRechazo` (= `motivo`) y `marcadoAt`. `comentario` se reenvía a Nexo y no se persiste en Cotifly (decisión de la HU-005.14).
- `409 RESULTADO_YA_DEFINIDO` y `409 COTIZACION_NO_ENTREGADA` de Nexo → 409 `ErrorNegocio` con el mismo código y **ningún cambio local**. Nexo caído → 503 `COSTEO_NEXO_NO_DISPONIBLE`, sin cambio local.
- **Entrega implícita:** la HU-005.14 la describía (marcar el resultado sobre un `Borrador` lo entregaba sin correo). Nexo la eliminó (issue #171): hoy ese caso responde `COTIZACION_NO_ENTREGADA`. Si Nexo la restableciera, `estado_costeo` puede saltar a `entregada` en el mismo momento de marcar el resultado aunque antes estuviera en `autocosteo` o `pendiente_costeo`: Cotifly lo trata como un estado válido (el polling y este endpoint derivan el estado de la respuesta de Nexo), nunca como una inconsistencia.
- `POST /web/cotizaciones/{folio}/seguimiento` (interino) se conserva por compatibilidad con el frontend y queda marcado como obsoleto en el contrato.

### 7. Agente de chat

No se agrega una herramienta `enviar_a_costeo` en esta iteración. La HU-005.5 envía "los datos capturados en el formulario ampliado" (HU-005.3), que el chat no recoge (carrocería, clase de viaje, contenedor, posiciones), y el costeo puede tardar horas: no cabe en un turno. El chat sigue con el costo manual (`buscar_tarifa` responde `TARIFA_FUENTE_NO_DISPONIBLE`) y el conjunto de herramientas y `prompt_cotizador.md` no cambian. Una herramienta futura armaría la solicitud desde el estado de ADR-004 (los ids los pondría el filtro, nunca el LLM).

### 8. URL base

`Nexo__UrlBase` es la raíz del servicio **sin** `/api` (ej. `https://nexo.boterosoto.com.co`): todas las rutas de `CONTRATO.md` empiezan por `/api/...` y el código las agrega. Se corrigen el ejemplo de `.env.example`, la documentación de `clsOpcionesNexo` y la URL de las pruebas (`http://nexo-test.local`), que terminaban en `/api` y habrían dado `/api/api/...`. Las rutas ya existentes no cambian.

## Alternativas consideradas

- **Guardar el costeo completo (líneas y conexos de Nexo) en tablas propias:** duplica la fuente de verdad; la HU-005.8 pide consultarlo en Nexo por `costeoId`. Solo se guarda lo que el comercial decide (conexos incluidos) y lo que se imprime (precios).
- **Llamar a Nexo dentro de la transacción de creación:** si Nexo falla se perdería el formulario o quedaría una cotización a medias; se confirma primero y el envío actualiza después.
- **Reutilizar el cliente `Nexo` (8 s, 2 reintentos):** el alta auto-costea en línea (puede pasar de 8 s) y un reintento automático de un POST no idempotente duplicaría solicitudes.
- **Polling con intervalo fijo:** preguntar cada pocos segundos durante horas no tiene sentido (HU-005.6); el espaciado por edad es determinista y no necesita contador.
- **Resolver el margen al llegar el costeo:** exigiría volver a buscar el BP en segundo plano (otra llamada a Nexo); al enviar ya se tiene.

## Consecuencias

- "Mis cotizaciones" muestra el estado real del costeo (`estado_costeo` y `motivo_estado_costeo`), y `precio_final` solo cuando pasó por el margen.
- Las cotizaciones del chat (costo manual) no cambian: `estado_costeo = null`, sin polling.
- El 0004 debe aplicarse antes de desplegar esta versión (la readiness lo exige).
- La primera consulta del polling crea en Nexo el borrador vacío de una solicitud que no se auto-costeó (lo mismo que hace la Mesa de Nexo al abrirla). Es **una sola vez por solicitud**: las consultas siguientes recuperan ese mismo borrador ("crear o recuperar").
- **Efecto de `GET /api/solicitudes/{id}` en Nexo:** abrir la solicitud la pasa de `Pendiente` a `EnCosteo` (`ObtenerCosteoQuery`). La primera consulta del polling (a los 5 s) cambia así la bandeja de la Mesa y los KPIs de Nexo ("pendientes" vs "en costeo") aunque nadie la haya abierto. Pendiente con Nexo (ver Pendientes).
- **Ambientes con base compartida:** si staging y producción de Nexo comparten base, el polling de un staging de Cotifly consultaría (y cambiaría de estado) solicitudes reales. Staging corre con `CosteoNexo__Habilitado=false` hasta que el DBA confirme esquemas separados.
- **PDF en `autocosteo`:** se permite generar el PDF con el costeo automático, sin esperar a que el coordinador entregue (`entregada`). Pendiente de confirmar con negocio si debe exigirse `entregada`.
- **Concurrencia polling vs selección de conexos:** el polling (si el costeo cambia) y `PUT .../servicios-conexos` escriben `serviciosConexos` sin control optimista; el polling conserva la selección que leyó, pero una selección guardada en el mismo instante puede perderse. Pendiente: control por `updatedAt` en ese caso.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Aprobar el DDL del `0004_costeo_nexo.sql` (coordinación con HU-002) | scalderon-bs + DBA (redacta Duban) |
| Confirmar el endpoint real de alta (`POST /api/captura-solicitudes`) y el mecanismo de autenticación para Cotifly (hoy `X-Portal-Usuario-Id` del comercial, sin token; ¿API key, JWT de servicio, allowlist de IP?) | Duban + equipo de Nexo |
| Confirmar el endpoint de consulta del costeo: no existe `GET /api/captura-solicitudes/{id}`; se usa `GET /api/solicitudes/{id}` + `POST /api/cotizaciones` (crear o recuperar), que crea un borrador vacío si no hay auto-costeo | Duban + equipo de Nexo |
| Efecto de `GET /api/solicitudes/{id}` (pasa `Pendiente` → `EnCosteo` y afecta la bandeja y los KPIs de la Mesa): ¿endpoint de lectura sin efecto para integraciones? | Equipo de Nexo |
| Confirmar esquemas separados entre staging y producción de Nexo (mientras tanto, staging con `CosteoNexo__Habilitado=false`) | DBA |
| ¿PDF permitido en `autocosteo` o solo en `entregada`? | Negocio |
| `margen_aplicado` junto a `precio_final` (en "Mis cotizaciones" y en el costeo) permite al comercial deducir el costo de Nexo; se conserva porque el resumen ya exponía el margen. ¿Ocultarlo? | Negocio |
| Control optimista (`updatedAt`) entre el polling y `PUT .../servicios-conexos` | Duban |
| Intervalo de polling que acepta Nexo (por defecto 5 s al inicio, hasta 15 min, 72 h de edad máxima) y límites de rate limiting | Equipo de Nexo |
| Clave de idempotencia o referencia externa en el alta, para reintentar sin riesgo de duplicar tras un tiempo agotado | Equipo de Nexo |
| Cómo enviar prospectos (Nexo exige `clienteId` o `clienteBp`; el buscador de Cotifly no expone el `clienteId` de Nexo) | Duban + equipo de Nexo |
| Identidad y rol con que Cotifly marca el resultado (hoy el comercial; Nexo exige `Comercial`, `AdminTarifas`, `CoordinadorFletes` o `Admin`) y confirmar que la entrega implícita no vuelve (issue #171 de Nexo) | Duban + equipo de Nexo |
| Si los servicios conexos llevan el mismo margen que el flete | Negocio (Gerencia Comercial) |
| Probar contra Nexo real que marcar "ganada" alimenta la Biblioteca de Tarifas Ganadas (`TarifaHistorica`) | Duban + equipo de Nexo |
| Licitaciones de varias líneas (HU-011): un script 000N (el 0005 es de la TRM) que mueva los campos de línea a `CotizacionLineasSolicitud` | Negocio + Duban + DBA |
