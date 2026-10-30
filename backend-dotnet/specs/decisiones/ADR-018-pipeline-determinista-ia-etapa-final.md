# ADR-018 — Pipeline determinista de cotización e IA en la etapa final

- **Estado:** Aceptada (decisión del usuario del 2026-10-05); reemplazada en parte por ADR-019 (§3, §4, §12 y §18, ver la sección siguiente). **Reemplazada en parte por [ADR-023](ADR-023-correo-mailsender-y-resultado-diferido.md) (HU-033, #199, 2026-10-22):** el resultado ganada/perdida se guarda **diferido** (solo en Cotifly, pendiente de sincronizar con Nexo) en lugar del 409 `COTIZACION_NO_ENTREGADA` cuando Nexo todavía no entregó (`autocosteo` y `pendiente_costeo`; ADR-023 §6), y el envío por correo, una vez que MailSender lo acepta, no se deshace con un `fallido` posterior (ADR-023 §2). Se implementa por partes en HU-013.2 a HU-013.9 (#98 a #105); varios puntos quedan pendientes de confirmar con el equipo de Nexo (`docs/conexion_nexo/HU-013-preguntas-nexo.md`).
- **Fecha:** 2026-10-05
- **Issue / HU:** #97 (HU-013.1). Lo implementan #98 (HU-013.2), #99 (HU-013.3), #100 (HU-013.4), #101 (HU-013.5), #102 (HU-013.6), #103 (HU-013.7), #104 (HU-013.8) y #105 (HU-013.9).
- **Relación con otras ADR:** amplía ADR-003 (la IA solo redacta y propone), **reemplaza a ADR-011** en lo que toca a Google Chat (el formato del folio sigue vigente y se retoma en §7), se apoya en ADR-015 (costeo en Nexo, que pasa a ser el único cotizador), ADR-014 (TRM y USD) y ADR-017 (líneas de solicitud). Mapeo de campos Nexo ↔ Cotifly: `docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md`.

## Reemplazado en parte por ADR-019 (HU-021, 2026-10-08)

El script `0009` reestructuró la cotización en solicitud → rutas → camiones y [ADR-019](ADR-019-reestructuracion-rutas-y-camiones.md) fija cómo lo usa el código. Siguen vigentes el pipeline determinista, la IA en la etapa final, la regla del PDF (§11), `versionNegocio` con `If-Match` y el candado del guardado. Quedan reemplazados:

- **§3 (margen):** el ajuste es **por ruta**. `PUT .../costeo/margen` dice la ruta (`linea_orden` o `linea_solicitud_id`; con varias y sin ella, 400 `RUTA_REQUERIDA`), valida contra el sugerido de esa ruta y solo esa ruta necesita precio; el PDF sigue exigiendo todas las rutas con precio (decisión 9). `MargenAjustes.lineaSolicitudId` apunta a la ruta real.
- **§4 y §12 (conexos):** el JSON `Cotizaciones.serviciosConexos` se reemplazó por filas de la ruta (`CotizacionServiciosConexos`: id, precio = costo de Nexo, cantidad). Quitar un conexo borra su fila (ya no hay `incluido`). A Nexo van pegados al primer camión vigente de la ruta, con envío diferido desde el polling (decisiones 3 y 10).
- **§12 (margen sugerido):** `Cotizaciones.margenSugerido` se dividió en `margenPoliticaSugerido` y `margenSAPSugerido` por ruta.
- **§12 (idempotencia):** ~~la tabla de idempotencia, cuando se escale, va en el siguiente script libre (hoy el `0011`)~~. **#191
  (HU-026.1):** va en `db/scripts/06_idempotencia_captura.sql` (`dbo_cotifly.CapturasIdempotentes`), que conserva el número con que se
  propuso en #173. `IClsAlmacenIdempotencia` es `clsAlmacenIdempotenciaSql` (la de memoria queda para pruebas): un INSERT que choca con
  `UNIQUE (usuarioId, operacion, clave)` reserva la clave entre réplicas; la petición simultánea de OTRA réplica recibe 409
  `ESTADO_NO_PERMITE` (`CAPTURA_EN_CURSO`) en lugar de esperar (en la misma réplica un semáforo por clave la hace esperar, como antes); la toma vence en 10 min (huérfanas tras una caída); la respuesta que consume la clave se
  guarda en JSON 24 h y una tarea horaria purga las vencidas. La huella compara los números por valor (`clsHuellaIdempotencia`). Sin la
  tabla, `/health/ready` responde `falta 06` (no se degrada a memoria). La API ya no exige `replicas: 1` por la idempotencia.
- **§12 (idempotencia de la captura, #173):** `POST /web/solicitudes-costeo` acepta `Idempotency-Key`. Antes de llamar a Nexo la clave
  queda registrada con el folio confirmado (`IClsRegistroCapturasIdempotentes`) y, en cuanto se conoce, con el `solicitudNexoId`: si la
  primera petición falla después de enviar a Nexo, el reintento con la misma clave **no vuelve a llamar a Nexo** (responde la cotización
  guardada o 409 `ESTADO_NO_PERMITE` con `detalle.motivo = CAPTURA_EN_CURSO`, el folio y el id de Nexo). ~~**Deuda:** todo vive en la
  memoria del proceso~~ **#191:** el registro (`clsRegistroCapturasIdempotentesSql`) usa la misma fila de `CapturasIdempotentes` que
  reserva la clave, y el folio se anota DENTRO de la transacción que lo confirma: sobrevive a un reinicio y lo ve cualquier réplica.
  `POST .../costeo/reintentar` consulta el mismo registro por folio: si la captura llegó a Nexo y el guardado posterior falló (folio en
  `pendiente_envio` sin `solicitudNexoId`), vincula ese id y deja la cotización en `pendiente_costeo` (el polling la sigue) **sin
  reenviar**. **Riesgo vigente:** si el registro ya no lo sabe (reinicio, otra réplica, más de 24 h), el reintento reenvía como antes y
  Nexo tendría dos solicitudes; lo cierra la tabla del `06`.
- **§18 (conexos al re-solicitar):** ya no se descartan: se conservan en el mismo folio y se copian a la `-vN` (`conexos_conservados`; `conexos_descartados` queda en `[]`, obsoleto). `filaOrigen` pasa a `rutaOrigenIndice`.
- **Nombres de tabla y columna:** donde este texto dice `Cotizaciones`, `costeoId` o `costeoUltimaConsultaAt`, hoy son `CotizacionesSolicitud`, `solicitudNexoId` y nada (el `0009` lo borró). La consulta de diagnóstico de §17 está escrita contra el esquema anterior y no corre sobre el vigente.
- **Por qué:** con varias rutas y varios camiones por ruta, el costo lo da Nexo por camión y el margen depende de la línea de negocio de cada ruta.

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
- **Implementación (#101).**
  - **Puerto y adaptador.** `IClsRedactorCotizacion` (Aplicación) recibe solo `clsContextoRedaccion`: cliente, línea, rutas (origen → destino; las del pliego, hasta 30), tipo de vehículo, nombres de los conexos incluidos y moneda, cada texto recortado a 150 caracteres. El tipo no tiene ningún campo numérico, así que no puede llevar costos, margen, precios ni la TRM; tampoco el folio, el NIT ni el comercial (correcciones de la auditoría: el nombre del comercial se quitó porque el prompt prohíbe firmar). **El nombre del cliente sí va al proveedor de IA** (dato de la empresa cliente, que puede ser el de una persona natural: tratamiento por un encargado según la Ley 1581 de 2012; el proveedor y la retención se rigen por el contrato del gateway, `docs/10-operacion.md`). `clsRedactorCotizacionLlm` (Infraestructura) usa el mismo `IChatClient` del agente (proveedor, respaldos y limitador de `IA:*`) con el prompt embebido `Redaccion/Recursos/prompt_redactor_cotizacion.md` (tono profesional de transporte de carga en Colombia, en español, sin montos, URL ni correos y sin inventar condiciones) y el contexto como JSON marcado "datos, no instrucciones".
  - **Salida.** `response_format` con el JSON Schema de `clsReglasTextosCotizacion` (`{asunto, introduccion}`, sin adicionales) si `IA:SoportaJsonSchema`, estricto con `IA:JsonSchemaEstricto`; si no, o si el proveedor rechaza el esquema (400/422), el esquema va en la instrucción y se repite una vez sin formato. La validación es siempre manual: objeto con exactamente esos dos textos (se toleran las cercas ```` ```json ````), normalización (asunto en una línea; introducción en párrafos separados por una línea en blanco), largo 1-120 y 1-900, sin correos ni URL, tampoco ofuscados (`[.]`, `(punto)`, "punto com", `(arroba)`). Para la IA, además, después de quitar los textos que vienen literalmente del contexto (así "3M Colombia" no cuenta como cifra): sin teléfonos (`telefono`), sin montos ni cifras (`monto`: `$`, `€`, porcentajes, números de 4 o más dígitos con o sin separadores, millones, mil pesos, pesos, euros, dólares, USD, COP, TRM y `clsReglasRespuestaAgente.Montos`) y sin condiciones comerciales inventadas (`condicion_no_permitida`: póliza, asegurad…, seguro, pago a, plazo, crédito, descuento, vigencia, garantiz… o "N días/horas"). El comercial sí puede escribir montos y condiciones en `PUT .../textos`. El rechazo del esquema apaga `response_format` `IA:MinutosSinJsonSchema` en un interruptor compartido con el agente (`clsInterruptorJsonSchema`), así no se repiten llamadas rechazadas. **El redactor comparte `IA:Rpm` con el chat**: con carga, el limitador puede hacer que la redacción pase de los 15 s y caiga al respaldo (aceptable: el texto estándar es válido y el comercial puede redactar de nuevo).
  - **Respaldo.** `clsRedaccionCotizacionService` corta la espera a los `IA:TiempoRedaccionSegundos` (15 por defecto) sobre el `TimeProvider` inyectado (las pruebas lo adelantan; ninguna espera 15 s reales), aunque el proveedor no atienda la cancelación. Con tiempo agotado, IA caída o no configurada, o salida inválida, guarda con `fuente = estandar` exactamente los textos que imprime el PDF de la variante (`clsCosteoCotizacionService.TextosEstandar`): en tarifa única `AsuntoEstandar(línea)` e `IntroduccionEstandar(false)`; en un pliego `AsuntoLicitacion(referencia, rutas)` e `IntroduccionEstandar(true)` (la de `MultiSeccion`, 1026 caracteres: por eso `TextosCotizacion.introduccion` y el `PUT .../textos` del comercial admiten hasta 1100; la IA sigue limitada a 900). El PDF solo reemplaza su asunto e introducción con textos `ia` o editados.
  - **Auditoría.** La registra el caso de uso DESPUÉS de guardar. `EventosHerramienta` `redactar_textos_costeo` con el folio, proveedor, modelo (el que informa la respuesta, si no el configurado), versión del prompt (16 primeros hexadecimales de su SHA-256), duración, fuente, respaldo y su motivo (`tiempo_agotado`, `ia_no_disponible` o el de la validación), `sha256` del asunto y la introducción y `guardado` (`false` con `CONFLICTO_CONCURRENCIA`); `editar_textos_costeo` con la fuente, si invalidó el PDF, `sha256` y la versión del prompt. Nunca los textos ni el contexto, y sin `ThreadId` (como #100).
  - **Concurrencia.** La IA se llama antes de escribir; la escritura (textos, PDF invalidado con `MotivoInvalidacionPdf.Textos`, versión + 1) se confirma con `GuardarCambiosSiVersionAsync`: si la cotización cambió mientras la IA redactaba, `CONFLICTO_CONCURRENCIA` y nada se guarda. `PDF_YA_ENVIADO` y `TEXTOS_EDITADOS` se responden sin llamar a la IA.
  - **PDF sin textos guardados.** `POST .../costeo/pdf` no redacta ni guarda nada: imprime los estándar. Así el PDF nunca depende de la IA ni espera sus 15 s.

**(b) Propone** acciones finales desde el asistente de `/web/chat` (HU-013.6):

- `/web/chat` exige `folio` (400 `FOLIO_REQUERIDO`): el asistente trabaja sobre una cotización ya creada, del comercial.
- Herramientas de solo lectura o propuesta: `consultar_cotizacion`, `proponer_ajuste_margen`, `proponer_re_solicitud` y `buscar_servicio_conexo`. Se retiran `generar_pdf` (payload armado por el LLM), `calcular_precio`, `obtener_margen`, `buscar_tarifa` y las herramientas de creación.
- **Ninguna herramienta entrega montos de dinero al LLM.** Con el margen y un precio, el LLM podría deducir el costo de Nexo. Salida exacta de `consultar_cotizacion`:

  | Campo | Ejemplo |
  |---|---|
  | `folio`, `version` | `PCN-900555111-DG-05102026-001`, 1 |
  | `estado_costeo`, `estado_envio`, `tiene_resultado`, `tiene_pdf`, `pdf_desactualizado` | `autocosteo`, `sin_enviar`, `false`, `false`, `false` |
  | `cliente` (nombre), `linea_negocio` | `Cliente SAS`, `Nacional` |
  | `origen`, `destino`, `tipo_vehiculo` | `Buenaventura`, `Bogotá D.C.`, `3S3` |
  | `moneda` | `COP` |
  | `margen_actual`, `margen_sugerido`, `origen_margen` (porcentajes) | `0.15`, `0.15`, `politica_linea` |
  | `conexos` (solo nombre, cantidad e incluido) | `[{"nombre": "Cargue", "cantidad": 1, "incluido": true}]` |
  | `rutas` (solo con varias líneas: orden, origen, destino, vehículo) | `[{"linea_orden": 1, "origen": "Buenaventura", ...}]` |

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
- Roles de Nexo para agregar: `Comercial` y `Admin`. Antes también `CoordinadorFletes` (confirmado en el código de Nexo según `docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md`; el swagger no lo declara); con NEXO#307 (S6-HU-25.1) ese rol pasa a `AdminBiblioteca` y sale de cotizaciones. El texto de `NEXO_SIN_PERMISO` nombra solo los roles vigentes (#180). 403 → `NEXO_SIN_PERMISO`; 400/404 → `CONEXO_INVALIDO`; caído → `NEXO_NO_DISPONIBLE`.
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
  - **Trazabilidad en Nexo.** La captura nueva agrega a `notas` "Re-solicitud de {folio}; reemplaza la solicitud {id anterior}" (en una línea nueva, sin superar 2000 caracteres: se recortan las notas existentes). La solicitud anterior sigue viva en Nexo (`docs/conexion_nexo/HU-013-preguntas-nexo.md`, pregunta 4).
  - **Moneda (decisión del usuario, 2026-10-06; ADR-014 §5).** La versión nueva `-vN` es una cotización nueva: copia la moneda pero **vuelve a fijar la TRM del día** (`IClsFijacionMonedaService.FijarAsync`) antes de reservar la versión de la original y antes de llamar a Nexo; sin TRM, `TRM_NO_DISPONIBLE` (400, igual que el formulario y las licitaciones) y no se crea nada (ni la reserva, ni la `-vN`, ni la captura). Re-solicitar en el mismo folio y reintentar conservan la moneda y la TRM fijadas.
  - **Catálogos (#106, G2).** Los `datos_carga` de `-vN` se validan con `IClsValidadorCatalogosCaptura` antes de reservar, y la captura usa el envío normalizado de #106.
  - **Restricción para #102.** Los eventos `costeo_nexo_re_solicitud` traen costos y se escriben SIN `ThreadId`: el asistente no debe leer `EventosHerramienta` de esta herramienta (ni de otra con montos) para construir su contexto.
  - **Servicios conexos al re-solicitar (#124, HU-013.15).** Regla en §18.

### 6. Moneda en el formulario (HU-013.7)

- `moneda` (`COP` por defecto, o `USD`) en `POST /web/solicitudes-costeo`, con el mismo contrato que `POST /web/licitaciones` (ADR-014 §5).
- La TRM se fija al crear con `clsFijacionMonedaService`. Sin TRM, 400 `TRM_NO_DISPONIBLE`: no se guarda nada ni se envía nada a Nexo.
- El polling convierte a USD el precio del flete y de los conexos; el costo sigue en COP.
- El PDF lleva la nota de TRM mínima con la `trm_aplicada`.
- **Depende de #50 (PR #96, rama `50-hu-0121-trm-dolarapi-nota-pdf`)**, que aún no está en `QA`.

### 7. Folio, sesión y retiro de Google Chat (HU-013.9)

- **ADR-011 queda reemplazada por esta ADR.** Su formato de folio sigue vigente tal cual: `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`, generado por el servidor con las iniciales del comercial; la v2+ reutiliza el folio base. Lo que se retira es la identidad y la entrega por Google Chat.
- Se retiran `POST /chat/events` (marcado `deprecated` en el contrato), su controlador, `Infraestructura/GoogleChat/*`, las variables `GChat__*` y su validación al arrancar. **Hecho en #105** (ver §15).
- El estado de la conversación se simplifica a una sesión web por folio.
- Las tablas `EventosConversacion` y `Sesiones` **no se borran** (auditoría): las filas de Google Chat dejan de alimentarse; el asistente por folio sigue escribiendo las suyas (§14).

### 8. Concurrencia optimista

- **El script `db/migrations_mssql/0006_textos_y_concurrencia.sql` es prerrequisito de #98 (HU-013.2) y lo entrega #98** (lo aplica el DBA). Agrega la versión de la cotización (en la implementación, `Cotizaciones.versionNegocio`: ver §12), `Cotizaciones.textosCotizacion` (JSON, lo usa #101) y lo que necesita la regla del PDF de §11 (marca de PDF desactualizado e historial de PDF). #99, #100 y #101 dependen de él.
- `GET .../costeo` devuelve `ETag` y `row_version`. **Toda respuesta 200 o 201 de una mutación del costeo devuelve el `ETag` nuevo**, incluidos `PUT .../servicios-conexos`, `re-solicitar` (200 y 201) y `pdf`.
- **`If-Match` es OBLIGATORIO en las mutaciones de la etapa final:** `margen`, `servicios-conexos` PUT y POST, `re-solicitar`, `textos` y `textos/redactar`. Sin él, 428 `PRECONDICION_REQUERIDA`; si no coincide, 409 `CONFLICTO_CONCURRENCIA`, sin cambios. En `POST .../costeo/pdf` sigue opcional. Antes del `0006` el servidor no lo exige. El polling también sube la versión cuando el costeo cambia (estado, costo o precio), así que una acción propuesta queda invalidada si el costeo cambió después de proponerla.
- **`Idempotency-Key`** (opcional) en `POST .../servicios-conexos` y `POST .../re-solicitar`: una repetición con la misma clave y el mismo usuario en 24 h devuelve la respuesta guardada sin volver a ejecutarla. Un doble clic no duplica el conexo en Nexo (cuyo POST no es idempotente) ni crea `-v2` y `-v3`. La misma clave con otro cuerpo responde 400. El frontend genera un UUID por intención (al pulsar Confirmar o el botón).

### 9. Estados de Nexo ↔ `estado_costeo` (vigente, ADR-015 §2 y §4)

La Parte 1 de `docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md` marcaba que los estados de Nexo no correspondían a los de Cotifly. El mapeo vigente en el código (`clsDerivacionEstadoCosteo`) es este:

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

Los gaps entre el mapeo, el DTO actual y el swagger de Nexo están en `docs/conexion_nexo/HU-013-preguntas-nexo.md` §Gaps. Se implementan en una issue aparte.

**Implementación (HU-013.10, #106).** Cotifly valida por código antes de guardar: `tipo_vehiculo` y `clase_viaje` contra los catálogos maestros de Nexo, y `carroceria`, `tipologia` y `clase_contenedor` contra listas en código (con la lista provisional de extradimensionado). Los catálogos maestros se guardan en memoria (`Nexo:MinutosCacheCatalogos`, 30 min) y, **si Nexo no responde**, se usa la última copia mientras tenga menos de `Nexo:HorasCatalogosVencidos` (24 h): los catálogos cambian poco y un código válido hace unas horas lo sigue siendo. Sin copia utilizable, `GET /web/catalogos-flete` y `POST /web/solicitudes-costeo` responden 400 `CATALOGOS_FLETE_NO_DISPONIBLE` y no se guarda nada: se descartó aceptar el texto sin validar (crearía vehículos duplicados en Nexo) y guardar la cotización para validarla después (un reintento enviaría lo no validado). `MargenesPolitica.mapeoLineasNexo` se revisa contra `GET /api/catalogos/lineas-negocio` como advertencia en el log al enviar, con la copia en memoria: no hay identidad de servicio para hacerlo al arrancar ni en `/health/ready` (Nexo exige `X-Portal-Usuario-Id`), y Nexo resuelve la línea por coincidencia aproximada, así que no se bloquea el envío. `tipos-vehiculo` y `clases-viaje` son obligatorios (solo los oficiales); si fallan `clases-contenedor` o `lineas-negocio` quedan vacíos con un warning y `clase_contenedor` se valida contra la lista fija. `CATALOGOS_FLETE_NO_DISPONIBLE` responde 503, como el resto de "Nexo no disponible". Los 400 `CLIENTE_*` de Nexo son reintentables (el reintento vuelve a resolver el BP); `ENUM_INVALIDO`, `LINEA_INCOMPLETA` y `PESO_*` son definitivos.

**Pendientes (issues aparte):** en las licitaciones, las filas del Excel siguen sin `origenMunicipioId`/`destinoMunicipioId`, sin horas propias por línea y sin validación por código de catálogo (llegan como texto del pliego); y la autenticación entre servicios con Nexo (hoy solo `X-Portal-Usuario-Id`, pregunta 6 de `docs/conexion_nexo/HU-013-preguntas-nexo.md`).

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
- **Envío por correo y resultado también versionados.** El envío reserva el estado (enviada o reenviada, versión + 1) con `GuardarCambiosSiVersionAsync` ANTES de que el correo salga y lo deshace si el transporte falla [Reemplazado por ADR-023 §2 (HU-033): solo se deshace si MailSender rechaza el encolado (400/413/422, caída); un tiempo agotado en el POST (incierto) conserva la reserva y un `fallido` posterior a la aceptación no la revierte]; con el PDF desactualizado o la versión cambiada responde 409 `CONFLICTO_CONCURRENCIA` sin enviar. El resultado ganada/perdida sube la versión y guarda con comparación (si pierde, recarga y reaplica, porque Nexo ya lo tiene). Así un ajuste nunca invalida un PDF que ya salió: de un ajuste y un envío simultáneos gana exactamente uno.
- **Margen sugerido con trazabilidad.** `Cotizaciones.margenSugerido` y `origenMargenSugerido` (`sap_tm` o `politica_linea`) se fijan al resolver el margen al enviar o crear la cotización; el ajuste no los cambia ni toca `margenPolitica`. La regla de la justificación usa el sugerido, y cada fila de `MargenAjustes` lo copia en `margenPolitica`. El 0006 llena las filas existentes con lo que haya (`margenAplicado` si nadie ajustó; la política si ya estaba negociada).
- **Una sola función de precio.** `clsPrecioMonedaCosteo` la comparten el polling y el ajuste: costo en pesos por el módulo de Precio y, en otra moneda, conversión con la `trmAplicada` fijada en la cotización (lista para #103).
- **Historial del PDF.** `CotizacionPdfsHistorial` guarda la referencia del PDF invalidado, la acción, el comercial y la fecha; la acción queda además en `EventosHerramienta` (`ajustar_margen_costeo`, sin montos). Al regenerar, el archivo anterior se copia en el almacén como `{folio}-hN.pdf` (no se borra; no se descarga por `/pdfs`) y el nuevo conserva `{folio}.pdf`. Retención propuesta: la misma de los PDF del almacén (ver `docs/10-operacion.md`).
- **Justificación.** Se guarda sin secuencias ANSI completas ni caracteres de control, con los espacios repetidos colapsados. Es texto libre del comercial y puede traer datos personales del cliente: retención y tratamiento en `docs/10-operacion.md`.
- **Métricas.** `vAjustesMargenPorComercial` gana `numAjustesBajoSugerido` (`diferencia < 0`) y `GET /web/metricas/ajustes-margen` lo expone como `num_ajustes_bajo_sugerido`.
- **No se reutiliza `IClsAjusteMargenService`** (chat, HU-003.4): guarda por su cuenta y pone un motivo por defecto. El ajuste del costeo confirma el ajuste, los precios, la invalidación del PDF y la versión en una sola transacción, y deja la justificación nula por encima del sugerido.
- **Reversa del 0006:** `db/migrations_mssql/rollback/0006_textos_y_concurrencia_reversa.sql` (destructiva para esas columnas y el historial; primero se vuelve a la versión anterior de la aplicación).
- **Idempotencia (#100, adoptada por #99).** `IClsAlmacenIdempotencia.EjecutarUnaVezAsync<T>(ambito, clave, huella, accion, seGuarda, ct)` es la única abstracción de `Idempotency-Key` del proyecto: `ambito = "{operacion}:{usuarioId}"`, `huella` = SHA-256 del folio y el cuerpo, semáforo por clave, 24 h de vigencia, purga de claves vencidas y fallidas, y `seGuarda` (por defecto, solo los éxitos) decide qué resultados consumen la clave. Desde #191 la implementación es `clsAlmacenIdempotenciaSql` sobre `db/scripts/06_idempotencia_captura.sql` (ver la nota de §12 arriba); `clsAlmacenIdempotenciaEnMemoria` queda para las pruebas.

### 13. Decisiones de implementación (#99)

- **El id de la línea de la cotización de Nexo no se persiste.** `clsLineaCosteoNexo.Id` se llena cada vez que se lee la cotización de Nexo (polling y conexos). Al agregar, Cotifly lee la cotización con `GET /api/cotizaciones/{id}` y traduce `linea_orden` → `CotizacionLineasSolicitud.lineaSolicitudNexoId` (ya guardado por el polling) → la línea de la cotización con ese `lineaSolicitudId`. Así el `lineaId` siempre es el vigente en Nexo (si Nexo recrea las líneas de su cotización, uno guardado quedaría viejo) y no hace falta un script nuevo. Cuesta un GET más por petición. Sin `lineaSolicitudNexoId`, sin `cotizacionNexoId` o sin línea de cotización para esa línea: `LINEA_SIN_COTIZACION_NEXO`, sin llamar al POST.
- **Idempotencia de los conexos (unificada).** `POST .../servicios-conexos` usa `IClsAlmacenIdempotencia` con `ambito = "conexos:{usuarioId}"` y la huella del folio y los items; se retiró el filtro `clsIdempotenteAttribute` de la API (la API solo lee el encabezado y lo pasa al caso de uso). Su `seGuarda` guarda los éxitos y toda respuesta con `agregado_en_nexo` (`true` o `"desconocido"`): repetirla duplicaría el conexo en Nexo, que no tiene DELETE. El resto de errores no tocó Nexo y no consume la clave. Contra el doble clic sin clave, en varias réplicas o tras un reinicio protege la reserva de versión.
- **Reserva de la versión antes de Nexo.** El caso de uso sube `versionNegocio` y la guarda con el `If-Match` (`GuardarCambiosSiVersionAsync`) ANTES del POST a Nexo, como `re-solicitar` (#100). De varias peticiones con la misma versión solo una gana y llama a Nexo; las demás reciben 409 `CONFLICTO_CONCURRENCIA` sin tocar Nexo, con o sin `Idempotency-Key`, en cualquier réplica y tras un reinicio. El POST va sin el token de cancelación del cliente (no se corta a mitad). Los precios se guardan después con la versión reservada; si otro cambio (el polling) ganó en medio, 409 con `agregado_en_nexo = true`. Costo: si Nexo rechaza o no está disponible después de la reserva, la versión (y el `ETag`) ya cambió aunque nada más cambie.
- **Todo 2xx de Nexo es efecto.** Un 2xx con cuerpo vacío, 204, JSON inválido o más grande que el límite cuenta como agregado: nunca se responde `NEXO_NO_DISPONIBLE` sin marca. Se usa la `clsCotizacionDto` que devuelve el POST (también cubre una cotización `entregada`, que el polling ya no consulta) y solo se relee la cotización si el cuerpo no sirve. Si la relectura falla: 503 `NEXO_NO_DISPONIBLE` con `agregado_en_nexo = true`; con PDF sin enviar se invalida, porque el polling no toca los conexos de una cotización con PDF vigente.
- **Efecto incierto: "desconocido".** Tiempo agotado, error de red o 5xx después de enviar el POST: Cotifly relee la cotización y la compara con la lectura previa. Si aparece un conexo nuevo, termina normal (200). Si no se puede determinar, 503 `NEXO_NO_DISPONIBLE` con `agregado_en_nexo = "desconocido"`. Nunca se reintenta el POST. Las respuestas con `agregado_en_nexo` (true o "desconocido") se guardan en la idempotencia.
- **403 de Nexo.** `detalle.motivo` lleva solo el código de Nexo (o `HTTP 403`), nunca su texto.
- **Respuestas de Nexo de hasta 1 MiB** en estas tres rutas (`clsServiciosConexosNexo.LimiteRespuestaBytes`): en las lecturas, más grande es `NEXO_NO_DISPONIBLE`; en el POST es efecto sin cuerpo (se relee).

### 14. Decisiones de implementación (#102)

- **Dos caminos en `POST /web/chat` (hasta #105).** Con `folio` respondía siempre el asistente (`clsAsistenteFolioRunner`). Sin `folio`, 400 `FOLIO_REQUERIDO`, salvo con `Chat:CreacionHabilitada=true`, que volvía al agente cotizador legado (`clsAgenteRunner`, HU-003); el flag valía `false` por defecto y también apagaba Google Chat. Desde #105 el flag y el legado no existen: sin `folio`, siempre 400 `FOLIO_REQUERIDO` (§15). El folio se valida ANTES de abrir la sesión o llamar al modelo: ajeno o inexistente, 404 sin costo de tokens.
- **Plugin aparte, mismo loop.** El asistente usa su propio plugin de Semantic Kernel (`asistente`: `consultar_cotizacion`, `proponer_ajuste_margen`, `proponer_re_solicitud`, `buscar_servicio_conexo`) con el mismo loop manual de ADR-003, las declaraciones portables (#84), el filtro de auditoría y el validador de la respuesta final (HU-003.10). No usa el filtro de inyección de estado ni `clsEstadoConversacionService`: ninguna herramienta recibe identificadores del LLM; la cotización sale del contexto del turno (`clsContextoAsistenteFolio`), ya validada como del comercial. Una herramienta retirada (`generar_pdf`, `calcular_precio`...) responde `HERRAMIENTA_DESCONOCIDA` como cualquier inexistente.
- **La propuesta la arma el código, con una sola fuente de reglas.** Ninguna regla se copia: el margen usa `clsCotizacion.ValidarAjusteMargen` y el recálculo del ajuste; los conexos, `clsServiciosConexosCosteoService.Validar` y `ValidarPrecondiciones` (licitación, costeo, regla del PDF, línea inexistente y `LINEA_SIN_COTIZACION_NEXO`), que también usa el endpoint; la re-solicitud, `IClsReSolicitudCosteoService.ValidarAsync`, el mismo método que corre el endpoint antes de escribir (entrada, estado, regla del PDF, líneas, política, catálogo de Nexo G2/G3 en solo lectura y las reglas de la captura sobre la combinación, como el peso frente a las posiciones con tolerancia del 1 %). Solo quedan fuera el `If-Match` y la TRM del día, que pueden cambiar entre proponer y confirmar. `clsPropuestasAsistente` arma el cuerpo exacto del contrato, pone el endpoint con el folio de la conversación y el `row_version` vigente (`versionNegocio`) y redacta el `resumen` con plantilla, citando entre comillas (recortados a 90 caracteres) la justificación o el motivo que escribió el LLM, porque quedan en `MargenAjustes` y en la auditoría: el comercial ve lo que confirma. Las pruebas de aceptación confirman cada tipo de propuesta contra el endpoint real. La vista previa del precio usa el mismo recálculo del ajuste (`clsAjusteMargenCosteoService.Recalcular`, sobre `clsPrecioMonedaCosteo` y la TRM fijada) sin cambiar la cotización. Con un rango inválido, una justificación que falta o un estado que no lo permite, el LLM recibe el código y un mensaje sin montos, y no hay propuesta. Una propuesta por turno: la última gana.
- **El LLM aporta solo la intención y los parámetros:** margen (fracción) y justificación; motivo y cambios de carga sin dinero ni catálogos (peso, horas de cargue y descargue, exclusivo, consolidado; el resto se cambia desde el detalle); texto de búsqueda, cantidad y, si hubo varios resultados, el id elegido. Con varios servicios posibles nunca se adivina: el asistente pregunta.
- **Ningún monto llega al modelo.** El prompt (`Agente/Recursos/prompt_asistente_folio.md`) no lleva el estado del cotizador legado; las salidas de las herramientas no traen precios, costos, totales, TRM ni valores de la mercancía; el `resumen` sale en la respuesta HTTP pero no se guarda en el historial de la conversación (que sí vuelve al modelo en el turno siguiente). El estado de la sesión solo guarda el folio, el id, la versión y la moneda. Como nada respalda una cifra de dinero, el validador rechaza cualquier monto que el modelo escriba; si el turno sí armó una propuesta, sale con un texto fijo (`clsTextosAgente.PropuestaSinTexto`). Huecos del filtro y latencia del turno: §19 (#126).
- **Sesión por comercial y folio.** `spaceId = web/{sesion_id}` y `threadId = web/{sesion_id}/{FOLIO}`: el mismo `sesion_id` con otro folio es otra conversación con su propio historial y candado. La auditoría de las herramientas del asistente (`EventosHerramienta` con ese `threadId`) lleva los argumentos del LLM y la salida que vio el modelo, sin montos. Los eventos con costos (`costeo_nexo_re_solicitud`) siguen sin `ThreadId` y el asistente no lee `EventosHerramienta`.
- **El chat nunca escribe datos de negocio:** solo guarda el turno (eventos de la conversación y estado de la sesión). Conversar no cambia `versionNegocio`.
- **Transición (cerrada en #105).** Durante #102 se podía dejar `Chat:CreacionHabilitada=true` en QA y staging mientras el frontend desplegaba la versión que envía `folio`, y en Production `true` registraba un aviso al arrancar (`clsAvisoArranqueChat`). #105 retiró el flag y el aviso.
- **Código legado.** Quedó detrás del flag durante la transición y se borró en #105 junto con Google Chat (§15): `clsAgenteRunner`, `clsHerramientasCotizador`, `clsInyeccionEstadoFilter`, `clsEstadoConversacionService`, el slot filling, `clsPromptSistema` y sus pruebas. El flag `Chat:CreacionHabilitada` y `clsAvisoArranqueChat` también se retiraron.
- **Arrastre de #99.** En `NEXO_NO_DISPONIBLE` (401 incluido) y en `CONEXO_INVALIDO` el `detalle.motivo` lleva solo el código de Nexo (una palabra en mayúsculas antes de los dos puntos) o `HTTP nnn`, nunca su texto. El validador de la respuesta final trata como monto un número junto a "TRM". `clsAlmacenIdempotenciaEnMemoria` tiene un tope de 20.000 claves y purga cada 5 minutos (o al llegar al tope) en lugar de recorrer el diccionario en cada petición; lleno, desaloja por lotes (10 % del tope) las claves sin uso más próximas a vencer (queda la defensa del `If-Match`). La guía del frontend documenta que tras un rechazo de Nexo el `ETag` ya cambió.

### 15. Retiro de Google Chat y del agente legado (#105, completado)

- **Retiro completado (HU-013.9).** Se borraron el webhook `POST /chat/events` y su controlador; el verificador del JWT, el parser, la cola, el worker, el publicador, las tarjetas y el notificador de errores de Google Chat; las opciones `GChat:*` y su validación al arrancar; `ObtenerPorEmailAsync` (identidad por correo); y el agente cotizador legado completo (runner, plugin `cotizador`, filtro de inyección, slot filling, prompt `prompt_cotizador.md`, flag `Chat:CreacionHabilitada` y su aviso de arranque). `POST /web/chat` sin `folio` responde siempre 400 `FOLIO_REQUERIDO`, aunque un despliegue viejo traiga el flag en `true`.
- **Desacople previo.** Lo que compartían el legado y el asistente quedó en componentes propios: `clsEventoConversacion.RolHerramienta` (antes `clsAgenteRunner.RolHerramienta`); `clsContextoAsistenteFolio` lleva el hilo del turno que lee el filtro de auditoría (antes `clsContextoTurnoAgente`); `IClsCandadoSesion` y `clsBarridoSesionesHostedService` en archivos propios. `clsEstadoConversacion` queda solo con `FolioEnProgreso`, `CotizacionId`, `Version` y `Moneda`; el JSON legado de `Sesiones.estadoAgente` se sigue leyendo (los campos retirados se ignoran y la fila no se reescribe). `clsResolvedorMunicipios` se conserva: lo usa la validación de licitaciones (HU-013.11).
- **Validador de la respuesta final.** Como el estado ya no tiene montos, solo respaldan un monto las salidas de las herramientas del turno y lo que el comercial escribió como dinero; la previsualización nunca es válida y una acción sugerida exige su herramienta declarada (ninguna del asistente lo es).
- **Datos.** No se borra ninguna tabla. `Sesiones` y `EventosConversacion` conservan la auditoría de Google Chat (`spaceId = spaces/...`, `threadId = spaces/.../threads/...`) y siguen recibiendo las conversaciones del asistente (`web/{sesion_id}` y `web/{sesion_id}/{FOLIO}`). Las cotizaciones creadas por el chat (`estadoCosteo = null`) siguen en Mis cotizaciones, admiten `/seguimiento` y cuentan en las métricas: dependen de `Cotizaciones`, no del código retirado.
- **Contrato.** Sale `/chat/events`, el tag `chat` y el esquema de seguridad `googleChat`. `RespuestaChat.precio`, `margen` y `pdf` siguen `deprecated` (siempre `null`) por compatibilidad con el frontend; `MensajeWeb.cotizacion_origen_id` y `moneda` se aceptan y se ignoran. La URL firmada del PDF (`?t=`, ADR-011 §3) también se retiró: era una entrada anónima que solo emitía Google Chat. `GET /pdfs/{archivo}` exige siempre Bearer y sale `Pdf:ClaveFirmaUrl`/`Pdf:MinutosUrlFirmada` (con `IClsFirmaUrlPdf`, `clsFirmaUrlPdfHmac`, `clsDescargaPdfFirmadaService` y `clsOpcionesFirmaUrlPdf`). Se borró además el puerto `IClsTarifaProvider`, sin consumidor desde el retiro de `buscar_tarifa`.
- **Despliegue.** Los manifiestos ya no traen `GChat__*`, el secreto `gchat_sa` ni `Chat__CreacionHabilitada` (`verificar-manifiestos-dotnet.sh` lo comprueba). Borrar el secreto de Swarm y la app de Chat en Google Cloud queda para el despliegue, con confirmación (DEPLOY.md §7).

### 16. Decisiones de implementación (#108, HU-013.11)

- **Mismas reglas de captura en cada fila de una licitación.** `IClsValidadorFilasLicitacion` junta lo que depende de catálogos: códigos (G2, con `IClsValidadorCatalogosCaptura.ValidarLineasAsync`, la misma regla del formulario) e ids de municipio (G6). G3 vive en `clsLineaSolicitudCosteo.ErroresCondicionales` y la comparten el formulario y las filas. Todos los errores vuelven juntos en `errores[]`.
- **Municipios sin adivinar.** Si la fila no trae `origen_municipio_id`/`destino_municipio_id`, se resuelve el texto con el catálogo de `GET /web/municipios` y la regla determinista de HU-005.13. Una sola coincidencia no aproximada se usa; varias o una aproximada es `MUNICIPIO_AMBIGUO`; ninguna, `MUNICIPIO_NO_ENCONTRADO`.
- **El reenvío también valida.** El reintento y la re-solicitud (mismo folio o `-vN`) de una licitación vuelven a pasar sus filas guardadas por todas las reglas (forma, G3, catálogos y municipios). Las filas sin id que se resuelven guardan su id con el envío. Si una fila ya no pasa (una licitación guardada antes de #108 con texto libre, o un G3 inválido), responde 400 `LICITACION_INVALIDA` con `detalle.motivo = LICITACION_REQUIERE_NUEVA_CARGA` y `detalle.errores[]`, y nada llega a Nexo: ni el reintento ni la `-vN` (que copia las mismas filas) la arreglan; se carga de nuevo con `POST /web/licitaciones`.
- **Catálogos caídos.** En las licitaciones, `CATALOGOS_FLETE_NO_DISPONIBLE` y `MUNICIPIOS_NO_DISPONIBLE` son 503 (nada se guarda). Si además hay errores que se detectan sin ese catálogo (forma, G3, tipología, carrocería, clase de contenedor fija), se devuelve primero el reporte 400 con todos ellos y el código del catálogo caído en `error.detalle.motivo`: el comercial no pierde errores que puede corregir ya.
- **`GET /web/municipios` respondía 400** con `MUNICIPIOS_NO_DISPONIBLE`. Alineado a 503 como el resto de catálogos (deuda de #122, 2026-10-15): el controlador usa la traducción única de `clsRespuestasWeb`.
- **Script `0008_horas_por_linea.sql`.** Horas de cargue y descargue por línea (G5). Orden de aplicación: 0006 → 0007 → 0008.

### 17. Consistencia del costeo al entregar y al registrar el resultado (#123, HU-013.14)

**Defecto (E2E del 2026-10-07, folio `PCN-9001234567-AP-07102026-004`).** El comercial marcó el resultado (`POST .../resultado`) cuando Nexo ya había entregado pero el polling aún no lo leía. El resultado pasaba `estado_costeo` a `entregada` (entrega implícita) y detenía el polling **sin leer el costo**. La cotización quedaba en `entregada`, con `precio_final` nulo para siempre, el PDF en 409 `COSTEO_NO_LISTO` y las métricas la contaban como ganada por 0. Además, el formulario respondía `autocosteo` con `precio_final: null` hasta la primera consulta del polling.

**Regla: `autocosteo` y `entregada` solo se exponen después de que Cotifly leyó el costo de Nexo.** La lectura siempre aplica la misma regla del polling (`clsLecturaCosteo.AplicarFoto`): deriva el estado, guarda el id de la cotización de Nexo, agenda o detiene el polling y pone el precio con el margen guardado, también por línea.

- **Resultado con el costeo no aplicado** (sin `precio_final` y sin PDF):
  - Primero se escribe en Nexo, como antes.
  - Si Nexo acepta, Cotifly **relee el costeo** y guarda el resultado, el estado y el precio **en una sola escritura** con `versionNegocio` (`GuardarCambiosSiVersionAsync`).
  - Si la relectura falla, el resultado se registra igual, porque Nexo ya lo tiene. La cotización **conserva su estado de costeo** (nunca pasa a `entregada` sin haber leído el costo) y queda agendada para el polling en ese momento (`costeoSiguienteConsultaAt = ahora`), que aplica el precio en segundos.
  - **Carrera con el polling:** si el polling guarda primero, el resultado recarga, encuentra el precio y no reaplica la foto. Si el resultado guarda primero, el polling pierde la versión y su siguiente lectura no cambia nada.
  - Con el precio ya aplicado no se relee. La entrega implícita se mantiene: pasa a `entregada` y se detiene el polling.
- **Resultado en `pendiente_costeo`.** El polling espacia sus consultas según la edad de la solicitud (hasta 15 min), así que Cotifly puede no haber visto todavía un costeo que Nexo ya completó o entregó. Antes de pedirle el resultado a Nexo, Cotifly lee el costeo:
  - si Nexo ya tiene el costo, sigue la regla anterior;
  - ~~si no lo tiene, responde 409 `COTIZACION_NO_ENTREGADA` sin llamar al POST del resultado;~~ [Reemplazado por ADR-023 §6 (HU-033): si Nexo confirma que no está entregada, la `ganada` se guarda solo en Cotifly con `sincronizacionResultadoNexo = pendiente` (200) y el polling la envía cuando la Mesa entregue; la `perdida` sigue siendo la "perdida solo en Cotifly" (#127). Sin llamar al POST del resultado, como antes.]
  - si Nexo no responde, 503 sin cambio local.

  Antes respondía 409 sin preguntar. `pendiente_envio` sigue sin llamar a Nexo.
- **El polling aplica el precio aunque ya haya resultado.** El resultado no congela el precio; solo lo congela el PDF (§11).
- **Opción A para el formulario (elegida).** Si la respuesta del alta (`POST /api/captura-solicitudes`) trae todas las líneas con flete (`fletesCalculados`, `costeoCompleto`), Cotifly lee el costeo en la misma petición y responde `autocosteo` **con** `precio_final`. Antes guarda el `costeoId`: si el proceso muere, el polling la retoma. La lectura cuesta un `GET /api/solicitudes/{id}` y un "crear o recuperar" `POST /api/cotizaciones`, los mismos que haría el polling 5 s después. Si la lectura falla, o el polling ganó la versión, la cotización queda en `pendiente_costeo` y el polling la promueve.
  - Se descartó la **opción B** (un estado intermedio nuevo, p. ej. `costeado_sin_precio`). Exigía un script para el `CHECK` de `estadoCosteo`, ampliar el enum del contrato y cambiar el frontend, y solo cubría el hueco de pocos segundos que la opción A cierra sin cambios.
  - **Compatibilidad:** el enum no cambia. El formulario ya no devuelve `autocosteo` sin precio: devuelve `autocosteo` con precio o `pendiente_costeo`. Un frontend que consulta mientras el estado sea `pendiente_costeo` no cambia. `autocosteo` sin precio solo aparece si falta el margen, y entonces lleva `motivo_estado_costeo`.
- **Reparación de las afectadas.** Al arrancar, el worker del polling agenda una relectura (`IClsPollingCosteoService.ReprogramarEntregadasSinPrecioAsync`) de las cotizaciones que cumplen todo esto: `entregada`, con `costeoId`, sin `precioFinal`, sin PDF, sin `motivoEstadoCosteo` y sin agenda. El polling lee una vez cada `entregada` sin precio (`clsCotizacion.EntregadaSinPrecio`), aunque haya pasado la edad máxima de 72 h. Si Nexo no responde, reintenta con el espaciado máximo hasta leerla. Después vuelve a ser terminal. Es idempotente: una ya leída tiene precio o un motivo (sin margen, líneas sin flete) y no se vuelve a agendar. No hay script de datos.
- **Diagnóstico para el DBA (solo lectura; escrito contra el esquema anterior al `0009`: hoy la tabla es `CotizacionesSolicitud`, `costeoId` es `solicitudNexoId` y `costeoUltimaConsultaAt` no existe, ver ADR-019):**

  ```sql
  SELECT c.folio, c.usuarioId, c.costeoId, c.cotizacionNexoId, c.ganada, c.marcadoAt, c.costeoEnviadoAt,
         c.costeoUltimaConsultaAt, c.costeoSiguienteConsultaAt, c.motivoEstadoCosteo
  FROM dbo_cotifly.Cotizaciones c
  WHERE c.estadoCosteo = 'entregada' AND c.costeoId IS NOT NULL AND c.precioFinal IS NULL AND c.pdfPath IS NULL
  ORDER BY c.marcadoAt DESC;
  ```

  Las filas con `motivoEstadoCosteo` nulo son las que repara el arranque.

**Correcciones de la auditoría (#123).**

- **Menos llamadas a Nexo.** En `pendiente_costeo`, la lectura previa se reutiliza como la foto que se aplica: también aporta el id de la cotización de Nexo, así que no hace falta "crear o recuperar". Solo se le pide el resultado a Nexo si esa lectura muestra la cotización **entregada**; si no, ~~una `ganada` responde 409 `COTIZACION_NO_ENTREGADA` sin llamar al POST~~ [Reemplazado por ADR-023 §6: la `ganada` queda pendiente de sincronizar, sin llamar al POST; en `autocosteo`, un 409 `COTIZACION_NO_ENTREGADA` de Nexo también deja el resultado pendiente (200)]. En `autocosteo` sin precio, la foto se arma con la cotización entregada que devuelve el POST del resultado, siempre que todas las líneas ya tengan su id de Nexo. Solo si no hay ninguna de las dos se relee el costeo.
- **Resultado ya definido en Nexo.** Puede pasar que Nexo acepte, el guardado en Cotifly falle y el comercial pida después el resultado contrario: Nexo responde `RESULTADO_YA_DEFINIDO`. Ante ese 409, Cotifly lee el resultado de Nexo (`POST /api/cotizaciones`, "crear o recuperar") y lo guarda con `versionNegocio`. Luego responde el 409 con `detalle` = `ganada`/`perdida` (el de Nexo) y un mensaje que lo dice. Nexo es la fuente de verdad del resultado y Cotifly queda igual. Si el comercial repite el mismo resultado, Nexo responde 200 (es idempotente) y el guardado local se completa.
- **Lectura del alta con tiempo propio.** La lectura del costeo en la misma petición usa `CosteoNexo:LecturaInmediataSegundos` (6 s por defecto, de 1 a 30). Si no alcanza, responde `pendiente_costeo` y el polling sigue.
- **Tope de la relectura.** El polling relee una cotización que solo espera precio (`clsCotizacion.RequiereRelecturaCosteo`: una `entregada` sin precio, o una con resultado registrado en Nexo sin costo leído). Después de `CosteoNexo:MaximoFallosRelectura` fallos seguidos (10 por defecto), deja el motivo y para; si además pasó la edad máxima y sigue en `pendiente_costeo`, la vence. Un 404 `SOLICITUD_NO_ENCONTRADA` la detiene en la primera consulta, como antes.
- **`pendiente_costeo` con resultado y más de 72 h.** Se lee antes de vencerla: si Nexo la entregó, toma el precio; si sigue sin costeo, se vence después de leerla.
- **`entregada` leída sin precio y sin explicación** (por ejemplo, Nexo no trajo la cotización en la foto). Queda con el motivo `clsLecturaCosteo.MotivoEntregadaSinPrecio`, así la reparación del arranque no la vuelve a agendar cada vez.

**Perdida solo en Cotifly (#127, HU-013.17; decisión del usuario del 2026-10-08).** Si el cliente desiste antes de que Nexo costee, el comercial puede marcarla **perdida solo en Cotifly** con el mismo `POST .../resultado`:

- **Cuándo aplica:**
  - en `error_envio`;
  - en `pendiente_costeo` solo si la lectura previa **confirma** que Nexo no tiene la cotización entregada. Si Nexo no responde (caído o lento), responde 503 reintentable y no registra nada, igual que con una `ganada`: si Nexo ya hubiera entregado, una perdida solo en Cotifly dejaría a Cotifly y Nexo distintos para siempre, con el polling detenido.
- **Reglas:**
  - solo se admite `perdida`; una `ganada` responde 409 `COTIZACION_NO_ENTREGADA` [Reemplazado en parte por ADR-023 §6: sigue así en `error_envio`; en `pendiente_costeo` la `ganada` se guarda pendiente de sincronizar con Nexo];
  - el `motivo` es obligatorio (400 `SEGUIMIENTO_INVALIDO`);
  - `If-Match` es obligatorio: 428 sin él y 409 `CONFLICTO_CONCURRENCIA` si no coincide. Se guarda con `versionNegocio` y el 200 trae el `ETag` nuevo. En el resto de `/resultado`, `If-Match` sigue opcional por compatibilidad: si viene, se compara.
- **Efectos:**
  - no se envía a Nexo, porque allá no hay cotización entregada;
  - el polling se detiene y no se reanuda. `clsCotizacion.RegistrarPerdidaSinCosteo` deja `ganada = 0`, `motivoRechazo` y `marcadoAt`; el estado de costeo no cambia; `motivoEstadoCosteo` queda con el prefijo "Perdida solo en Cotifly ...", que la distingue (`PerdidaSoloEnCotifly`).
  - Si Nexo costea después, el resultado no cambia y el costeo no se lee.
  - Cuenta en las métricas como perdida (`ganada = 0`), sin script nuevo.
  - Queda en `EventosHerramienta` como `resultado_cotizacion_sin_costeo`.
  - Un segundo intento responde 409 `RESULTADO_YA_DEFINIDO`. El reintento del envío ya no aplica, porque exige que no haya resultado.
  - `/seguimiento` sigue respondiendo `USAR_RESULTADO_NEXO` en esos estados: el camino es `/resultado`. Las que tienen motivo se leyeron y no admiten precio (revisar el margen de la línea o las líneas sin flete con Nexo). Mientras una fila aparezca aquí, las métricas la cuentan sin valor: `valor_ganado_cop` queda subestimado.

### 18. Avisos en la re-solicitud y en las licitaciones (#124, HU-013.15)

Hallazgos de las pruebas E2E del 2026-10-07.

- **Los conexos se descartan, se avisan y no se re-agregan.** *(Rehecho en #136 con la decisión 3 de ADR-019: los conexos se conservan en el mismo folio, se copian a la `-vN` y los envía a Nexo el envío diferido; lo que sigue describe la regla de #124.)* Re-solicitar es una captura nueva en Nexo (§5): la cotización nueva de Nexo empieza sin conexos. En el mismo folio los conexos del costeo anterior se pierden (`PrepararReSolicitud` limpia `serviciosConexos`; quedan en la instantánea de la auditoría) y la `-vN` no los copia (la original conserva los suyos). La regla:
  - la respuesta de `re-solicitar` (200 o 201) trae siempre `conexos_descartados` (`CotizacionResumen`, solo en este endpoint): los nombres sin repetir, sin montos, `[]` si no había. **Solo los incluidos** (`incluido = true`): uno excluido no iba en el PDF ni en el precio para el cliente, así que no se pierde nada con él. También van en la fase `resultado` de la auditoría;
  - la propuesta `re_solicitar_costeo` del asistente los avisa en el `resumen` que el comercial ve ANTES de confirmar ("Se descartan los servicios conexos «Cargue», …: agrégalos de nuevo cuando llegue el costeo nuevo"; en la `-vN`, "La versión nueva no lleva…"). El aviso tiene espacio reservado dentro de los 300 caracteres: va después de la acción y antes del motivo (texto del LLM), y siempre cabe completo con "y N más" y "agrégalos de nuevo". Para eso nombra hasta 3 conexos (cada nombre recortado a 40 caracteres) y nombra menos si no cabe (en el límite, solo los cuenta), y en la `-vN` la descripción de los cambios de carga se acorta antes (o queda en "con cambios de carga"); solo el motivo y lo que sigue pueden recortarse. La salida de `proponer_re_solicitud` lleva los mismos nombres, recortados a 40 caracteres y nunca con montos, para que el modelo lo mencione;
  - **Cotifly no los vuelve a agregar en Nexo por su cuenta.** Nexo no tiene DELETE de conexos ni idempotencia en `POST .../conexos` (`docs/conexion_nexo/HU-013-preguntas-nexo.md`, preguntas 9 y 13): un re-agregado automático que falle a medias o se repita dejaría conexos duplicados o cobrados sin que el comercial los confirme, y el precio del conexo puede haber cambiado. Además el costeo nuevo todavía no existe al responder (los conexos se agregan sobre la cotización de Nexo con el costeo listo, `COSTEO_NO_LISTO`). El comercial los agrega de nuevo con `POST .../costeo/servicios-conexos` (o la propuesta del asistente) cuando llegue el costeo. Se descartó "ofrecer volver a agregarlos" como una acción automática por las mismas razones; el frontend puede precargar el buscador con los nombres.
- **Numeración de filas: la del Excel del comercial.** `fila` (en `LineaLicitacionWeb`, `ErrorFilaLicitacion` y `LineaLicitacion`) sigue siendo el índice base 0 en `hojas[h].filas` (contrato y dato guardado en `CotizacionLineasSolicitud.filaOrigen`; cambiarlo rompería el frontend, las licitaciones guardadas y la exportación). Lo que ve el comercial es el **número de fila de la hoja de Excel** (base 1, encabezado incluido):
  - `analizar-excel` agrega `hojas[h].filas_excel` (el número de fila de Excel de cada `filas[i]`), calculado con el puerto de la detección de Nexo (`IClsLectorExcelLicitacion.FilasDeDatosPorHoja`, una sola lectura del libro). Se compara con el total real de la hoja (`total_filas`), porque Nexo corta `filas` en 1000 (Cotifly también se detiene en 1000), y se toman tantos números como filas devolvió Nexo. Una hoja que no cuadra, o que Cotifly no puede ubicar (por ejemplo, con demasiadas combinadas), queda en `null` sin afectar a las demás; el análisis nunca falla por esto;
  - `ErrorFilaLicitacion.fila_excel`: la fila de Excel de la línea con error, con el archivo adjunto y una `fila` que existe en la hoja; si no, `null`;
  - los `mensaje` nombran la fila del Excel o, sin ella, la línea en base 1 (`indice + 1`); nunca el índice `fila`. Ejemplo: "La línea 4 apunta a una fila que no está en la hoja 'Pliego': la hoja tiene 3 filas con datos (de la fila 2 a la 4 del Excel)." en lugar de "La fila 3 no existe (3 filas con datos)". `indice` se mantiene.
- **La línea "Licitaciones" de Nexo no es una línea de Cotifly.** Existe en `GET /api/catalogos/lineas-negocio` de Nexo pero no en `MargenesPolitica`: no tiene margen ni `mapeoLineasNexo`. Una licitación se crea con la línea de Cotifly que le da el margen (la misma para todas sus filas: las filas del pliego no traen línea de negocio propia) y en Nexo viaja su `mapeoLineasNexo` (ADR-017 §3). Con "Licitaciones" (o cualquier línea desconocida) `POST /web/licitaciones` responde 400 `MARGEN_LINEA_DESCONOCIDA` con un mensaje que dice qué líneas usar y `detalle.lineas_validas` (las activas; también con una línea inactiva); con "Licitaciones", además `detalle.motivo = LINEA_DE_NEXO`. No se agrega un mapeo a `Licitaciones` en Nexo hasta que negocio lo defina: cambia la tarifa y la bandeja de la Mesa (pregunta 16 de `docs/conexion_nexo/HU-013-preguntas-nexo.md`). No hay cambio de esquema.
- **`analizar-excel` sin texto crudo de Nexo.** Su `detalle.motivo` (503 `COSTEO_NEXO_NO_DISPONIBLE`, 403, 413 y otros 4xx) lleva solo el código de Nexo (`SESION_INVALIDA`) o `HTTP {estado}` si Nexo no manda código, con la misma regla de los conexos (`clsServiciosConexosNexo.SoloCodigo`); el texto completo queda en el log. Los 400 de archivo (`EXCEL_INVALIDO`, `EXCEL_SIN_DATOS`…) conservan el mensaje de Nexo, que describe el archivo del comercial.

### 19. Filtro de montos y latencia del asistente (#126, HU-013.16)

Complementa §2b y §14. Ninguna regla de seguridad cambia: el modelo sigue sin recibir montos, solo propone, y toda respuesta que llega al comercial pasa por las mismas reglas de `clsReglasRespuestaAgente`.

- **Huecos del filtro de montos, corregidos.**
  - Miles separados con espacio normal, duro o fino (`$1 109 756`): el número se partía y el `$1` quedaba respaldado por `"version": 1` de `consultar_cotizacion`, así que el monto pasaba.
  - Dígitos de otros sistemas (`３.４００.０００`, árabes): `\d` los encontraba, pero el número no se podía leer y se descartaba. Ahora se pasan a ASCII, y un número ilegible cuenta como monto sin respaldo.
  - Montos en letras ("tres millones", "dos mil quinientos pesos", "medio millón"): antes eran un falso negativo aceptado (§10.6). Solo los respalda el mismo texto en un mensaje del comercial o en una salida de herramienta.
  - Sufijos `mil`, `k` y `mil millones`; `EUR` y `€`; signo menos (`-150.000 pesos`).
  - Palabras de dinero `monto`, `importe`, `presupuesto`, `cuesta`, `vale`, `tasa` y `tipo de cambio`.
- **Respaldo de un monto (corrección tras la validación de #126).** Las herramientas del asistente no le entregan montos al modelo, así que **ningún campo de sus salidas respalda dinero**. Antes, `version: 1`, `cantidad: 2` o el margen 0,15 redondeado a 0 dejaban pasar "$1", "USD 2", "$0,15", "$0" y "$ 1 . 400 . 000". Ahora:
  - Un número con marca de dinero (símbolo, moneda o palabra de dinero) solo pasa si su valor es **exactamente**, sin redondear, el de un monto que el comercial escribió como dinero. Si el comercial escribió TRM 4.123,45 y el modelo escribe 4.123,46, se rechaza.
  - Antes de leer, el texto se normaliza (`clsReglasRespuestaAgente.Normalizar`): dígitos Unicode a ASCII, separadores de ancho completo y árabes a punto o coma, espacios y saltos de línea dentro de un número quitados, y espacios de miles convertidos en punto. Así "3⏎400⏎000 pesos" o "$١٬٤٠٠٬٠٠٠" se leen enteros.
  - Una cifra sin marca de dinero que forma parte de un texto completo de las salidas pasa, como "3M" dentro de "3M Colombia". Si va junto a "precio", "vale" o "cuesta", nunca pasa.
  - Una palabra de dinero justo antes del número ("precio 950", "el valor queda en 3,4") vuelve monto a cualquier cifra, también a las menores de 1.000. "Doscientos mil" sin moneda cuenta como monto si va tras una palabra de dinero.
  - Ya no se bloquean: un número tras "radicado", "orden de compra", "pedido", "factura" o "guía", y una fecha con puntos (08.10.2026).
- **Tipo de la respuesta.** Lo decide el servidor: con una propuesta armada siempre es `confirmacion_requerida`, y sin propuesta nunca lo es (`clsAsistenteFolioRunner.Coherente`). El prompt agrega: "no presentes como calculado un valor que escribió el comercial".
- **Tope de espera del limitador.** `IA:EsperaMaximaRpmSegundos` vale 20 por defecto. Si el cupo de `IA:Rpm` tardaría más, la llamada no espera: lanza un 429 local y el comercial ve de inmediato `clsTextosAgente.LimiteModelo`. Antes esperaba hasta 60 s sin ningún mensaje.
- **Respaldo de modelos y "IA no disponible" (E2E de #126).**
  - **El 404 pasa al siguiente modelo.** OmniRoute caído devolvía el 404 de Traefik y `clsChatClientConRespaldo` no probaba los respaldos. Ahora pasan al siguiente modelo el 404 (ruta o modelo), el 408, el 429, los 5xx y los errores de red. Un 400 de contenido no pasa.
  - **Mensaje propio cuando todo falla.** Si fallan todos los modelos por indisponibilidad (no por cuota), se lanza `clsIaNoDisponibleException` y el chat responde `RespuestaChatError` con el texto `clsTextosAgente.IaNoDisponible` y `codigo: IA_NO_DISPONIBLE`.
  - **Códigos de `RespuestaChatError.codigo`.** `IA_NO_DISPONIBLE`, `LIMITE_MODELO`, `TURNO_EN_CURSO`, `MAX_ITERACIONES`, `RESPUESTA_NO_ESTRUCTURADA` y `ERROR_INTERNO` (contrato y guía del frontend).
  - **Un solo proveedor.** El decorador de respaldo se usa también con un solo proveedor, para que su caída llegue con el mismo código.
- **Falsos positivos que se evitan.**
  - Unidades `metros`, `kilómetros`, `posiciones` y `viajes`.
  - Un fragmento que aparece tal cual en un texto de las herramientas, como `3M` en "3M Colombia". Las salidas de las herramientas nunca traen montos, así que esto no abre un respaldo de dinero.
- **`clsNumerosDelMensaje`.** Queda como lector de valores posibles de un número, con cobertura del 100 % de líneas y ramas. `Numeros` y `Contiene` se retiraron: eran del costo manual del agente legado y no tenían uso desde #105.
- **Latencia.** En E2E (lote 2) cada turno tardaba de 17 a 24 s con 3 llamadas al modelo. La causa principal era el limitador `IA:Rpm`: el port del Python espaciaba todas las llamadas 60/rpm segundos (7,5 s con 8), aunque el minuto tuviera cupo, así que un turno de 3 llamadas esperaba unos 15 s solo por el limitador. Cambios:
  - **Ventana deslizante en `IA:Rpm`.** Pasan hasta `Rpm` llamadas por cada 60 s, sin espaciarlas. Sigue siendo un tope global del proceso: con 8, unos 4 turnos por minuto para todos los comerciales. Se ajusta al plan del proveedor.
  - **`IA:PropuestaDirecta`** (`true` por defecto). Cuando una ronda arma una propuesta, el turno va directo a la respuesta final, sin otra llamada con herramientas (una propuesta por mensaje).
  - **`IA:RespuestaFinalEnLoop`** (`true` por defecto). La respuesta sin herramientas se valida como respuesta final con las mismas reglas. Si pasa, no hay llamada final aparte; si no, la pide el validador como antes. El prompt pide ese JSON al cerrar y ya no exige consultar antes de proponer.
  - **Sin reintento del validador** cuando el turno ya armó una propuesta: su respaldo es el texto fijo `PropuestaSinTexto`.
  - **Resultado.** Un turno de propuesta pasa de 3 a 2 llamadas y una consulta de 3 a 2; un saludo, a 1.
  - **Medición** (`docs/10-operacion.md`, "Latencia del asistente"). No hubo acceso a OmniRoute durante #126, así que se midió con el runner, el validador y el limitador reales, y un proveedor simulado de 1,5 a 3,5 s por llamada, en 10 turnos por escenario. En los 10 turnos de cada escenario la herramienta armó la propuesta `ajustar_margen`. El p50 bajó de 17,3 a 4,9 s y el p95, de 18,3 a 5,8 s. **Pendiente:** confirmar con OmniRoute desde la red interna que el p95 queda por debajo de 8 s.
- **Modelo más rápido para elegir herramientas.** Se evaluó y no se implementó: `gemini-3.5-flash-lite` ya es el modelo más liviano de la cadena, y el tiempo se iba en el limitador y en el número de llamadas, no en el modelo. `MaxTokens` no se cambia (el tope no acelera respuestas cortas); el prompt pide un `texto` de hasta tres frases.
- **Observabilidad.**
  - Cada turno registra una línea "Turno del asistente de {folio}" con la duración total y por fase (preparación, modelo con herramientas, herramientas, respuesta final y guardado), las llamadas por fase, las iteraciones, el cierre (`loop` o `llamada_final`), los reintentos del validador, el respaldo, la propuesta, los tokens y los modelos que respondieron. Un modelo distinto del principal indica un respaldo. La línea no lleva textos.
  - La espera del limitador se registra aparte ("esperó N ms por IA:Rpm").
- **Rutas probadas.**
  - **Turno concurrente.** Dos turnos de la misma sesión: el segundo recibe `clsTurnoEnCursoException`, que el canal responde con `TurnoEnCurso`, sin llamar al modelo ni guardar su mensaje.
  - **Modelo que pide herramientas sin parar.** Se corta en `IA:MaxIteraciones` con `clsMaxIteracionesExcedidasException`; el comercial recibe `clsTextosAgente.MaxIteraciones` y las rondas quedan en la conversación. Una propuesta repetida ya no llega al tope, porque la propuesta cierra la ronda.

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
| Issue de gaps del formulario y migración de catálogos (`docs/conexion_nexo/HU-013-preguntas-nexo.md` §Gaps) | Coordinador + Duban |
| Compartir el contrato con el desarrollador del frontend | Duban |
| ~~Alinear `GET /web/municipios` a 503 con `MUNICIPIOS_NO_DISPONIBLE`~~ Hecho en el backend (2026-10-15); falta que el frontend trate el 503 | Frontend |
| Script `0008_horas_por_linea.sql` (#108): pendiente de aprobación; se aplica después del 0007 | Duban + DBA |
| ¿Una licitación debe viajar a Nexo con la línea `Licitaciones` (y el margen salir de la línea de Cotifly)? Hoy viaja el `mapeoLineasNexo` de la línea elegida (§18, pregunta 16) | Negocio + equipo de Nexo |
