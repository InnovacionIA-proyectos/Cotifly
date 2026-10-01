# ADR-003 — Orquestación de IA agnóstica al proveedor y principio "el LLM solo redacta"

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #12 (HU-003.6) y #16 (HU-003.10).

## Contexto

El loop agéntico se migra a .NET (decisión ya marcada en #7). Cubre el Asistente de `/web/chat` y el webhook de Google Chat. Falta documentar el mecanismo concreto; el issue sugiere Semantic Kernel o un cliente HTTP directo equivalente a LiteLLM.

Situación en Python:

- Usa LiteLLM (multi-proveedor) desde `orquestador/agente.py`.
- Invoca las herramientas del `mcp_server` por MCP.

Además:

- Ya está decidido que el LLM se limita a redactar (ítem marcado en #7).
- HU-003.10 pide validar las respuestas contra un esquema (Instructor o un equivalente .NET). El Python actual no valida la salida del LLM.

## Decisión

**Un solo camino de invocación:**

- **Modelo:** el agente habla con el modelo a través de `IChatClient` (Microsoft.Extensions.AI).
- **Herramientas:**
  - Se registran como `KernelFunction` en un Kernel de **Semantic Kernel** (plugin `cotizador`).
  - Se exponen al modelo con `KernelFunction.AsAIFunction()`.
- **Loop manual en `clsAgenteRunner`:**
  - Corre hasta `IA:MaxIteraciones`.
  - **No** se usa `UseFunctionInvocation()`.
  - Por cada `FunctionCallContent` de la respuesta, el runner:
    1. valida el nombre de la herramienta (si no existe, error `HERRAMIENTA_DESCONOCIDA`);
    2. la ejecuta con `kernel.InvokeAsync(function, args)`.
  - Así corren siempre los `IFunctionInvocationFilter`: inyección de estado (ADR-004) y auditoría en `EventosHerramienta`.
  - `IAutoFunctionInvocationFilter` no aplica: no se usa la invocación automática de SK.

**Proveedor por configuración:**

- Claves: `IA:Proveedor`, `IA:Modelo`, `IA:ApiKey`, `IA:Endpoint`.
- `clsChatClientFactory` construye el `IChatClient` para `openai | azure_openai | anthropic | gemini | ollama | openai_compatible`.
- `IA:Fallbacks` activa el decorador `clsChatClientConRespaldo`, que cambia de proveedor ante errores 429 o 5xx.
- `IA:Rpm` limita las peticiones por minuto.
- Las versiones de SK y MEAI se fijan en `Directory.Packages.props`.
- Solo `Infraestructura/IA` conoce las implementaciones concretas; Aplicación depende de las abstracciones.

**Principio de diseño del agente (regla obligatoria):**

1. El LLM **solo** redacta la respuesta en lenguaje natural y **elige** qué herramienta invocar y con qué argumentos de texto (por ejemplo origen, destino o el texto de búsqueda de un cliente).
2. El LLM **nunca** consulta datos: no genera SQL, no lee tablas, no calcula precios ni márgenes. Toda lectura la hacen herramientas deterministas: `buscar_tarifa`, `obtener_margen`, `calcular_precio`, `generar_pdf`, `enviar_correo`, `registrar_ajuste_margen`, `consultar_cotizacion` y `buscar_cliente`.
3. Los identificadores (cliente, BP, línea de negocio, cotización, usuario, sesión) los propaga el orquestador desde el estado estructurado (ADR-004).
4. Los datos que deben ser reales (teléfono del comercial, folio, NIT) se leen de la base de datos o los genera el servidor.
5. Las acciones con efecto externo (el PDF final y el correo) exigen confirmación explícita del usuario (`CONFIRMACION_REQUERIDA`).

**Salida estructurada (HU-003.10):**

- Terminado el loop de herramientas, la respuesta final se pide en **una llamada aparte, sin herramientas**, con `ChatResponseFormat.ForJsonSchema` sobre `clsRespuestaAgenteEstructurada { tipo: conversacional | previsualizacion | confirmacion_requerida | error, texto, requiere_confirmacion, acciones_sugeridas? }` (esquema definitivo en "Implementación (#16)").
- Va en una llamada separada porque algunos proveedores no combinan herramientas con JSON Schema en la misma llamada.
- Además del formato pedido, el JSON se **valida manualmente** contra el esquema.
- Si no valida, se reintenta una vez con un mensaje de corrección. Si vuelve a fallar, se responde un error controlado (`200 {texto, error: true}`, paridad con Python; si las herramientas del turno ya dieron PDF, precio o margen, `RespuestaChat` con esos datos y el texto de respaldo). Nunca llega al flujo de negocio una respuesta sin validar.
- `clsRespuestaAgenteEstructurada` es interna. En `RespuestaChat`, los campos `precio`, `margen` y `pdf` salen de las salidas de las herramientas del turno, nunca del LLM. Del LLM solo sale `texto`.

## Alternativas consideradas

- **Cliente HTTP directo por proveedor:** habría que mapear el tool-calling y los reintentos de cada proveedor a mano.
- **`UseFunctionInvocation()` de MEAI:** invoca las funciones sin pasar por los filtros de SK. Se perderían la inyección de estado y la auditoría, o habría que duplicarlas.
- **Invocación automática de SK (`FunctionChoiceBehavior.Auto`):** sería un segundo camino con otro tipo de filtro (`IAutoFunctionInvocationFilter`). Se descarta para mantener un único camino.
- **Mantener LiteLLM como proxy:** agrega un servicio Python, lo que contradice ADR-001.

## Consecuencias

- Cambiar de proveedor es un cambio de configuración.
- Los tests del runner usan un `IChatClient` guionizado: son deterministas y no consumen tokens.
- La respuesta final cuesta una llamada adicional al modelo por turno. Se acepta a cambio de un camino de validación uniforme.
- Las APIs de SK y MEAI cambian rápido. El impacto se acota con versiones fijas y porque solo `clsChatClientFactory` y `clsKernelBuilder` dependen de ellas.

## Implementación (#12, parte C)

- `Cotifly.Aplicacion/Agente/Services/clsAgenteRunner`: loop manual sobre `IChatClient` hasta `IA:MaxIteraciones` (por defecto 12), sin `UseFunctionInvocation()`. Cada `FunctionCallContent` se ejecuta con `kernel.InvokeAsync`; un nombre inexistente vuelve al modelo como resultado `HERRAMIENTA_DESCONOCIDA`, y una excepción de una herramienta como `ERROR_INTERNO` (red de seguridad, como `_tool_segura` del MCP). Tope superado → `clsMaxIteracionesExcedidasException` (el canal responde el error controlado).
- **Declaración de las herramientas:** en Semantic Kernel 1.80.1 `KernelFunction.AsAIFunction()` está marcada obsoleta ("usa la función directamente") y, usada como `AITool`, la función se expone como `cotizador_<nombre>`. El runner declara al modelo `AIFunctionFactory.CreateDeclaration(nombre, descripción, esquema)` de cada `KernelFunction` del plugin `cotizador`: el modelo ve los nombres del Python (`buscar_tarifa`...) y la ejecución sigue siendo solo `kernel.InvokeAsync`. Es el mismo principio de la decisión (el modelo solo recibe declaraciones), con la API vigente.
- Filtros en el `Kernel` de cada turno: `clsAuditoriaHerramientaFilter` (por fuera, ve también los rechazos) y `clsInyeccionEstadoFilter` (ADR-004). `IAutoFunctionInvocationFilter` no se usa.
- Respuesta final: siempre en una llamada aparte sin herramientas. En #12 el validador era de paso (`clsValidadorRespuestaPasante`, texto libre); #16 lo reemplazó (ver "Implementación (#16)"). El texto del loop se descarta.
- Proveedor: `Cotifly.Infraestructura/IA/Services/clsChatClientFactory` por `IA:Proveedor` = `openai` | `azure_openai` (API v1 de Azure OpenAI con el SDK de OpenAI) | `openai_compatible` (OpenRouter, vLLM...) | `gemini` (endpoint compatible con OpenAI de Google) | `ollama` (endpoint `/v1`) | `anthropic` (SDK oficial `Anthropic` 12.50, con su `IChatClient`). Sin proveedor, `clsChatClientNoConfigurado` (la API arranca; cada turno es el error controlado). Encima: `clsChatClientConRespaldo` (`IA:Fallbacks`, ante 429, 5xx, red o tiempo agotado), `clsLimitadorRpm` (`IA:Rpm`, por defecto 8) y `UseLogging`. Versiones fijas en `Directory.Packages.props` (SK 1.80.1, MEAI 10.10.x, OpenAI 2.14.0, Anthropic 12.50.0; todas MIT).
- Regla 5 (confirmación): `generar_pdf` y `enviar_correo` solo con la confirmación explícita del turno (`clsEstadoConversacionService` la reconoce sobre una previsualización ya mostrada); si no, `CONFIRMACION_REQUERIDA` sin llegar al caso de uso.
- Pruebas: `clsAgenteRunnerTests` y `clsInyeccionEstadoTests` con un `IChatClient` guionizado (`tests/Comun/clsChatClientGuionizado`), `clsProveedoresIATests` (fábrica sin red, respaldo, limitador), `chat_web.feature` y `chat_slot_filling.feature`.

## Implementación (#16, HU-003.10): validador de la respuesta final

Es el equivalente .NET de *Instructor* y forma parte de la arquitectura del agente (documentación técnica de HU-003.8, #14).

**Flujo** (`Cotifly.Aplicacion/Agente/Services/clsValidadorRespuestaAgente`, puerto `IClsValidadorRespuestaAgente.ObtenerRespuestaFinalAsync`):

1. Terminado el loop, `clsAgenteRunner` le pasa el `IChatClient` del turno, la conversación (sin modificarla) y el contexto de validación: estado resuelto, salidas de las herramientas del turno, mensajes del comercial y herramientas declaradas.
2. Llamada final **sin herramientas** con `ChatResponseFormat.ForJsonSchema` (esquema `respuesta_agente`) y una última instrucción de usuario con las reglas. El esquema completo solo va en la instrucción en el modo sin JSON Schema (serializado sin escapar tildes).
3. Validación manual (`clsReglasRespuestaAgente.Validar`, nunca lanza).
4. Si falla: **un** reintento con la respuesta inválida (como mensaje del asistente) y un mensaje de corrección que enumera los fallos.
5. Si vuelve a fallar: respaldo `{tipo: error, texto: clsTextosAgente.RespuestaNoEstructurada}` ("No pude estructurar la respuesta…"). El runner lo guarda como respuesta del turno y lo marca (`clsResultadoTurno.EsRespaldo`). `/web/chat`: si las herramientas del turno produjeron PDF, precio o margen, responde `RespuestaChat` con esos datos y el texto de respaldo (el comercial no pierde el PDF ni lo regenera); si no, `200 RespuestaChatError {texto, error: true}`. Google Chat publica solo el texto, sin botones de control (la tarjeta del PDF, si el turno ya lo generó, sí sale: viene de la herramienta). El respaldo **no** alerta al space de soporte (pendiente).
6. Un fallo del proveedor (429, 5xx, red) y la cancelación se propagan como antes (error controlado del canal). Un JSON inválido nunca lanza.

**Esquema por tipo de interacción** (`clsRespuestaAgenteEstructurada`, generado con `AIJsonUtilities.CreateJsonSchema`: todos los campos requeridos, `acciones_sugeridas` admite `null`, `additionalProperties: false`; se completan `texto.minLength: 1`, `texto.maxLength: 4000`, `acciones_sugeridas.maxItems: 2` y `acciones_sugeridas.uniqueItems: true`):

| Campo | Tipo | Regla |
|---|---|---|
| `tipo` | `conversacional` \| `previsualizacion` \| `confirmacion_requerida` \| `error` | `conversacional`: pregunta o respuesta simple (slot filling). `previsualizacion`: presenta costo, margen y precio de las herramientas y pide confirmación; exige un precio en el estado. `confirmacion_requerida`: pide la confirmación explícita de una acción con efecto (PDF, correo). `error`: explica un error de negocio de una herramienta |
| `texto` | string | 1 a 4000 caracteres sin contar espacios al borde; montos y folios respaldados (abajo) |
| `requiere_confirmacion` | boolean | `true` exactamente con `previsualizacion` y `confirmacion_requerida` |
| `acciones_sugeridas` | lista de `confirmar_pdf` \| `ajustar_margen`, o `null` | Solo con una previsualización pendiente (`ConfirmacionPendiente`) y la herramienta de la acción declarada en el turno (`generar_pdf`, `registrar_ajuste_margen`). En Google Chat se muestran como botones |

No hay campo "herramienta a invocar". El tipo "invocación de herramienta" del ítem 2 del issue lo cubre el **loop**, no el JSON final: function calling nativo del proveedor sobre las declaraciones del plugin `cotizador`, un nombre inexistente responde `HERRAMIENTA_DESCONOCIDA` y los argumentos los valida Semantic Kernel contra el esquema de cada `KernelFunction` (argumentos inválidos → `ERROR_INTERNO` con el aviso de revisar nombres y tipos) antes de que el filtro de inyección de estado (ADR-004) los sobrescriba. La respuesta final no ejecuta nada. `precio`, `margen` y `pdf` **no** están en el esquema: salen de las salidas de las herramientas. Hacia `RespuestaChat` solo sale `texto` (el contrato no cambia).

**Reglas de validación manual** (códigos estables para métricas y logs):

- Forma: JSON completo (`json_invalido`; se toleran las cercas de bloque de código `json`), objeto (`no_es_objeto`), sin campos desconocidos (`campo_desconocido`: un campo o una herramienta alucinados) ni repetidos (`campo_repetido`), campos obligatorios (`campo_faltante`), tipos JSON (`tipo_incorrecto`, p. ej. `"false"` entre comillas), enums (`enum_invalido`, `accion_invalida`), largos (`texto_vacio`, `texto_largo`) y respuesta vacía (`respuesta_vacia`).
- Coherencia: `incoherencia_confirmacion`, `previsualizacion_sin_precio`, `accion_no_disponible`.
- **Montos** (`monto_no_respaldado`, `clsReglasRespuestaAgente.Montos`): es monto todo número con moneda (`$`, `COP`, `USD`, `US$` —también pegados: `COP3.500.000`—, `pesos`, `dólares`, antes o después), con sufijo de millones (`millones`, `millón`, `mill`, `M`, `MM`; "m" minúscula no, puede ser metros), con separadores de miles de 1.000.000 o más (`\d{1,3}([.,]\d{3}){2,}`) aunque no haya palabra clave, o de 1.000 o más en la ventana de 40 caracteres (sin pasar del inicio de la frase) tras "precio", "costo", "utilidad", "tarifa", "valor", "total", "flete", "descuento", "IVA", "cobr…" o "pag…" (se revisan todos los números de la ventana, no solo el primero). No son montos: fechas (`25/09/2026`), años sueltos tras una palabra clave, números tras NIT/tel/teléfono/cel/celular/cédula/CC/folio/llamar/WhatsApp, números seguidos de kg/ton/toneladas/km/%/días/vehículos, y números de 9 cifras o más sin separadores ni moneda (NIT o teléfono). Cada monto debe coincidir, redondeado a pesos, con un número de las salidas de las herramientas del turno (también cadenas de solo dígitos), con el costo, el precio final, la utilidad o el NIT del estado, o con un monto que el comercial escribió **como dinero** (mismas reglas: un "12.500 kg" del comercial no respalda "$12.500"). Sigue siendo una regla práctica: no entiende montos en palabras ("tres millones y medio") y rechaza aproximaciones ("unos 3,3 millones"); el mensaje de corrección le pide al modelo usar los valores tal cual. Batería de 52 textos del validador de #16: falsos positivos 3 → 0, falsos negativos 11 de 24 → 2 (montos en palabras y el folio base de una versión, que se acepta a propósito porque la v1 existe).
- **Folios** (`folio_no_respaldado`): todo `PCN-…` del texto debe aparecer en el estado, en las salidas de las herramientas o en los mensajes del comercial (el folio base de una versión `-vN` se acepta).

**Proveedores sin JSON Schema:** `IA:SoportaJsonSchema` (por defecto `true`). Con `false`, la llamada final va sin `ResponseFormat` y el esquema va en la instrucción; la validación es la misma. Con `true`, si el proveedor rechaza el formato —estado HTTP **400 o 422** (el de `HttpRequestException` o la propiedad `Status`/`StatusCode` de los SDK, p. ej. `ClientResultException.Status`, leído con `clsEstadoHttpExcepcion`, el mismo que usa `clsChatClientConRespaldo`) y un mensaje que nombra `response_format`, `responseFormat`, `json_schema` o "structured output", o una `NotSupportedException` del adaptador sin estado HTTP; nunca un 429, un 5xx ni un fallo de red—, se repite la llamada sin formato, se cuenta en `cotifly.agente.esquema_rechazado` y se sigue sin formato durante `IA:MinutosSinJsonSchema` (por defecto 30); pasado ese tiempo se vuelve a probar con esquema (re-sonda). Aplica a todo el proceso (también a los proveedores de `IA:Fallbacks`).

**Modo estricto:** Microsoft.Extensions.AI.OpenAI 10.10.1 lee `ChatOptions.AdditionalProperties["strict"]` (su `HasStrict`) y lo pasa como `jsonSchemaIsStrict` al SDK de OpenAI; se verificó en el ensamblado (clave `strict`). Se activa con `IA:JsonSchemaEstricto=true` (por defecto `false`): solo tiene efecto con proveedores del SDK de OpenAI (openai, azure_openai, openai_compatible, gemini, ollama; con Anthropic la clave se ignora o podría viajar como parámetro, sin probar), y como el modo estricto de OpenAI no admite `minLength`, `maxLength` ni `uniqueItems`, en ese modo se envía `EsquemaEstricto` sin esas palabras (la validación manual las sigue exigiendo).

**Observabilidad:** medidor `Cotifly.Agente` (creado con `IMeterFactory`; `AddAplicacion` llama a `AddMetrics()`) con los contadores `cotifly.agente.respuesta_final.invalidas` (etiqueta `motivo`), `cotifly.agente.respuesta_final.reintentos`, `cotifly.agente.respuesta_final.respaldos` y `cotifly.agente.esquema_rechazado`. Sin exportador todavía (OpenTelemetry queda para #14/HU-006); para verlos en un contenedor o en local: `dotnet-counters monitor --process-id <pid> --counters Cotifly.Agente`; `Warning` con el intento y los códigos de fallo, nunca el texto de la respuesta (puede traer datos del cliente). `clsResultadoTurno` expone `Respuesta`, `ReintentosRespuesta` y `EsRespaldo`; `Iteraciones` cuenta también las llamadas del reintento.

**Pruebas:** `clsValidadorRespuestaAgenteTests` (esquema con `minLength`/`maxLength`/`maxItems`/`uniqueItems` y su variante estricta; instrucciones con y sin esquema; frases de la batería del validador: NIT, teléfono, NIT con puntos, unidades y fechas que no son montos, y montos sin palabra clave, pegados a la moneda, con `M`/`MM`/millones o después del primero; montos del comercial solo si los escribió como dinero; NIT del estado y cadenas de dígitos; re-sonda tras el TTL; 5xx y 429 que no apagan el esquema; modo estricto; los 3 casos malformados del issue —JSON incompleto, campo con tipo incorrecto, herramienta o campo alucinado— y 20 más; coherencia; montos y folios; reintento que corrige; reintento que falla → respaldo; proveedor sin JSON Schema por configuración y por error en tiempo de ejecución; 429 que se propaga; cancelación; métricas; logs sin el texto), `clsAgenteRunnerTests` (contexto que recibe el validador, respaldo sin excepción, reintento dentro del turno), `clsGoogleChatTests` (botones de las acciones, respaldo sin botones), `clsChatWebEndpointTests` (`/web/chat` de punta a punta: malformada y luego válida; dos malformadas → `RespuestaChatError`; respaldo con precio y margen del turno → `RespuestaChat` con los datos y el texto de respaldo) y `validador_respuesta_ia.feature` (4 escenarios).

**Propuesta (no implementada), pendiente de acordar con frontend:** agregar a `RespuestaChat` un campo opcional y aditivo `interaccion {tipo, requiere_confirmacion, acciones_sugeridas[]}` con la respuesta estructurada, para que el Angular de ClientePro (HU-004.6) muestre los mismos botones de control que Google Chat (Confirmar y generar PDF, Ajustar margen). Es compatible hacia atrás (campo nuevo, opcional), pero cambia `specs/openapi.yaml` (#17) y el contrato con Sara; hasta acordarlo, la web solo recibe `texto`.

**Riesgo con Anthropic (a probar si se usa ese proveedor):** la llamada final va sin herramientas pero la conversación trae los bloques `tool_use`/`tool_result` del loop. La API de Anthropic exige las definiciones de herramientas cuando hay esos bloques en algunos casos; si falla, habría que declarar las herramientas con `ToolMode = None` en la llamada final o resumir los resultados como texto. Sin prueba automática (las pruebas no consumen tokens).

## Implementación (#84, HU-003.6): OmniRoute y esquemas portables

**Esquemas portables de las herramientas.** Las declaraciones que ve el modelo ya no usan el JSON Schema de Semantic Kernel tal cual. `KernelFunction.JsonSchema` (Semantic Kernel 1.80.1, `KernelFunctionFromMethod`) tiene dos problemas:

- pone la descripción de la función en una clave no estándar `condition` en la raíz del esquema;
- expresa los parámetros anulables como un arreglo de tipos (`"type": ["number", "null"]`, por ejemplo `pesoKg`).

`AIFunctionFactory.CreateDeclaration` y el adaptador de OpenAI de Microsoft.Extensions.AI pasan las claves desconocidas sin tocarlas. Por eso Gemini, también detrás de OmniRoute, respondía `400 Invalid JSON payload received. Unknown name "condition" at 'tools[0].function_declarations[0].parameters'`.

`clsEsquemaPortableHerramientas` (Aplicación) reconstruye el esquema con una lista blanca:

- las palabras permitidas son `type`, `properties`, `required`, `description`, `enum`, `items` y `format`, este último solo con `date-time`, `int32`, `int64`, `float` o `double`;
- un campo anulable (arreglo con `null`, `nullable: true`, `anyOf`/`oneOf` con `null` o un `enum` con `null`) queda con su único tipo no nulo y sale de `required`;
- la descripción va a nivel de función, nunca en el esquema.

Nombres, descripciones, tipos y obligatorios no cambian, así que los demás proveedores ven lo mismo que antes. Lo que no tiene forma portable no se degrada en silencio: `$ref`, uniones de varios tipos, tuplas, diccionarios, campos sin tipo, objetos sin propiedades y arreglos sin `items` hacen lanzar a `Declarar`, con la ruta del campo, y la prueba de contrato lo detecta antes de desplegar. Al serializar, el adaptador de OpenAI agrega `"additionalProperties": false` en la raíz. Es parte del contrato de OpenAI y Gemini vía OmniRoute lo acepta (probado el 2026-10-01, igual que los tipos en arreglo). El problema era solo `condition`.

**Payload de `generar_pdf` tipado.** `cotizacion` es un `JsonElement` libre y Semantic Kernel lo declara sin tipo. Con un parámetro sin tipo, Gemini vía OmniRoute arma los objetos anidados como texto (`"textos": "{\"intro\": ...}"`, `"tarifa": "COTIZACIÓN..."`) y el PDF falla con `PDF_PAYLOAD_INVALIDO` hasta agotar `IA:MaxIteraciones`. `clsEsquemaPayloadPdf` declara ese parámetro con el esquema anidado del prompt (paso 5): ciudad, fecha, cliente, contactos, línea, modalidad, `textos.intro`, `comercial.cargo`, `tarifa{titulo, origen, filas[{destino, vehiculo_peso, nota}]}`, aduana, extradimensionada y refrigerada. No incluye `referencia` ni los números, que pone el servidor. La descripción original del parámetro se conserva. Una prueba por reflexión ata el esquema a `clsCotizacionPdfPayload`: cada campo debe existir en snake_case y con un tipo compatible. En la sonda del 2026-10-01 con `gemini/gemini-3.5-flash-lite`, sin tipo el modelo mandó `tarifa` y `comercial` como texto; con el esquema anidado, como objetos. La lectura del payload no cambia.

El contrato lo cubren estas pruebas:

- `clsEsquemaPortableHerramientasTests` recorre las 9 herramientas que recibe el modelo, verifica que se conserve el contenido del esquema de Semantic Kernel y avisa si Semantic Kernel deja de emitir `condition`;
- `clsOpenAiCompatibleTests` revisa el cuerpo HTTP real que sale hacia el proveedor.

**`"stream": false` explícito con `openai_compatible`.** El SDK de OpenAI no envía `stream` en el modo sin streaming, porque la API de OpenAI asume `false`. OmniRoute asume `true` y responde SSE (`text/event-stream`), que el SDK no puede leer como un `chat.completion`. `clsPoliticaSinStreaming` es una `PipelinePolicy` por llamada que solo se registra con `openai_compatible`. Agrega `"stream": false` al cuerpo de `POST .../chat/completions` cuando no lo trae. Una petición en streaming (que ya trae `stream: true`) o un cuerpo que no es un objeto JSON pasan sin cambios. `openai`, `azure_openai`, `gemini` y `ollama` no cambian.

**Respuesta final con texto alrededor.** Gemini vía OmniRoute ignora `response_format: json_schema` y responde prosa con un bloque de código `json`. `clsReglasRespuestaAgente` sigue aceptando primero el JSON completo, con o sin cercas. Si no lo es, reúne los candidatos en orden de aparición: los bloques de código y los objetos `{...}` de primer nivel balanceados, en una pasada lineal (sin costo cuadrático con miles de llaves sin cerrar). Acepta el objeto que cumple toda la forma **solo si es el único distinto**. Con dos o más (un ejemplo citado, un JSON que pegó el comercial) responde `json_invalido` y fuerza el reintento de corrección: así el validador no adivina cuál es la respuesta. Las reglas de negocio (montos, folios, coherencia) se aplican igual a ese objeto. Si ningún objeto cumple la forma, el fallo sigue siendo `json_invalido`, más los fallos del primer objeto embebido para que la corrección los nombre. Con `IA__SoportaJsonSchema=false` es el camino normal para Gemini vía OmniRoute.

**Respaldo y reintentos con OmniRoute.**

- `clsChatClientConRespaldo` pasa al respaldo ante el `400` cuyo `error.message` **empieza** con `No credentials for provider:`. Lo lee del cuerpo crudo de `ClientResultException` o del mensaje. Un 400 que solo cita la frase no cuenta.
- Con `openai_compatible`, `clsPoliticaReintentoGateway` reemplaza la política de reintentos del SDK. No reintenta un `429` con `code: model_cooldown`, ni uno con `reset_seconds` mayor que `IA:TimeoutSeg`, y deja que el respaldo actúe de inmediato. Los 3 reintentos sobre un enfriamiento de horas alargaban el turno 100 a 160 s.
- Los manifiestos declaran `IA__Fallbacks__0__*` y `__1__*` con `${...:-}`. Un respaldo con `Proveedor` vacío no existe (`clsOpcionesIA.FallbacksConfigurados`).

**Modelos con OmniRoute (2026-10-01).**

- **Principal:** `gemini/gemini-3.5-flash-lite`. Es el único que completó la cotización con PDF (folios `-001` y `-002`), el único que respeta `json_schema` y el más rápido.
- **Respaldos:** `gemini/gemini-3.5-flash` y `gemini/gemini-3-flash-preview`. Validaron las herramientas, pero entraron en enfriamiento antes de llegar al precio.

**`IA:MaxTokens`.** El valor por defecto sigue en 4096. Con OpenRouter, 4096 provocó un `402 payment_required`: OpenRouter reserva crédito por el máximo de tokens pedido, aunque la respuesta use muchos menos. Para OmniRoute y OpenRouter se recomienda **1500**, que cubre la respuesta final (`texto` ≤ 4000 caracteres, unos 1000 tokens) y las llamadas a herramientas, incluido el payload de `generar_pdf`. La matriz de proveedores probada está en `docs/03-infraestructura.md` (OmniRoute).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Cuota de OmniRoute: las credenciales de Gemini que expone OmniRoute entran en enfriamiento de unas 9 horas tras 4 a 8 llamadas por modelo (2026-10-01). Antes de producción hace falta una credencial con cuota de pago, o varios modelos en `IA__Fallbacks` | Duban + dueño de OmniRoute |
| Rotar la clave de OmniRoute usada en las pruebas del 2026-10-01 (se compartió por chat) antes de staging; después cada 6 meses o ante cualquier exposición (procedimiento en DEPLOY.md §2.2) | Duban |
| **Riesgo de seguridad:** OmniRoute va por HTTP sin cifrar dentro de la red. Viajan en claro los nombres y NIT de los clientes, los precios, el correo del comercial y la clave `Bearer`. Publicarlo por HTTPS (Traefik con TLS, #25/#26) o, como mínimo, aislar el tráfico worker → manager | Infraestructura + dueño de OmniRoute |
| **Datos y términos:** OmniRoute reenvía a Google, OpenRouter o Groq bajo los términos de la credencial cargada (con una clave gratuita de AI Studio, Google puede usar los datos). Confirmar qué credencial es, la residencia de los datos y si OmniRoute registra los prompts | Negocio + jurídica + dueño de OmniRoute |
| Repetir la conversación de punta a punta con `gemini-3.5-flash` y `gemini-3-flash-preview` cuando haya cuota (criterio 4 de #84: parcial) | Duban |
| Proveedor y modelo de producción; custodia de la API key (secreto en Dokploy, HU-006.5) | Duban + scalderon-bs |
| Registrar el mecanismo en la HU de Notion | Duban |
| Validar el loop con el proveedor real elegido (sin prueba automática: las pruebas no consumen tokens) | Duban |
| Confirmar si el proveedor de producción acepta JSON Schema en `response_format` (si no, `IA__SoportaJsonSchema=false`) y medir la tasa de reintentos y respaldos (`cotifly.agente.respuesta_final.*`) antes de salir a producción | Duban |
| Campo `interaccion` en `RespuestaChat` para los botones en Angular (propuesta de #16) | Duban + Sara (HU-004.6) |
| Alerta al space de soporte cuando la respuesta final termina en el respaldo | Duban |
| Probar la llamada final sin herramientas con Anthropic (bloques `tool_use`/`tool_result`) si se elige ese proveedor | Duban |
