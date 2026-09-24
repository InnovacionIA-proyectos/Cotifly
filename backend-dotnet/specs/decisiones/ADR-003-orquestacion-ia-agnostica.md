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

- Terminado el loop de herramientas, la respuesta final se pide en **una llamada aparte, sin herramientas**, con `ChatResponseFormat.ForJsonSchema` sobre `clsRespuestaAgenteEstructurada { tipo: conversacional | previsualizacion | error, texto, requiereConfirmacion, resumen? }`.
- Va en una llamada separada porque algunos proveedores no combinan herramientas con JSON Schema en la misma llamada.
- Además del formato pedido, el JSON se **valida manualmente** contra el esquema.
- Si no valida, se reintenta una vez con un mensaje de corrección. Si vuelve a fallar, se responde un error controlado (`200 {texto, error: true}`, paridad con Python).
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

## Pendientes

| Pendiente | Dueño |
|---|---|
| Proveedor y modelo de producción; custodia de la API key (secreto en Dokploy, HU-006.5) | Duban + scalderon-bs |
| Registrar el mecanismo en la HU de Notion | Duban |
