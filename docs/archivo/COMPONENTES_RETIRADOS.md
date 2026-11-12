# Componentes retirados

> **Estado:** histórico · **Dueño:** Duban Guerra (backend)

Registro de los componentes que Cotifly tuvo y retiró. **No describe el sistema actual.** Lo vigente está en
[`backend-dotnet/docs/`](../../backend-dotnet/docs/) y en [`DEPLOY.md`](../../DEPLOY.md). El último estado del código Python está en el
tag de git `python-final`.

## Google Chat y agente cotizador por chat

Hasta HU-013.9 (#105) la sección 2.11 de `backend-dotnet/docs/02-arquitectura.md` describía el agente cotizador legado (`clsAgenteRunner`, plugin `cotizador` con `buscar_cliente`, `buscar_tarifa`, `calcular_precio`, `generar_pdf`, `enviar_correo`..., filtro de inyección y slot filling de [ADR-004](../../backend-dotnet/specs/decisiones/ADR-004-estado-conversacion-slot-filling.md)) y la integración con Google Chat (`POST /chat/events`, verificación del JWT de Google, cola, worker, publicador y tarjetas; [ADR-011](../../backend-dotnet/specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)), ambos de #12 parte C. Los dos se retiraron en HU-013.9: el webhook responde 404, `POST /web/chat` sin `folio` responde 400 `FOLIO_REQUERIDO` y la cotización la arman el formulario, Nexo y el código ([ADR-018](../../backend-dotnet/specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md)). Las tablas `Sesiones` y `EventosConversacion` conservan esas conversaciones como auditoría (las de Google Chat con `spaceId` `spaces/...`).

### Google Chat y enlace firmado del PDF

Google Chat (webhook `POST /chat/events`, verificación del JWT, cola, worker, publicador con cuenta de servicio y opciones `GChat:*`) y el enlace firmado del PDF (`GET /pdfs/{archivo}?t=...`, `Pdf:ClaveFirmaUrl`, `Pdf:MinutosUrlFirmada`, ADR-011 §3), que solo emitía Google Chat, se retiraron en HU-013.9 (#105). El PDF se descarga siempre con Bearer. Si un entorno todavía trae esas variables, la API las ignora.

### Retiro en el despliegue

Decisión del 2026-10-05 (ADR-018 §15): Cotifly es un módulo de formularios dentro de ClientePro y Google Chat sale del
alcance. Desde HU-013.9 el backend .NET **no tiene** el webhook `POST /chat/events` (responde 404), ni el verificador del
JWT de Google, la cola, el worker, el publicador o las tarjetas, ni las opciones `GChat__*` y su validación al arrancar.
El agente cotizador legado y el flag `Chat__CreacionHabilitada` también se retiraron: `POST /web/chat` exige `folio`.

Al desplegar esta versión:

- Quitar del *Environment* de Dokploy las variables `GChat__*`, `Chat__CreacionHabilitada` y `GCHAT_SA_SECRETO` (si
  quedan, la API las ignora y arranca igual, pero no deben confundir).
- Borrar el secreto de Swarm de la cuenta de servicio (`cotifly_gchat_sa_v1`, `cotifly_staging_gchat_sa_v1`) y, en
  Google Cloud, deshabilitar la app de Chat que apuntaba a `https://<PUBLIC_HOST>/chat/events`. Ambas acciones tocan
  infraestructura real: solo con confirmación explícita.
- Las tablas `Sesiones` y `EventosConversacion` **no se borran**: guardan la auditoría de las conversaciones de Google
  Chat (`spaceId = spaces/...`) y siguen alimentándose con el asistente por folio (`web/{sesion_id}`).

La verificación del JWT del webhook (HU-006.7) era del stack Python; su runbook está en el tag `python-final` (sección siguiente).

### Stack Python (tag `python-final`)

El stack Python (orquestador, servidor MCP, `shared/`, Postgres y MinIO) se retiró del repositorio en HU-022.1 (#146),
por decisión del usuario del 2026-10-13: "todo lo referente a python muere en este repo". Ya no es destino de rollback
desde este repositorio.

- **Dónde quedó:** el tag `python-final` guarda el último estado completo: el código, `deploy/docker-compose.swarm.yml`
  (el stack que corre en Dokploy, proyecto `bot-cotizador`, puerto 8091 del worker), `deploy/rebuild-on-worker.sh`,
  `infra/stack.dokploy.yml`, `infra/stack.staging.yml`, los scripts de respaldo y la versión completa de este anexo
  (runbook de la Fase 15). Para consultarlo: `git show python-final:DEPLOY.md`.
- **Lo que sigue desplegado:** el stack Python en Dokploy no se tocó desde aquí. Apagarlo, rotar sus credenciales por
  defecto y deshabilitar la app de Google Chat son acciones de infraestructura con confirmación aparte (`DEPLOY.md` §7).

## Integraciones del Python

| Integración del Python | En .NET | Configuración |
|---|---|---|
| SAP TM, export JSON (`export_fuente.py`) y OData en vivo (`odata_fuente.py`) | ~~`clsFuenteMargenExportJson`, `clsFuenteMargenOData` (#13)~~ retirados en HU-042.2: `clsFuenteMargenTablaLocal` lee la copia local `dbo_cotifly.SapMargenCliente`, que sincroniza el worker `clsWorkerSincronizacionMargenSap` | `SapTm__*`, `ConnectionStrings__DwOperaciones` |
| Tarifario en Google Drive (`DriveFlatFileProvider`) | **No se porta**: retirado el 2026-09-23, el costeo vendrá de Nexo (HU-005.5 en adelante) | ninguna |
| Búsqueda de clientes (no existía en el Python) | `clsBuscadorClientesNexo`, más municipios y catálogos de flete de Nexo (la cubicación de Nexo se retiró en HU-023.2: "Calcular cubicaje" usa el cubicador, `Cubicador__UrlBase`) | `Nexo__UrlBase` ([Nexo](../../backend-dotnet/docs/03-infraestructura.md#nexo)) |
| Sincronización a Google Sheets (Looker Studio) | **Diferida**, pendiente en ADR-012 (Looker directo a SQL Server, port a .NET o tablero de HU-010) | ninguna |
| PDF en S3/MinIO (`almacen_pdf.py`) | **Diferido**: se mantiene el volumen `/data/pdfs` detrás de `IClsAlmacenPdf` (#11). S3 solo si HU-006.6 despliega varias réplicas sin volumen compartido (ADR-012) | ninguna |

## Variables del stack Python

Variables del Python que **no** pasan al backend .NET (se retiran; no cargarlas en el stack nuevo):
`DATABASE_URL`, `POSTGRES_*`, `MSSQL_SA_PASSWORD`, `MCP_URL`, `MCP_AUTH_TOKEN` (no hay MCP aparte, ADR-002),
`WEB_SESSION_SECRET` (reemplazada por `Jwt__SigningKey`), `INTERNAL_API_TOKEN`, `LLM_MODEL`/`LLM_API_KEY`/`LLM_RPM`
(reemplazadas por `IA__*`), `GCHAT_*` y `GOOGLE_SA_JSON` (Google Chat retirado en #105), y `DRIVE_TARIFARIO_*` (tarifario
de Drive retirado), `TARIFA_*`, `S3_*` y `MINIO_*` (MinIO no se mantiene, ADR-012), `SHEETS_*` (diferida, ADR-012),
`SMTP_*`/`SMTP_RELAY_*`/`CORREO_*` (reemplazadas por `Correo__*`), `SAP_TM_*`/`SAP_SALES_ORG_DEFAULT` (reemplazadas por
`SapTm__*`: `SalesOrgDefault`, `AvisoSincronizacionHoras` y, desde el 2026-11-03, `Url`, `User`, `Pass`, `SapClient` y los ajustes del worker), `PDF_*` (reemplazadas por `Pdf__*`), `PUBLIC_HOST` (sigue como variable del manifiesto para Traefik, no
de la app).
