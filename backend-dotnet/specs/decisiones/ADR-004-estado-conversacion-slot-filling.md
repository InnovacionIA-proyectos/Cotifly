# ADR-004 — Estado de conversación estructurado (slot-filling)

- **Estado:** Aceptada (las columnas nuevas en BD dependen del script 0002, ver Pendientes)
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

**Columnas nuevas** en `dbo_cotifly.Sesiones`, vía el script `migrations_mssql/0002_identidad_estado_agente.sql` (ADR-008):

- `estadoAgente` JSON, con `CHECK (estadoAgente IS NULL OR ISJSON(estadoAgente) = 1)`;
- `usuarioId` nullable, con FK a `UsuariosWeb`.

El DDL definitivo queda en el script.

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
