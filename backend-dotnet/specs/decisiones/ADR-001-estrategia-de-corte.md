# ADR-001 — Estrategia de corte: big-bang

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Impacta a HU-004 y HU-006.
- **Actualización 2026-10-13 (HU-022.1, #146):** por decisión del usuario ("todo lo referente a python muere en este
  repo") el stack Python se retiró del repositorio, incluida la carpeta `deploy/`. Ya no hay rollback al Python desde
  aquí: el último estado del Python vive en el tag `python-final`. Las rutas del Python que cita esta ADR quedan como historia.

## Contexto

El issue #7 pide elegir entre dos estrategias para pasar del backend Python (`orquestador/` + `mcp_server/` + `shared/`) al backend .NET 10:

- *Big-bang*: se apaga Python y se enciende .NET de una sola vez.
- *Incremental / strangler*: se migra módulo por módulo, con ambos backends conviviendo y el frontend apuntando a uno u otro por configuración.

Hechos que pesan en la decisión:

- El código Python solo funciona contra PostgreSQL (JSONB, ARRAY, UUID, `make_interval`, `COUNT FILTER`). El esquema objetivo ya vive en SQL Server 2019 (`dbo_cotifly`, HU-002.4) y nada del Python corre contra él. Convivir obligaría a reescribir la capa de datos Python, trabajo que se tiraría al terminar.
- No se migran datos históricos (HU-002.4), así que no hay estado de producción que sincronizar entre dos backends.
- `/web/login`, `/web/mis-cotizaciones`, `/enviar` y `/seguimiento` del Python responden 501 desde la migración 0006 (HU-002.2). El Angular de HU-004 no tiene nada del Python que valga la pena consumir mientras se migra.
- Despliegue actual (`deploy/docker-compose.swarm.yml`): solo se publica `bot-orquestador` (puerto 8091, `mode: host`, sin TLS). `bot-mcp` no publica puertos y solo lo consume el orquestador (`MCP_URL`).
- Superficie pública del orquestador Python hoy:
  - webhook de Google Chat `POST /chat/events`;
  - `/pdfs/*`;
  - consola `GET /web`, que sirve `orquestador/static/chat.html`, la única UI web con `/web/chat` operativo;
  - rutas de soporte `GET /sesiones/{space_id}` y `POST /internal/sync-sheets`;
  - sincronización saliente a Google Sheets (Looker Studio).

## Decisión

**Corte único (big-bang).** Todo el backend se reescribe en .NET 10 y reemplaza al Python en un solo paso, en el despliegue de HU-006.

1. **Python queda intacto** en el repo y en producción hasta que el backend .NET esté validado en producción. No se le agrega ni se le corrige nada. Su retiro definitivo del repo es una tarea posterior (HU-003, alcance punto 5).
2. **HU-004 (Angular dentro de ClientePro) apunta solo al backend .NET**, con el contrato congelado en `backend-dotnet/specs/openapi.yaml` (#17). El frontend no tiene ningún conmutador de backend.
3. **El webhook de Google Chat se reapunta al .NET** (misma ruta `/chat/events`) durante el despliegue de HU-006. La verificación del JWT del webhook en producción (hoy `GCHAT_VERIFICAR_JWT=false`) se resuelve en HU-006.7.
4. **Exposición de `mcp_server` / `orquestador` durante la convivencia** (cierra el ítem abierto de #7):
   - Mientras se desarrolla y valida .NET, el Python sigue expuesto igual que hoy: solo el orquestador en el puerto 8091 y el MCP como servicio interno. Así no se rompe el webhook de Google Chat.
   - El backend .NET se prueba en paralelo en un entorno de QA separado, sin tráfico real de Google Chat.
   - **Tras el corte, ni `orquestador` ni `mcp_server` quedan expuestos:** se detienen sus servicios en Dokploy. El MCP deja de existir como servicio y sus herramientas pasan a ser módulos internos del monolito .NET (decisión ya tomada en HU-003).
5. **Qué pasa con cada ruta del Python al apagarlo:**

   | Ruta o función del Python | Destino tras el corte |
   |---|---|
   | `chat.html` (consola `/web`) | Desaparece. La reemplaza el Angular de HU-004 (ver Consecuencias) |
   | `/sesiones/{space_id}` | No se porta en esta HU (soporte interno). Si se necesita, se agrega en un issue aparte |
   | `/internal/sync-sheets` y sincronización a Sheets | Diferidos. HU-003.7 (#13) no los porta: el destino del tablero queda pendiente en ADR-012 |

## Alternativas consideradas

- **Strangler con enrutamiento por módulo:** descartado. Exige que el Python hable SQL Server o mantener dos bases sincronizadas. Además obliga a sostener dos autenticaciones (el token HMAC propio del Python, que hoy no tiene emisor porque `/web/login` responde 501, y el SSO de ClientePro) y dos contratos. Es demasiado costo para una transición sin datos productivos.
- **Strangler solo para el chat:** descartado, porque el loop agéntico también se migra (decisión ya marcada en #7).

## Consecuencias

- La validación previa al corte tiene que ser exhaustiva: paridad QA de HU-003.9 (Kevin, 10 casos o más), regresión manual de HU-003.8 y prueba punta a punta del Angular contra .NET (HU-003.6).
- **Al apagar Python desaparece `chat.html`.** El corte exige una de dos cosas:
  - que el Angular de HU-004 esté en producción al mismo tiempo;
  - o aceptar un intervalo en el que solo funcione Google Chat.

  Lo decide negocio.
- **Rollback (reemplazado el 2026-10-13, HU-022.1: el Python ya no está en el repositorio; ver el tag `python-final`):** se reapuntaba el webhook de Google Chat al orquestador Python y se reactivan sus servicios. Lo escrito en SQL Server mientras .NET estuvo arriba no será visible para Python, que usa su PostgreSQL. Es aceptable porque no hay datos productivos que migrar, pero hay que comunicarlo en HU-006.
- El Angular de HU-004 no puede salir a producción antes que el backend .NET.
- Secuenciación con HU-005: el tarifario de Drive se retira (HU-003.4) y `buscar_tarifa` queda como stub hasta que llegue el costeo de Nexo. Si HU-004 sale antes que HU-005, la "Nueva cotización" no tendrá fuente de costeo (riesgo ya registrado en HU-003.4).
- **D8 (fuente de roles, ADR-009) bloquea el login.** Por lo tanto también bloquea la salida de HU-004 y el corte. Se escala aquí como bloqueo del corte.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Elegir entre Angular (HU-004) en producción el mismo día del corte o un intervalo solo con Google Chat | Negocio |
| Decidir la salida de HU-004 frente a HU-005 (vacío de costeo) | Negocio + scalderon-bs |
| Resolver D8, que bloquea login, HU-004 y el corte | Duban + equipo ClientePro (scalderon-bs) |
| Procedimiento para reapuntar el webhook de Google Chat y ventana de corte | Duban (HU-006) |
| Registrar esta decisión en la HU de Notion | Duban |
