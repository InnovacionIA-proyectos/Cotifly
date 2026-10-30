# ADR-004 — Estado de conversación estructurado (slot-filling)

- **Estado:** Aceptada (las columnas nuevas en BD dependen del script 0002, ver Pendientes)
- **Nota de estado (HU-013.9, #105):** retirada en la práctica junto con el agente cotizador legado y Google Chat. Se borraron `clsEstadoConversacionService`, `clsInyeccionEstadoFilter`, `clsHerramientasCotizador` y `clsAgenteRunner`; `clsEstadoConversacion` queda solo con `FolioEnProgreso`, `CotizacionId`, `Version` y `Moneda` (el JSON antiguo de `Sesiones.estadoAgente` se sigue leyendo y los campos retirados se ignoran). El asistente por folio de ADR-018 no necesita slot-filling: opera sobre una cotización ya armada por el pipeline determinista. Lo que sigue es la decisión histórica.
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #12 (HU-003.6), tanto en `/web/chat` como en Google Chat.

## Contexto

Causa raíz confirmada en Python: el id del cliente no llega de forma confiable desde el chat hasta `obtener_margen` y `generar_pdf`. Hay dos motivos:

- **Tres nombres para el mismo dato:** `cliente`, `cliente_id` y `cliente_id_sap`, sin mapeo programático entre ellos. `mcp_server/server.py` los adivina con cadenas de `or`.
- **El historial se reconstruye solo como texto** entre turnos (`orquestador/agente.py:366-380`, `_messages_desde_historial`), sin los tool-calls ni sus resultados. Como `generar_pdf` ocurre en un turno posterior a la confirmación, el LLM tiene que re-inferir el id desde el texto.

## Decisión

**1. Identificador único de cliente: `nitCliente`** (el NIT, igual que `Cotizaciones.nitCliente`), acompañado de `origenCliente` (`cliente | prospecto`). Es el único nombre de campo en herramientas, DTOs y estado. Desaparecen `cliente`, `cliente_id` y `cliente_id_sap`.

**2. Value object `clsEstadoConversacion`** (Dominio), uno por sesión:

| Grupo | Campos |
|---|---|
| Cliente | `NitCliente`, `ClienteNombre` (nombre oficial del catálogo), `OrigenCliente`, `BpCliente` (nullable, solo en el estado, **no se persiste** en `Cotizaciones`), `ClienteConfirmado`, `CandidatosCliente[]` |
| Solicitud | `LineaNegocioId`, `LineaNegocio`, `Origen`, `Destino`, `TipoVehiculo`, `TipoCarga`, `PesoKg` |
| Resultado | `Costo`, `CosteoId`, `MargenPolitica`, `MargenAplicado`, `OrigenMargen`, `MetodoCalculo`, `PrecioFinal`, `Moneda` |
| Documento | `FolioEnProgreso`, `CotizacionId`, `Version`, `ConfirmacionPendiente` |

**3. Quién lo actualiza:** solo `clsEstadoConversacionService`, de forma determinista, a partir de dos fuentes:

- (a) la salida de una herramienta que terminó sin error. Por ejemplo, `buscar_cliente` con coincidencia exacta llena `NitCliente`, `ClienteNombre`, `OrigenCliente` y `BpCliente`; `obtener_margen` llena el margen y su origen;
- (b) una confirmación explícita del usuario que el runner reconoce.

El LLM **nunca** escribe el estado.

**4. El orquestador, no el LLM, propaga los ids.** El `IFunctionInvocationFilter` `clsInyeccionEstadoFilter` corre en cada `kernel.InvokeAsync` (ADR-003):

- Sobrescribe los argumentos de identidad con los valores del estado: `nitCliente`, `origenCliente`, `lineaNegocioId`, `cotizacionId`, `spaceId` y `threadId`. Si el LLM mandó otro valor, gana el estado y queda en el log.
- `usuarioId` sale siempre del token o del usuario resuelto.
- **Margen SAP:** en `obtener_margen`, el filtro inyecta `BpCliente` como cliente de la consulta SAP TM (`OrderPartyId`).
  - Si no hay BP (prospecto o cliente sin búsqueda), **no** se consulta SAP con el NIT.
  - En ese caso se cae a `politica_linea` y el motivo se registra en `detalle`.
- Los argumentos de texto libre (origen, destino, texto de búsqueda) sí vienen del LLM.
- **Números del estado (anotado en #10, se implementa en #12):** el filtro también sobrescribe `MargenPolitica`, `MetodoCalculo`, `Costo` y `MargenAplicado` desde el estado en `calcular_precio`, `registrar_ajuste_margen` y `generar_pdf`: el LLM no transcribe números (ADR-003 §2). `obtener_margen` los llena a partir de `clsMargenResuelto` (`Salida.Margen`, `MargenPolitica`, `MetodoCalculo`, `LineaNegocioId`) y el ajuste negociado a partir de `clsSalidaAjusteMargen`.
- **Margen negociado:** si el comercial negoció (`clsAjusteMargenService`, origen `negociado`), la creación de la cotización debe persistir `Cotizaciones.origenMargen = negociado` con el `margenAplicado` guardado en `MargenAjustes` y el `margenPolitica` de referencia, sin volver a resolver la política.

Además, el estado se inyecta en el prompt de sistema como un bloque compacto "Estado resuelto", para que el LLM redacte con los datos correctos. Ese bloque es informativo; la fuente de verdad es el filtro.

**5. Coincidencias por similitud.** Si `buscar_cliente` no encuentra una coincidencia exacta:

- guarda `CandidatosCliente`, deja `ClienteConfirmado = false` y el agente pide confirmación;
- mientras el cliente no esté confirmado, `generar_pdf` responde `CONFIRMACION_REQUERIDA`;
- si el comercial rechaza las candidatas, no se acepta texto libre: se remite al buscador de HU-005.2.

El mismo patrón aplica a ciudades (HU-005.13).

**6. Persistencia.**

- El estado se serializa como JSON en `Sesiones` y se carga al inicio de cada turno.
- El historial que se entrega al LLM incluye los mensajes de herramienta del turno. Los tool-calls ya se guardan en `EventosConversacion.herramientas`.
- Se mantiene la convención de Python para las sesiones web: `spaceId = threadId = "web/{sesion_id}"`.
- Una sesión queda ligada a su usuario: si otro usuario la usa, recibe 404 (contrato #17).

**Columnas nuevas** en `dbo_cotifly.Sesiones`, vía el script `db/migrations_mssql/0002_identidad_estado_agente.sql` (ADR-008):

- `estadoAgente` JSON, con `CHECK (estadoAgente IS NULL OR ISJSON(estadoAgente) = 1)`;
- `usuarioId` nullable, con FK a `UsuariosWeb`.

El DDL definitivo queda en el script.

### Números del PDF desde el estado (#11)

`generar_pdf` recibe el payload que redacta el LLM, pero ningún número del PDF puede salir de él (ADR-003). El filtro de invocación de herramientas de #12, antes de llamar a `IClsGenerarPdfService`:

- sobrescribe en cada fila del payload `costo`, `margen` y `tarifa` con los valores del estado (resultado de `calcular_precio`, o del ajuste negociado), y los `precio` de `servicios_conexos` con los del costeo (Nexo, HU-005; mientras tanto, los del formulario);
- arma `clsTrazabilidadCotizacion` (costo, márgenes, `origenMargen`, método, `PrecioFinal`, moneda, TRM, NIT) desde el estado, nunca desde el payload;
- pone el folio que genera el servidor (ADR-011); la `referencia` del payload se ignora.

Como defensa, `clsGenerarPdfService.ValidarCoherencia` rechaza con `PDF_PAYLOAD_INVALIDO` un payload cuya tarifa impresa (dada o calculada con costo y margen) no sea el `PrecioFinal` de la trazabilidad, un `PrecioFinal` que no salga de costo y margen aplicado, o filas y servicios en otra moneda que la de la cotización (HU-012.3). `Cotizaciones` guarda un solo precio: una cotización con varias tarifas distintas (varios vehículos) no puede traer `PrecioFinal` hasta que se decida cómo guardar un precio por fila.

### Implementación (#12, parte C)

- `clsEstadoConversacion` suma dos campos: `MargenSugerido` (el que propuso `obtener_margen`, referencia de un ajuste negociado; `MargenPolitica` queda como la política de la línea que se guarda en `Cotizaciones`) y `FuenteCosto` (fuente de `buscar_tarifa` o `manual`). `clsCandidatoCliente` suma `Bp` (nulo en prospectos) para que elegir un candidato llene el BP.
- `clsEstadoConversacionService` (Aplicación, único que escribe el estado) aplica las salidas exitosas y reconoce en el mensaje del comercial la elección de un candidato (por NIT, nombre o posición) y la confirmación explícita ("Confirmo", "sí", "dale"...), que solo vale sobre una previsualización pendiente (`ConfirmacionPendiente`, que deja `calcular_precio`) y solo en su turno. Elegir un candidato no confirma el PDF.
- `clsInyeccionEstadoFilter`: además de lo descrito arriba, el costo que escribió el comercial (sin Nexo, `buscar_tarifa` responde `TARIFA_FUENTE_NO_DISPONIBLE`) sí llega del mensaje a `calcular_precio`; un costo de la fuente siempre gana. Sin margen resuelto, `calcular_precio` responde `PRECIO_INVALIDO` (el LLM no inventa márgenes). En `generar_pdf` se quitan también los `servicios_conexos` del payload: sus precios vendrán del costeo de Nexo (HU-005).
- Correcciones de la auditoría de #12: si cambia el cliente (NIT o referencia), la línea o la ruta, el estado olvida `CotizacionId`, `FolioEnProgreso` y `Version` (la siguiente cotización es nueva, no una `-vN` de otra); un costo manual solo vale si el número está en el mensaje del turno (o es el que el comercial ya dio); `cotizacion_origen_id` de `MensajeWeb` siembra el estado desde una cotización propia para una nueva versión; un turno a la vez por sesión (`sp_getapplock` de sesión).
- El identificador de los argumentos que inyecta el filtro no es un parámetro de las herramientas (el modelo no lo ve en el esquema): va en `KernelArguments` (`clsArgumentosHerramienta`) y, si el LLM lo manda, se pisa.
- Persistencia: el mensaje del comercial se guarda al empezar el turno; los eventos del loop (`assistant` con `invocaciones`, `herramienta` con `resultados`) y el estado, al final, sobre la sesión releída (el candado del folio deshace su transacción y limpia el seguimiento de cambios si la creación falla). Marcas de tiempo estrictamente crecientes para el orden.
- `chat_slot_filling.feature` reproduce el hallazgo por HTTP con SQL Server real y SAP TM de muestra: turno 1 identifica al cliente (BP 15927) y obtiene el margen de SAP con el BP aunque el LLM mande el nombre; turno 2 "Confirmo" persiste la cotización con el NIT, la línea, el margen `sap_tm` y el precio del estado aunque el payload no los traiga. Pruebas unitarias en `clsInyeccionEstadoTests` y `clsEstadoConversacionServiceTests`.

### Origen y destino contra el catálogo de municipios (HU-005.13, #43)

El comercial escribe la ciudad en texto libre y hay municipios con el mismo nombre en varios departamentos ("La Unión", "San Pedro"). Se aplica el mismo patrón que a los clientes candidatos:

- **Herramienta nueva `resolver_municipio(texto, campo)`** en el plugin `cotizador`, no resolución implícita dentro del servicio de estado: el modelo decide cuándo hay una ciudad nueva y el servicio de estado sigue siendo determinista. La herramienta lee el catálogo con `IClsMunicipiosProvider` y lo resuelve con `clsResolvedorMunicipios` (Aplicacion). El catálogo de Nexo no tiene filtro (el mapeo aprobado reemplazó la cascada `/departamentos` → `/ciudades?q=` de la issue), así que la búsqueda se hace en Cotifly.
- **Estado:** `OrigenMunicipio`/`DestinoMunicipio` (id, municipio, departamento, país, código Dane) y `CandidatosOrigen`/`CandidatosDestino`. Una coincidencia deja el texto canónico `Municipio, Departamento` en `Origen`/`Destino`; varias dejan los candidatos; ninguna no escribe nada. La elección del comercial ("la de Nariño", "2") la reconoce `ReconocerMensaje`, como la de un cliente.
- **Filtro:** con candidatos sin elegir, `buscar_tarifa`, `calcular_precio`, `registrar_ajuste_margen` y `generar_pdf` responden `CONFIRMACION_REQUERIDA`; el validador (#16) rechaza una `previsualizacion` en ese estado. En `buscar_tarifa` el municipio verificado va en su forma canónica.
- **Sin catálogo** (`MUNICIPIOS_NO_DISPONIBLE`: Nexo sin configurar, caído o el comercial sin `idUsuarioExterno`) el texto libre queda como ruta y nada se bloquea.
- **Al modelo** van `campo`, `texto`, `coincidencia`, el municipio o los candidatos por nombre (municipio, departamento, país) y `total` (cuántas coincidencias hubo, que puede superar los 8 candidatos mostrados). Sin id ni código Dane. `total` es el único número y es pequeño (no llega a 1.000, así que no respalda montos en el validador).
- **Argumentos inválidos:** un `campo` que no es `origen`/`destino` o un `texto` vacío responden `MUNICIPIO_ARGUMENTO_INVALIDO` sin consultar Nexo, para que el modelo corrija la llamada.
- **Elección del comercial:** criterio conservador. No elige con una negación ("no es el de Nariño"), ni si alguna palabra del mensaje no es de los candidatos (municipio, departamento, país), del campo, de la posición o de una lista de relleno ("va para medellin antioquia", "el cliente es de antioquia"), ni si nombra solo el campo que no está pendiente; en esos casos el modelo vuelve a llamar a `resolver_municipio`. Con origen y destino pendientes elige por departamento solo si coincide en una sola lista o si el mensaje nombra el campo. De departamentos anidados gana el más largo ("Valle del Cauca" sobre "Cauca"). Un "Confirmo" no confirma la previsualización mientras haya municipios por elegir.
- **Hacia adelante:** la fuente de tarifas (y el costeo de Nexo cuando llegue, HU-005.5) recibe en `origen`/`destino` el texto canónico `Municipio, Departamento` (`Bogotá, D.C.` sin repetir el departamento), no el id del catálogo. `generar_pdf` también toma del estado el origen de la tabla de tarifa y el destino cuando la tabla tiene una sola fila; con varias filas (varios destinos) el destino de cada fila sigue siendo el que escribió el modelo.
- **Caché:** el catálogo se reutiliza `Nexo:MinutosCacheMunicipios` (60 por defecto) en una foto singleton de Infraestructura (`clsMunicipiosEnCache`), sin `IMemoryCache` para no sumar un paquete por un solo valor. Nunca guarda un catálogo vacío ni un fallo; las consultas simultáneas comparten la misma consulta a Nexo ([10-operacion](../../docs/10-operacion.md)).

## Alternativas consideradas

- **Reenviar todo el historial con tool-calls, sin estado:** sigue dependiendo de que el LLM copie bien el id, y el prompt crece con cada turno.
- **Guardar el estado en memoria del proceso:** se pierde al reiniciar y no funciona con más de una réplica.
- **Tabla relacional de slots:** es rígida ante campos nuevos. JSON validado con `ISJSON` alcanza, porque solo lo lee el backend.

## Consecuencias

- `chat_slot_filling.feature` reproduce el hallazgo: cliente identificado en el turno 1, "Confirmo" en el turno 2, y `generar_pdf` persiste el `nitCliente` correcto sin que el LLM lo mande.
- Mientras `buscar_cliente` sea stub (ADR-005), no hay NIT ni BP:
  - la cotización se guarda con `nitCliente = NULL`;
  - el margen sale de `politica_linea`;
  - el folio va sin segmento de cliente (ADR-011).
- **Choque con HU-003.3:** el script 0002 va contra la indicación "no rediseñar el esquema aquí". Se justifica porque son tablas y columnas nuevas que el diseño .NET necesita, no un rediseño de lo existente. El DDL definitivo queda sujeto a la revisión de datos.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Revisar y aprobar el DDL de `0002_identidad_estado_agente.sql` (columnas nuevas de `Sesiones`) | Sara + scalderon-bs (redacta Duban) |
| Confirmar con HU-009.1 que el `bp` que devuelve Nexo es el `OrderPartyId` de SAP TM | Duban + equipo Nexo |
| Cómo presentar el buscador en Google Chat tras rechazar candidatas (tarjeta o remisión a la web) | Negocio + Sara |
| Validar `resolver_municipio` con un municipio ambiguo real contra Nexo (RM-15 de `docs/qa/regresion-manual.md`) | Kevin (QA) + Duban |
| Confirmar con Nexo el host de `Nexo__UrlBase` (sin `/api`: el código agrega `/api/captura-solicitudes/...`) | Duban + equipo Nexo |
