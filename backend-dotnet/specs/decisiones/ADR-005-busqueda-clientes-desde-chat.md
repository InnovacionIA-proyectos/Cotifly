# ADR-005 — Búsqueda real de clientes desde el chat

- **Estado:** Aceptada (depende de HU-005.2). El uso de Grovity para validar clientes (punto 3) queda como **Propuesta**.
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Relacionada con #12 (HU-003.6), HU-005.2 y HU-005.10 (repo NEXO).

## Contexto

Hoy, si el usuario escribe "cotiza para Falabella", el LLM copia ese texto al campo del cliente sin validarlo contra ningún catálogo. Cotifly no tiene tabla de clientes.

HU-005.2 construye el buscador de clientes consumiendo el endpoint de Nexo (`/captura-solicitudes/clientes` u otro):

- Combina `DimBpSolicitante` (clientes) y `tblProspectos` (prospectos). Esa ampliación la hace el equipo de Nexo en HU-005.10.
- Según el backlog (HU-002.4, Fase 2), responde `{clienteId, bp, nit, nombre, origen}`.

HU-005.2 también permite consumir la API Grovity de ClientePro (`docs/Manual de Conexión ClientePro - Grovity (1).pdf`), pero **solo** con `ObtenerCiudades`, `ObtenerTipoServicio` y `CrearProspecto`.

## Decisión

1. **Herramienta `buscar_cliente(texto)`** en el módulo `Clientes`, detrás del puerto `IClsBuscadorClientes`. Devuelve `{ exacta: bool, candidatos: [{ nitCliente, nombre, origenCliente, bp? }] }`.
   - `bp` es nullable: los prospectos no tienen BP.
   - Solo se guarda en el estado de conversación, para el margen SAP (ADR-004).
2. **Mismo catálogo que el formulario.** La implementación real consume el mismo endpoint de Nexo que HU-005.2, de modo que el chat y "Nueva cotización" resuelven clientes contra la misma fuente.
3. **Grovity desde el agente:**
   - Solo con las transacciones que permite HU-005.2.
   - `CrearProspecto` es un flujo del formulario, no del agente.
   - Credenciales solo en el backend (secretos, HU-006.5); el token de 24 h se cachea en el servidor.
   - *Propuesta, pendiente de aprobación (sería una desviación de HU-005.2):* usar `ValidacionNIT` e `InfoClienteActual` / `InfoClienteProspecto` cuando el texto del usuario sea un NIT.
4. **Nunca texto libre.** El texto del usuario es solo un término de búsqueda. `nitCliente`, `origenCliente` y `bp` se llenan únicamente desde un resultado del catálogo, a través del estado (ADR-004). Si la coincidencia no es exacta o hay varias, se pide confirmación.
5. **Stub hasta HU-005.2.** Mientras no exista el endpoint, `clsBuscadorClientesNoDisponible` responde `CLIENTE_BUSQUEDA_NO_DISPONIBLE`, código ya declarado en `openapi.yaml`. El agente sigue sin cliente resuelto:
   - `nitCliente = NULL`;
   - `clienteNombre` guarda el texto del usuario solo como referencia visible;
   - el margen sale de `politica_linea`.

## Alternativas consideradas

- **Tabla propia de clientes en Cotifly:** duplicaría el maestro que ya mantienen SAP (vía Nexo) y ClientePro.
- **Consultar `DimBpSolicitante` / `tblProspectos` por base de datos:** acopla Cotifly a esquemas ajenos. El equipo decidió consumir el endpoint de Nexo.
- **Que el LLM "normalice" el nombre:** viola ADR-003.

## Consecuencias

- Un mismo cliente queda con el mismo NIT, venga la cotización del chat o del formulario.
- Hasta HU-005.2, las cotizaciones del chat no tendrán `nitCliente`. Es un comportamiento visible y documentado, no un dato inventado.
- Si Nexo no está disponible, el agente responde un error controlado, nunca texto libre.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Contrato exacto del endpoint de Nexo (campos, criterio de coincidencia exacta frente a similitud, autenticación entre sistemas) | Duban + equipo Nexo (HU-005.2 / HU-005.10) |
| Aprobar o descartar el uso de `ValidacionNIT` / `InfoCliente*` desde el agente | Duban + scalderon-bs |
| Credenciales de servicio de Grovity y URL por ambiente | Duban + scalderon-bs |
